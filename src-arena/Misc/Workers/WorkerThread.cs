namespace eft_dma_radar.Arena.Misc.Workers
{
    internal sealed class WorkerThread : IDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        private bool _started;
        private Thread? _thread;
        private int _ctsDisposed;

        public event Action<CancellationToken>? PerformWork;
        public TimeSpan SleepDuration { get; init; } = TimeSpan.Zero;
        public ThreadPriority ThreadPriority { get; init; } = ThreadPriority.Normal;
        public string Name { get; init; } = "WorkerThread";
        public WorkerSleepMode SleepMode { get; init; } = WorkerSleepMode.Default;

        public void Start()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (Interlocked.Exchange(ref _started, true) == false)
            {
                var thread = new Thread(Worker) { IsBackground = true, Priority = ThreadPriority, Name = Name };
                _thread = thread;
                thread.Start();
            }
        }

        private void Worker()
        {
            Log.WriteLine($"[WorkerThread] '{Name}' starting...");
            try
            {
                bool shouldSleep = SleepDuration > TimeSpan.Zero;
                bool dynamicSleep = shouldSleep && SleepMode == WorkerSleepMode.DynamicSleep;
                var ct = _cts.Token;
                while (!ct.IsCancellationRequested)
                {
                    long start = dynamicSleep ? Stopwatch.GetTimestamp() : default;
                    try { PerformWork?.Invoke(ct); }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                    catch (Exception ex)
                    {
                        Log.WriteRateLimited(AppLogLevel.Warning, $"worker_{Name}", TimeSpan.FromSeconds(5),
                            $"[WorkerThread] '{Name}' error: {ex.GetType().Name}: {ex.Message}");
                    }
                    finally
                    {
                        if (!ct.IsCancellationRequested)
                        {
                            if (dynamicSleep)
                            {
                                var elapsed = Stopwatch.GetElapsedTime(start);
                                var remaining = SleepDuration - elapsed;
                                if (remaining > TimeSpan.Zero) Thread.Sleep(remaining);
                            }
                            else if (shouldSleep) Thread.Sleep(SleepDuration);
                        }
                    }
                }
            }
            finally
            {
                Log.WriteLine($"[WorkerThread] '{Name}' stopped.");
                Volatile.Write(ref _thread, null);
                DisposeCancellationSourceOnce();
            }
        }

        private bool _disposed;
        public bool StopTimedOut { get; private set; }

        public bool WaitForStop(TimeSpan timeout)
        {
            var thread = Volatile.Read(ref _thread);
            if (thread is null || !thread.IsAlive)
            {
                DisposeCancellationSourceOnce();
                StopTimedOut = false;
                return true;
            }
            if (thread == Thread.CurrentThread)
                return false;

            bool stopped = thread.Join(timeout);
            StopTimedOut = !stopped;
            if (stopped)
            {
                Volatile.Write(ref _thread, null);
                DisposeCancellationSourceOnce();
            }
            return stopped;
        }

        public void RequestStop()
        {
            if (Interlocked.Exchange(ref _disposed, true) == false)
            {
                _cts.Cancel();
                PerformWork = null;
            }
        }

        public void Dispose()
        {
            RequestStop();

            var thread = Volatile.Read(ref _thread);
            if (thread is not null && thread != Thread.CurrentThread
                && !WaitForStop(TimeSpan.FromSeconds(5)))
                Log.WriteLine($"[WorkerThread] '{Name}' did not stop within 5s; handle retained for a later drain.");
        }

        private void DisposeCancellationSourceOnce()
        {
            if (Volatile.Read(ref _disposed)
                && Interlocked.Exchange(ref _ctsDisposed, 1) == 0)
                _cts.Dispose();
        }
    }

    internal enum WorkerSleepMode { Default, DynamicSleep }
}
