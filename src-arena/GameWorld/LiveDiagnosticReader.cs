namespace eft_dma_radar.Arena.GameWorld
{
    /// <summary>
    /// Serializes and tracks on-demand diagnostics that perform live DMA reads.
    /// A match transition drains the tracked task before another LocalGameWorld
    /// may be created, so F8/F9 cannot overlap root refresh or the next match.
    /// </summary>
    internal static class LiveDiagnosticReader
    {
        private static readonly object Sync = new();
        private static Task? _activeTask;
        private static string _activeName = string.Empty;
        private static int _activeSynchronousReaders;

        internal static bool TryRun(LocalGameWorld game, string name, Action action)
        {
            ArgumentNullException.ThrowIfNull(game);
            ArgumentNullException.ThrowIfNull(action);

            lock (Sync)
            {
                if (!game.InMatch || !ReferenceEquals(Memory.CurrentGameWorld, game))
                {
                    Log.WriteLine($"[LiveDiagnostic] '{name}' skipped — match is stopping.");
                    return false;
                }
                if (_activeTask is { IsCompleted: false })
                {
                    Log.WriteLine(
                        $"[LiveDiagnostic] '{name}' skipped — '{_activeName}' is still running.");
                    return false;
                }
                if (_activeSynchronousReaders != 0)
                {
                    Log.WriteLine(
                        $"[LiveDiagnostic] '{name}' skipped — a synchronous live reader is active.");
                    return false;
                }

                _activeName = name;
                _activeTask = Task.Run(() =>
                {
                    try
                    {
                        if (game.InMatch)
                            action();
                    }
                    catch (Exception ex)
                    {
                        Log.WriteLine(
                            $"[LiveDiagnostic] '{name}' failed: {ex.GetType().Name}: {ex.Message}");
                    }
                    finally
                    {
                        Log.WriteLine($"[LiveDiagnostic] '{name}' finished.");
                    }
                });
                return true;
            }
        }

        internal static bool Drain(TimeSpan timeout)
        {
            var sw = Stopwatch.StartNew();
            Task? task;
            string name;
            lock (Sync)
            {
                task = _activeTask;
                name = _activeName;
            }

            if (task is not null && !task.IsCompleted)
            {
                Log.WriteLine($"[LiveDiagnostic] Waiting for '{name}' to leave the live DMA path...");
                if (!task.Wait(timeout))
                {
                    Log.WriteLine($"[LiveDiagnostic] '{name}' did not finish in time.");
                    return false;
                }
            }

            lock (Sync)
            {
                bool waitIndefinitely = timeout == Timeout.InfiniteTimeSpan;
                while (_activeSynchronousReaders != 0)
                {
                    TimeSpan remaining = waitIndefinitely
                        ? Timeout.InfiniteTimeSpan
                        : timeout - sw.Elapsed;
                    if (!waitIndefinitely && remaining <= TimeSpan.Zero)
                    {
                        Log.WriteLine(
                            $"[LiveDiagnostic] {_activeSynchronousReaders} synchronous reader(s) did not drain in time.");
                        return false;
                    }
                    Monitor.Wait(Sync, remaining);
                }
            }
            return true;
        }

        /// <summary>
        /// Runs a short synchronous UI diagnostic under the same match reader
        /// drain used by F8/F9. The stop flag closes admission before Dispose
        /// waits, eliminating the check-to-scatter transition race.
        /// </summary>
        internal static bool TryExecute(LocalGameWorld game, Action action)
        {
            ArgumentNullException.ThrowIfNull(game);
            ArgumentNullException.ThrowIfNull(action);

            lock (Sync)
            {
                if (!game.InMatch || !ReferenceEquals(Memory.CurrentGameWorld, game))
                    return false;
                if (_activeTask is { IsCompleted: false })
                    return false;
                _activeSynchronousReaders++;
            }

            try
            {
                action();
                return true;
            }
            finally
            {
                lock (Sync)
                {
                    _activeSynchronousReaders--;
                    if (_activeSynchronousReaders == 0)
                        Monitor.PulseAll(Sync);
                }
            }
        }
    }
}
