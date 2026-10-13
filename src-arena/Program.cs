using System.Runtime.Versioning;
using eft_dma_radar.Arena.Unity.PhysX;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Arena.OffsetContracts.Tests")]
[assembly: AssemblyTitle("Arena DMA Radar")]
[assembly: AssemblyProduct("Arena DMA Radar")]
[assembly: AssemblyDescription("DMA radar for Escape from Tarkov: Arena — Silk.NET Edition")]
[assembly: AssemblyVersion("0.1.0.0")]
[assembly: AssemblyFileVersion("0.1.0.0")]
[assembly: SupportedOSPlatform("Windows")]

namespace eft_dma_radar.Arena
{
    internal static class ArenaProgram
    {
        internal const string Name = "Arena DMA Radar";

        internal static ArenaConfig Config { get; private set; } = null!;

        /// <summary>
        /// True when every build-derived cache must be bypassed for this run.
        /// Shared by IL2CPP, Camera and PhysX so one command-line switch has
        /// consistent semantics across all offset-bearing subsystems.
        /// </summary>
        internal static bool ForceOffsetRescan { get; } = Environment.GetCommandLineArgs()
            .Contains("-force-offset-rescan", StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// True when every runtime-resolvable offset is required. Unlike a forced
        /// rescan, this controls coverage policy and rejects a core-only cache.
        /// </summary>
        internal static bool RequireAllOffsets { get; } = Environment.GetCommandLineArgs()
            .Contains("-require-critical-offsets", StringComparer.OrdinalIgnoreCase);

        [STAThread]
        static void Main()
        {
            try
            {
                Config = ArenaConfig.Load();
                var args = Environment.GetCommandLineArgs();
                Log.EnableDebugLogging = Config.DebugLogging
                    || (args?.Contains("-debug", StringComparer.OrdinalIgnoreCase) ?? false);
                Log.EnableIl2CppDump = Config.Il2CppDump
                    || (args?.Contains("-il2cppdump", StringComparer.OrdinalIgnoreCase) ?? false);
                Log.WriteLine("[ArenaProgram] Config loaded OK.");
                Log.WriteLine($"[ArenaProgram] Session log: {Log.CurrentFilePath ?? "disabled"}");

                // Config field is the primary toggle; ExceptionTracer.Install
                // also honours the ARENA_TRACE_DMA_EXCEPTIONS env var as an
                // override for "I need this on for one launch without editing
                // config.json" scenarios.
                ExceptionTracer.Enabled = Config.TraceDmaExceptions;
                ExceptionTracer.Install();

                SetHighPerformanceMode();

                Memory.ModuleInit(Config);
                Log.WriteLine("[ArenaProgram] Memory module initialized — waiting for game...");

                MapManager.ModuleInit();

                // Apply persisted vischeck classifier settings (layer mask, name patterns, bone toggles).
                VisibilityClassifier.LoadFromConfig(Config);
                VisibilityWorker.LoadFromConfig(Config);
                VisCheckDiagnostics.LoadFromConfig(Config);

                // Launch minimal radar window (blocks until user closes it).
                eft_dma_radar.Arena.UI.RadarWindow.Run();
            }
            catch (Exception ex)
            {
                HandleFatalError(ex);
            }
            finally
            {
                Memory.Close();
                Log.Close();
            }
        }

        private static void SetHighPerformanceMode()
        {
            try
            {
                using var process = Process.GetCurrentProcess();
                process.PriorityClass = ProcessPriorityClass.High;
                Log.WriteLine("[ArenaProgram] High performance mode set.");
            }
            catch (Exception ex)
            {
                // Priority elevation is an optimization, not a startup requirement.
                Log.WriteLine($"[ArenaProgram] High performance mode unavailable: {ex.Message}; continuing normally.");
            }
        }

        private static void HandleFatalError(Exception ex)
        {
            string error = $"FATAL ERROR -> {ex}";
            Log.WriteLine(error);
            try { File.WriteAllText("crash.log", $"[{DateTime.Now:u}] {error}"); }
            catch { }
            Environment.FailFast(error);
        }
    }
}
