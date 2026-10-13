namespace eft_dma_radar.Arena.Misc
{
    public enum AppLogLevel { Debug, Info, Warning, Error }

    public static class Log
    {
        private static StreamWriter? _fileWriter;
        private static bool _consoleAllocated;
        private static readonly Lock _writeLock = new();
        private static ConsoleColor _currentColor = ConsoleColor.Gray;
        private const int MaxRetainedSessionLogs = 20;

        public static string LogDirectory { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "eft-dma-radar-arena",
            "logs");

        public static string? CurrentFilePath { get; private set; }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AllocConsole();
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern nint GetConsoleWindow();

        static Log()
        {
            AllocateConsole();
            OpenSessionLog();
            AppDomain.CurrentDomain.ProcessExit += (_, _) => Close();
        }

        private static void OpenSessionLog()
        {
            try
            {
                var disable = Environment.GetEnvironmentVariable("ARENA_DISABLE_FILE_LOG");
                if (disable is "1" || string.Equals(disable, "true", StringComparison.OrdinalIgnoreCase))
                    return;

                Directory.CreateDirectory(LogDirectory);
                CleanupOldSessionLogs();

                string fileName = $"arena-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Environment.ProcessId}.log";
                CurrentFilePath = Path.Combine(LogDirectory, fileName);
                var fs = new FileStream(
                    CurrentFilePath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.ReadWrite,
                    0x1000,
                    FileOptions.SequentialScan);
                _fileWriter = new StreamWriter(fs, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), 0x1000)
                {
                    AutoFlush = true,
                };
            }
            catch (Exception ex)
            {
                CurrentFilePath = null;
                Debug.WriteLine($"Failed to open Arena session log: {ex.Message}");
            }
        }

        private static void CleanupOldSessionLogs()
        {
            try
            {
                var stale = new DirectoryInfo(LogDirectory)
                    .EnumerateFiles("arena-*.log", SearchOption.TopDirectoryOnly)
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .Skip(MaxRetainedSessionLogs - 1)
                    .ToArray();
                foreach (var file in stale)
                {
                    try { file.Delete(); }
                    catch { }
                }
            }
            catch { }
        }

        private static void AllocateConsole()
        {
            if (_consoleAllocated) return;
            try
            {
                if (GetConsoleWindow() == 0)
                {
                    if (AllocConsole())
                    {
                        Console.OutputEncoding = Encoding.UTF8;
                        Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), Encoding.UTF8) { AutoFlush = true });
                        Console.SetError(new StreamWriter(Console.OpenStandardError(), Encoding.UTF8) { AutoFlush = true });
                        Console.Title = "Arena DMA Radar — Debug Console";
                        _consoleAllocated = true;
                    }
                }
                else { _consoleAllocated = true; }
            }
            catch (Exception ex) { Debug.WriteLine($"Failed to allocate console: {ex.Message}"); }
        }

        public static AppLogLevel MinimumLogLevel { get; set; } = AppLogLevel.Info;
        public static bool EnableDebugLogging { get; set; } = false;

        /// <summary>
        /// Gates the IL2CPP dumper output (per-player <c>DumpClassFields</c> floods at discovery,
        /// transform reinit, skeleton init, etc.). Separated from <see cref="EnableDebugLogging"/>
        /// because the aimview stability metrics and other debug-level traces are useful day-to-day,
        /// while the dumper output is only wanted when actively diagnosing IL2CPP layout drift
        /// after a game update. Enable via <c>il2cppDump=true</c> in config or <c>-il2cppdump</c>.
        /// </summary>
        public static bool EnableIl2CppDump { get; set; } = false;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsEnabled(AppLogLevel level) =>
            level >= MinimumLogLevel && (level != AppLogLevel.Debug || EnableDebugLogging);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteLine(object data)
        {
            lock (_writeLock)
                WriteCore(data?.ToString() ?? string.Empty);
        }

        public static void WriteBlock(List<string> lines)
        {
            lock (_writeLock)
                foreach (var line in lines)
                    WriteCore(line);
        }

        private static void WriteCore(string message)
        {
            var timestamp = DateTime.UtcNow.ToString("HH:mm:ss.fff");
            var formatted = $"[{timestamp}] {message}";
            Debug.WriteLine(formatted);
            if (_consoleAllocated)
            {
                var color = message.Contains("ERROR") || message.Contains("FAIL") ? ConsoleColor.Red
                    : message.Contains("[IL2CPP]") ? ConsoleColor.Green
                    : message.Contains("[GOM]") || message.Contains("[Signature]") ? ConsoleColor.Yellow
                    : message.Contains("OK") || message.Contains("success") ? ConsoleColor.Cyan
                    : ConsoleColor.Gray;
                if (color != _currentColor) { Console.ForegroundColor = color; _currentColor = color; }
                Console.WriteLine(formatted);
            }
            _fileWriter?.WriteLine(formatted);
        }

        public static void Flush()
        {
            lock (_writeLock)
                _fileWriter?.Flush();
        }

        public static void Close()
        {
            lock (_writeLock)
            {
                var writer = _fileWriter;
                _fileWriter = null;
                if (writer is null) return;
                try { writer.Flush(); }
                catch { }
                try { writer.Dispose(); }
                catch { }
            }
        }

        public static void Write(AppLogLevel level, string message, string category = "")
        {
            if (!IsEnabled(level)) return;
            var prefix = level switch
            {
                AppLogLevel.Error   => "ERROR ",
                AppLogLevel.Warning => "WARNING ",
                AppLogLevel.Debug   => "DEBUG ",
                _                   => ""
            };
            var line = (prefix.Length, string.IsNullOrEmpty(category)) switch
            {
                (0, true)  => message,
                (0, false) => $"[{category}] {message}",
                (_, true)  => $"{prefix}{message}",
                _          => $"{prefix}[{category}] {message}"
            };
            WriteLine(line);
        }

        private static readonly ConcurrentDictionary<string, DateTime> _rateLimitCache = new();
        private static long _lastRateLimitCleanupTick;
        private const long RateLimitCleanupIntervalMs = 300_000;
        private static readonly TimeSpan RateLimitStaleThreshold = TimeSpan.FromMinutes(5);

        public static void WriteRateLimited(AppLogLevel level, string key, TimeSpan interval, string message, string category = "")
        {
            if (!IsEnabled(level)) return;
            var now = DateTime.UtcNow;
            if (_rateLimitCache.TryGetValue(key, out var last) && now - last < interval) return;
            _rateLimitCache[key] = now;
            Write(level, message, category);
            var nowTick = Environment.TickCount64;
            if (nowTick - Interlocked.Read(ref _lastRateLimitCleanupTick) > RateLimitCleanupIntervalMs)
            {
                Interlocked.Exchange(ref _lastRateLimitCleanupTick, nowTick);
                foreach (var kvp in _rateLimitCache)
                    if (now - kvp.Value > RateLimitStaleThreshold)
                        _rateLimitCache.TryRemove(kvp.Key, out _);
            }
        }
    }
}
