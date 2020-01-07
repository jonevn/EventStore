using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Threading.Tasks;
using EventStore.Common.Log;
using EventStore.Common.Utils;
using EventStore.Core.Services.Monitoring.Stats;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Analysis;
using Microsoft.Diagnostics.Tracing.Parsers;

namespace EventStore.Core.Services.Monitoring.Utils {
	internal class PerfCounterHelper : IDisposable {
		private static readonly ILogger Log = LogManager.GetLoggerFor<PerfCounterHelper>();
		private const int InvalidCounterResult = -1;

		private readonly List<EventPipeProvider> _providers;
		private DiagnosticsClient _client;
		private EventPipeSession _session;
		private readonly IDictionary<string, double> _collectedStats = new Dictionary<string, double>();

		private readonly ILogger _log;

		private readonly PerformanceCounter _totalCpuCounter;
		private readonly PerformanceCounter _totalMemCounter; //doesn't work on mono
		private readonly int _pid;

		public PerfCounterHelper(ILogger log, long collectIntervalInMs) {
			_log = log;

			var currentProcess = Process.GetCurrentProcess();
			_pid = currentProcess.Id;

			_providers = new List<EventPipeProvider> {
				new EventPipeProvider("System.Runtime", EventLevel.Informational,
					(long)ClrTraceEventParser.Keywords.All, new Dictionary<string, string> {
						{"EventCounterIntervalSec", $"{collectIntervalInMs / 1000}"}
					}),
				new EventPipeProvider("Microsoft-Windows-DotNETRuntime", EventLevel.Informational,
					(long)ClrTraceEventParser.Keywords.All, new Dictionary<string, string>())
			};

			_totalCpuCounter = CreatePerfCounter("Processor", "% Processor Time", "_Total");
			_totalMemCounter = CreatePerfCounter("Memory", "Available Bytes");
		}

		public void Start() {
			Task.Run(() => {
				_client = new DiagnosticsClient(_pid);
				_session = _client.StartEventPipeSession(_providers, false);
				using var source = new EventPipeEventSource(_session.EventStream);
				
				source.NeedLoadedDotNetRuntimes();
				source.AddCallbackOnProcessStart(proc =>
				{
					if (proc.ProcessID != _pid)
						return;

					proc.AddCallbackOnDotNetRuntimeLoad(runtime =>
					{
						runtime.GCEnd += (process, gc) =>
						{
							_collectedStats[$"gen-{gc.Generation}-gc-collection-count"] = gc.Number;
						};
					});
				});

				source.Dynamic.All += obj => {
					if (obj.EventName.Equals("EventCounters")) {
						var payload = (IDictionary<string, object>)obj.PayloadValue(0);
						var pairs = (IDictionary<string, object>)(payload["Payload"]);

						var name = string.Intern(pairs["Name"].ToString());

						var counterType = pairs["CounterType"];
						if (counterType.Equals("Sum")) {
							_collectedStats[name] = double.Parse(pairs["Increment"].ToString());
						}

						if (counterType.Equals("Mean")) {
							_collectedStats[name] = double.Parse(pairs["Mean"].ToString());
						}
					}
				};
				try {
					source.Process();
				} catch (Exception exception) {
					Log.WarnException(exception, "Error encountered while processing events");
				}
			});
		}

		private PerformanceCounter CreatePerfCounter(string category, string counter, string instance = null) {
			try {
				return string.IsNullOrEmpty(instance)
					? new PerformanceCounter(category, counter)
					: new PerformanceCounter(category, counter, instance);
			} catch (Exception ex) {
				_log.Trace(
					"Could not create performance counter: category='{category}', counter='{counter}', instance='{instance}'. Error: {e}",
					category, counter, instance ?? string.Empty, ex.Message);
				return null;
			}
		}

		private string GetProcessInstanceName(string categoryName, string counterName) {
			// On Unix or MacOS, use the PID as the instance name
			if (Runtime.IsUnixOrMac) {
				return _pid.ToString();
			}

			// On Windows use the Performance Counter to get the name
			try {
				if (PerformanceCounterCategory.Exists(categoryName)) {
					var category = new PerformanceCounterCategory(categoryName).ReadCategory();

					if (category.Contains(counterName)) {
						var instanceDataCollection = category[counterName];

						if (instanceDataCollection.Values != null) {
							foreach (InstanceData item in instanceDataCollection.Values) {
								var instancePid = (int)item.RawValue;
								if (_pid.Equals(instancePid)) {
									return item.InstanceName;
								}
							}
						}
					}
				}
			} catch (InvalidOperationException) {
				_log.Trace("Unable to get performance counter category '{category}' instances.", categoryName);
			}

			return null;
		}

		///<summary>
		///Total CPU usage in percentage
		///</summary>
		public float GetTotalCpuUsage() {
			return _totalCpuCounter?.NextValue() ?? InvalidCounterResult;
		}

		///<summary>
		///Free memory in bytes
		///</summary>
		public long GetFreeMemory() {
			return _totalMemCounter?.NextSample().RawValue ?? InvalidCounterResult;
		}

		///<summary>
		///Total process CPU usage
		///</summary>
		public float GetProcCpuUsage() {
			return (float)GetCounterValue("cpu-usage");
		}

		///<summary>
		///Current thread count
		///</summary>
		public int GetProcThreadsCount() {
			return (int)GetCounterValue("threadpool-thread-count");
		}

		///<summary>
		///Number of exceptions thrown per second
		///</summary>
		public float GetThrownExceptionsRate() {
			return (float)GetCounterValue("exception-count");
		}

		///<summary>
		///The rate at which threads in the runtime attempt to acquire a managed lock unsuccessfully
		///</summary>
		public float GetContentionsRateCount() {
			return (float)GetCounterValue("monitor-lock-contention-count");
		}

		private double GetCounterValue(string name) {
			if (!_collectedStats.TryGetValue(name, out var value)) return InvalidCounterResult;
			return value;
		}

		public GcStats GetGcStats() {
			return new GcStats(
				gcAllocationSpeed: (float)GetCounterValue("alloc-rate"),
				gcGen0Items: (long)GetCounterValue("gen-0-gc-collection-count"),
				gcGen0Size: (long)GetCounterValue("gen-0-size"),
				gcGen1Items: (long)GetCounterValue("gen-1-gc-collection-count"),
				gcGen1Size: (long)GetCounterValue("gen-1-size"),
				gcGen2Items: (long)GetCounterValue("gen-2-gc-collection-count"),
				gcGen2Size: (long)GetCounterValue("gen-2-size"),
				gcLargeHeapSize: (long)GetCounterValue("loh-size"),
				gcTimeInGc: (long)GetCounterValue("time-in-gc"),
				gcTotalBytesInHeaps: (long)GetCounterValue("gc-heap-size") * 1024 * 1024);
		}

		public void Dispose() {
			_session?.Dispose();
			_totalCpuCounter?.Dispose();
			_totalMemCounter?.Dispose();
		}
	}
}
