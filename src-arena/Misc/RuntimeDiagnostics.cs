using eft_dma_radar.Arena.Unity;
using eft_dma_radar.Arena.Unity.IL2CPP;
using eft_dma_radar.Arena.Unity.PhysX;
using eft_dma_radar.Arena.GameWorld;
using SDK;

namespace eft_dma_radar.Arena.Misc
{
    /// <summary>
    /// Writes a small, atomically replaced machine-readable health snapshot.
    /// It contains no memory contents or player identity data and is safe to
    /// bundle with diagnostic logs.
    /// </summary>
    internal static class RuntimeDiagnostics
    {
        private static readonly object Sync = new();
        private static readonly string DirectoryPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "eft-dma-radar-arena");
        internal static readonly string FilePath = Path.Combine(DirectoryPath, "runtime-diagnostics-latest.json");
        internal static readonly string EventsFilePath = Path.Combine(DirectoryPath, "runtime-diagnostics-events.jsonl");
        private const long MaxEventsBytes = 10 * 1024 * 1024;

        private static string _rootStage = "not-observed";
        private static int _rootFailureStreak;
        private static long _lastRootCaptureTick;
        private static int _rootRecoveryCount;
        private static string _lastRefreshReason = "not-run";
        private static bool? _lastRefreshSucceeded;
        private static double _lastRefreshDurationMs;
        private static DateTime _lastRefreshUtc;
        private static string _lastWorkerDrain = "not-run";
        private static int _workerDrainTimeoutCount;

        internal static void ResetForTargetProcessChange()
        {
            lock (Sync)
            {
                _rootStage = "target-detached";
                _rootFailureStreak = 0;
                _lastRootCaptureTick = 0;
                _lastWorkerDrain = "target-detached";
            }
        }

        internal static void RecordFullRefresh(string reason, bool succeeded, TimeSpan duration)
        {
            lock (Sync)
            {
                _lastRefreshReason = reason;
                _lastRefreshSucceeded = succeeded;
                _lastRefreshDurationMs = duration.TotalMilliseconds;
                _lastRefreshUtc = DateTime.UtcNow;
                if (succeeded && reason.StartsWith("root-watchdog:", StringComparison.Ordinal))
                    _rootRecoveryCount++;
                Capture("full-refresh-result");
            }
        }

        internal static void RecordWorkerDrain(string workerName, bool stopped, TimeSpan duration)
        {
            lock (Sync)
            {
                _lastWorkerDrain = $"{workerName}:{(stopped ? "stopped" : "timeout")}:{duration.TotalMilliseconds:F0}ms";
                if (!stopped) _workerDrainTimeoutCount++;
                Capture("worker-drain");
            }
        }

        internal static void RecordRoot(string stage, int failureStreak)
        {
            lock (Sync)
            {
                long now = Environment.TickCount64;
                bool changed = !string.Equals(_rootStage, stage, StringComparison.Ordinal)
                    || Volatile.Read(ref _rootFailureStreak) != failureStreak;
                if (!changed && now - _lastRootCaptureTick < 5_000) return;

                _rootStage = stage;
                Volatile.Write(ref _rootFailureStreak, failureStreak);
                _lastRootCaptureTick = now;
                Capture("root-health");
            }
        }

        internal static void Capture(string reason)
        {
            lock (Sync)
            {
                try
                {
                    Directory.CreateDirectory(DirectoryPath);

                    var gameAssembly = Memory.GameAssemblyBase.IsValidVirtualAddress()
                        ? Memory.ReadPeFingerprint(Memory.GameAssemblyBase)
                        : (0u, 0u);
                    var unityPlayer = Memory.UnityBase.IsValidVirtualAddress()
                        ? Memory.ReadPeFingerprint(Memory.UnityBase)
                        : (0u, 0u);

                    var snapshot = new
                    {
                        SchemaVersion = 3,
                        CapturedUtc = DateTime.UtcNow,
                        Reason = reason,
                        ReceiverProcessId = Environment.ProcessId,
                        Target = new
                        {
                            ProcessName = "EscapeFromTarkovArena.exe",
                            ProcessId = Memory.ProcessId,
                            AttachGeneration = Memory.AttachGeneration,
                            State = Memory.State.ToString(),
                        },
                        Modules = new
                        {
                            GameAssembly = new
                            {
                                Base = ToHex(Memory.GameAssemblyBase),
                                Timestamp = ToHex(gameAssembly.Item1),
                                SizeOfImage = ToHex(gameAssembly.Item2),
                            },
                            UnityPlayer = new
                            {
                                Base = ToHex(Memory.UnityBase),
                                Timestamp = ToHex(unityPlayer.Item1),
                                SizeOfImage = ToHex(unityPlayer.Item2),
                                Version = Memory.UnityPlayerVersion,
                            },
                        },
                        Roots = new
                        {
                            GameObjectManager = ToHex(Memory.GOM),
                            GamePlayerOwnerStage = _rootStage,
                            FailureStreak = Volatile.Read(ref _rootFailureStreak),
                            RecoveryCount = _rootRecoveryCount,
                        },
                        Match = new
                        {
                            MapId = Memory.CurrentGameWorld?.MapID,
                            Active = Memory.CurrentGameWorld?.InMatch ?? false,
                        },
                        Recovery = new
                        {
                            LastRefreshReason = _lastRefreshReason,
                            LastRefreshSucceeded = _lastRefreshSucceeded,
                            LastRefreshDurationMs = _lastRefreshDurationMs,
                            LastRefreshUtc = _lastRefreshUtc,
                            LastWorkerDrain = _lastWorkerDrain,
                            WorkerDrainTimeoutCount = _workerDrainTimeoutCount,
                        },
                        RuntimeOffsets = new
                        {
                            TypeInfoTableRva = ToHex(Offsets.Special.TypeInfoTableRva),
                            GamePlayerOwnerTypeIndex = ToHex(Offsets.Special.GamePlayerOwner_TypeIndex),
                            CameraManagerGetInstanceRva = ToHex(Offsets.EFTCameraManager.GetInstance_RVA),
                            AllCamerasRva = ToHex(CameraManager.ResolvedAllCamerasRva),
                            CameraViewMatrix = ToHex(CameraManager.ResolvedViewMatrixOffset),
                            CameraFov = ToHex(CameraManager.ResolvedFovOffset),
                            CameraAspectRatio = ToHex(CameraManager.ResolvedAspectRatioOffset),
                            PhysXSdkRva = ToHex(PhysXOffsets.PhysXSdkRva),
                            PhysXSdkSource = PhysXOffsets.PhysXSdkSource,
                            TransformLayout = TransformLayoutResolver.CurrentDescription,
                        },
                        Camera = new
                        {
                            CameraManager.IsReady,
                            CameraManager.IsActive,
                            CameraManager.LastUpdateUtc,
                            LastUpdateAgeMs = CameraManager.LastUpdateUtc == default
                                ? (double?)null
                                : (DateTime.UtcNow - CameraManager.LastUpdateUtc).TotalMilliseconds,
                            Offsets = new
                            {
                                AllCameras = new { Value = ToHex(CameraManager.ResolvedAllCamerasRva), Source = CameraManager.AllCamerasSource },
                                ViewMatrix = new { Value = ToHex(CameraManager.ResolvedViewMatrixOffset), Source = CameraManager.ViewMatrixSource },
                                FOV = new { Value = ToHex(CameraManager.ResolvedFovOffset), Source = CameraManager.FovSource },
                                AspectRatio = new { Value = ToHex(CameraManager.ResolvedAspectRatioOffset), Source = CameraManager.AspectRatioSource },
                            },
                        },
                        OffsetSources = Il2CppDumper.GetFieldSourcesSnapshot(),
                        Files = new
                        {
                            SessionLog = Log.CurrentFilePath,
                            OffsetDiagnostics = Path.Combine(DirectoryPath, "offset-diagnostics-latest.json"),
                            RuntimeEvents = EventsFilePath,
                        },
                    };

                    string json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });
                    string temporary = FilePath + $".{Environment.ProcessId}.tmp";
                    File.WriteAllText(temporary, json, new UTF8Encoding(false));
                    File.Move(temporary, FilePath, true);

                    if (File.Exists(EventsFilePath)
                        && new FileInfo(EventsFilePath).Length >= MaxEventsBytes)
                    {
                        File.Move(EventsFilePath, EventsFilePath + ".previous", true);
                    }
                    string eventJson = JsonSerializer.Serialize(snapshot);
                    File.AppendAllText(EventsFilePath, eventJson + Environment.NewLine, new UTF8Encoding(false));
                }
                catch (Exception ex)
                {
                    Log.WriteRateLimited(AppLogLevel.Warning, "runtime_diag_write", TimeSpan.FromSeconds(10),
                        $"[Diagnostics] Failed to write runtime snapshot: {ex.Message}");
                }
            }
        }

        private static string ToHex(ulong value) => $"0x{value:X}";
    }
}
