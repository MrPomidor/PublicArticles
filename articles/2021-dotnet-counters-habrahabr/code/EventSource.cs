using System.Diagnostics.Tracing;

namespace TestApplication.Monitoring
{
    [EventSource(Name = "TestApplication.Tracing.WorkerEventSource")] // <- unique
    internal class WorkerEventSource : EventSource
    {
        public static readonly WorkerEventSource Log = new WorkerEventSource(); // <- entrypoint for using counters in application

        private long _workInProgressItems = 0;

        private EventCounter _processingTimeCounter;
        private PollingCounter _workInProgressCounter;

        private WorkerEventSource() { }

        public void WorkItemAdded()
        {
            Interlocked.Increment(ref _workInProgressItems);
        }

        public void WorkItemCompleted(long elapsedMilliseconds)
        {
            _processingTimeCounter?.WriteMetric(elapsedMilliseconds);
            Interlocked.Decrement(ref _workInProgressItems);
        }

        protected override void OnEventCommand(EventCommandEventArgs command)
        {
            if (command.Command == EventCommand.Enable)
            {
                _processingTimeCounter ??= new EventCounter("unit-processing-time", this)
                {
                    DisplayName = "Unit of work processing time",
                    DisplayUnits = "milliseconds"
                };
                _workInProgressCounter ??= new PollingCounter("worker-queue-length", this, () => Volatile.Read(ref _workInProgressItems))
                {
                    DisplayName = "Worker queue length",
                    DisplayUnits = "items"
                };
            }
        }

        protected override void Dispose(bool disposing)
        {
            _processingTimeCounter?.Dispose();
            _processingTimeCounter = null;

            _workInProgressCounter?.Dispose();
            _workInProgressCounter = null;

            base.Dispose(disposing);
        }
    }
}
