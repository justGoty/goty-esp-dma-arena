using System.IO;
using eft_dma_radar.Arena.Unity;
using VmmSharpEx;
using VmmSharpEx.Options;

using static eft_dma_radar.Arena.Unity.UnityOffsets;

namespace eft_dma_radar.Arena.GameWorld
{
    /// <summary>
    /// Arena camera resolver and ViewMatrix/FOV reader.
    /// <para>
    /// Camera discovery prefers the live
    /// <c>EFT.CameraControl.CameraManager.Instance</c> chain and falls back to a
    /// structurally validated Unity <c>AllCameras</c> name scan. Both paths resolve
    /// the FPS camera and the optional Optic/BaseOptic camera; the optic camera may
    /// not exist until the local player aims through a scoped optic.
    /// </para>
    /// <para>
    /// Exposes a static <see cref="WorldToScreen"/> for the (future) ESP overlay.
    /// </para>
    /// </summary>
    internal sealed class CameraManager
    {
        #region Static State

        private static ulong _allCamerasAddr;
        private static bool _staticInitDone;
        private static readonly object CameraOffsetSync = new();
        private static bool _cameraOffsetsPendingLiveValidation;
        private static int _cameraOffsetFailureStreak;
        private static DateTime _nextCameraOffsetRescanUtc;
        private static string _allCamerasSource = "unresolved";
        private static string _viewMatrixSource = "fallback";
        private static string _fovSource = "fallback";
        private static string _aspectRatioSource = "fallback";

        internal static ulong ResolvedAllCamerasRva
        {
            get
            {
                ulong unityBase = Memory.UnityBase;
                ulong address = Volatile.Read(ref _allCamerasAddr);
                return unityBase.IsValidVirtualAddress() && address >= unityBase
                    ? address - unityBase
                    : 0;
            }
        }

        internal static uint ResolvedViewMatrixOffset => Camera.ViewMatrix;
        internal static uint ResolvedFovOffset => Camera.FOV;
        internal static uint ResolvedAspectRatioOffset => Camera.AspectRatio;
        internal static string AllCamerasSource => Volatile.Read(ref _allCamerasSource);
        internal static string ViewMatrixSource => Volatile.Read(ref _viewMatrixSource);
        internal static string FovSource => Volatile.Read(ref _fovSource);
        internal static string AspectRatioSource => Volatile.Read(ref _aspectRatioSource);

        // -- EFT.CameraControl.CameraManager.Instance state ---------------------
        private static ulong _eftCameraManagerInstance;
        private static ulong _eftCameraManagerClassPtr;

        // -- Camera offset cache -------------------------------------------------

        private static readonly string CameraCacheFilePath =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                         "eft-dma-radar-arena", "camera_offsets.json");

        private static readonly JsonSerializerOptions _jsonOpts = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        private const int CameraCacheSchemaVersion = 3;

        private sealed class CameraOffsetCache
        {
            public int SchemaVersion { get; set; }
            public uint UnityPlayerTimestamp { get; set; }
            public uint UnityPlayerSizeOfImage { get; set; }
            public ulong AllCamerasRva { get; set; }
            public uint ViewMatrix { get; set; }
            public uint FOV { get; set; }
            public uint AspectRatio { get; set; }
            public Dictionary<string, string> Sources { get; set; } = new(StringComparer.Ordinal);
        }

        #endregion

        #region Static W2S State

        private const int VIEWPORT_TOLERANCE = 800;

        /// <summary>True if the CameraManager is active and reading data.</summary>
        public static bool IsActive { get; private set; }

        /// <summary>Game Viewport width (pixels).</summary>
        public static int ViewportWidth { get; private set; }

        /// <summary>Game Viewport height (pixels).</summary>
        public static int ViewportHeight { get; private set; }

        /// <summary>Center of the game viewport.</summary>
        public static Vector2 ViewportCenter => new(ViewportWidth / 2f, ViewportHeight / 2f);

        private sealed record CameraFrame(
            Vector3 Right,
            Vector3 Up,
            Vector3 Forward,
            Vector3 Translation,
            float Fov,
            float Aspect,
            DateTime TimestampUtc)
        {
            internal Vector3 WorldPosition =>
                -(Right * Translation.X + Up * Translation.Y + Forward * Translation.Z);
        }

        private static CameraFrame? _cameraFrame;

        /// <summary>True once a valid ViewMatrix + FOV has been read at least once this match. Required for ESP/W2S.</summary>
        public static bool IsReady { get; private set; }

        /// <summary>Last successfully-read FOV (degrees).</summary>
        public static float FieldOfView => Volatile.Read(ref _cameraFrame)?.Fov ?? 0f;

        /// <summary>Last successfully-read aspect ratio.</summary>
        public static float AspectRatio => Volatile.Read(ref _cameraFrame)?.Aspect ?? 0f;

        /// <summary>Diagnostic: last view-matrix translation (projection's negated forward).</summary>
        public static Vector3 ViewMatrixTranslation =>
            Volatile.Read(ref _cameraFrame)?.Translation ?? Vector3.Zero;

        /// <summary>Diagnostic: true if the view matrix has a non-zero translation (i.e. usable for W2S).</summary>
        public static bool HasUsableViewMatrix =>
            (Volatile.Read(ref _cameraFrame)?.Translation.LengthSquared() ?? 0f) > 1e-6f;

        /// <summary>World-space position of the live camera (derived from ViewMatrix).</summary>
        public static Vector3 WorldPosition =>
            Volatile.Read(ref _cameraFrame)?.WorldPosition ?? Vector3.Zero;

        /// <summary>UTC time of the last successful camera scatter read.</summary>
        public static DateTime LastUpdateUtc =>
            Volatile.Read(ref _cameraFrame)?.TimestampUtc ?? default;

        internal static void ResetReadiness()
        {
            IsReady = false;
            Volatile.Write(ref _cameraFrame, null);

            // Match teardown: the cached EFT.CameraControl.CameraManager.Instance
            // pointer almost certainly points at a now-freed singleton. Force the
            // next match to re-discover it (and the FPS/Optic cameras) from
            // scratch — otherwise TryResolveCameras keeps trying the stale pointer
            // forever, the camera never goes READY, and skeleton init (gated on
            // CameraManager.IsReady) never runs, breaking ESP bones for the round.
            _eftCameraManagerInstance = default;
            HasOpticCamera = false;
            Volatile.Write(ref _refreshRequested, 0);
        }

        /// <summary>
        /// Signals the camera worker that the FPS camera GameObject is likely about to be
        /// rebuilt (e.g. on local player respawn). The next <see cref="UpdateCamera"/> call
        /// will bypass the normal refresh rate-limit and re-resolve the FPSCamera pointer
        /// before reading. Cheap to call from any thread.
        /// </summary>
        public static void RequestFpsCameraRefresh()
        {
            Volatile.Write(ref _refreshRequested, 1);
        }

        // 1 = a consumer (e.g. RegisteredPlayers on respawn) requested an immediate FPS
        // camera re-resolve on the next worker tick, bypassing the normal rate-limit.
        private static int _refreshRequested;

        /// <summary>
        /// Update the Viewport dimensions for W2S calculations.
        /// Call once at CameraManager init or when config changes.
        /// </summary>
        public static void UpdateViewportRes(int width, int height)
        {
            ViewportWidth = width;
            ViewportHeight = height;
            Log.WriteLine($"[CameraManager] Viewport set to {width}x{height}");
        }

        #endregion

        #region Instance Fields

        /// <summary>FPS Camera pointer (may be re-resolved at runtime if the in-game camera is rebuilt, e.g. after respawn).</summary>
        public ulong FPSCamera { get; private set; }

        /// <summary>Optic Camera pointer (0 if not currently resolvable — only created when ADS through a scope).</summary>
        public ulong OpticCamera { get; private set; }

        /// <summary>True if an optic camera is currently resolved and being read.</summary>
        public static bool HasOpticCamera { get; private set; }

        #endregion

        #region Constructor / Init

        static CameraManager()
        {
            Memory.GameStopped += (_, _) => ResetForTargetProcessChange();
        }

        internal static void ResetForTargetProcessChange()
        {
            lock (CameraOffsetSync)
            {
                _allCamerasAddr = default;
                _staticInitDone = false;
                _cameraOffsetsPendingLiveValidation = false;
                _cameraOffsetFailureStreak = 0;
                _nextCameraOffsetRescanUtc = default;
                _allCamerasSource = "unresolved";
                ResetCameraStructOffsetsToFallbacks();
                _eftCameraManagerInstance = default;
                _eftCameraManagerClassPtr = default;
            }
            IsActive = false;
            IsReady = false;
            HasOpticCamera = false;
            Volatile.Write(ref _cameraFrame, null);
            Volatile.Write(ref _refreshRequested, 0);
        }

        private CameraManager(ulong fpsCamera, ulong opticCamera)
        {
            FPSCamera = fpsCamera;
            OpticCamera = opticCamera;
            IsActive = true;
            HasOpticCamera = opticCamera.IsValidVirtualAddress();
            if (HasOpticCamera)
                Log.WriteLine($"[CameraManager] FPSCamera: 0x{FPSCamera:X}  OpticCamera: 0x{OpticCamera:X}");
            else
                Log.WriteLine($"[CameraManager] FPSCamera: 0x{FPSCamera:X}  (no optic camera resolved yet)");
        }

        /// <summary>
        /// Non-throwing factory. Returns <c>null</c> when the camera pointer cannot
        /// be resolved (e.g. raid still loading). Safe to call repeatedly.
        /// </summary>
        public static CameraManager? TryCreate()
        {
            if (!TryResolveCameras(out var fpsCam, out var opticCam))
            {
                HandlePendingCameraOffsetFailure();
                return null;
            }

            if (!ValidateCameraSemantics(fpsCam))
            {
                HandlePendingCameraOffsetFailure();
                return null;
            }

            ConfirmCameraOffsetsLiveValidated(fpsCam);

            return new CameraManager(fpsCam, opticCam);
        }

        /// <summary>
        /// Pre-warms static camera data on game startup (once per game session).
        /// Tries to restore AllCameras address and Camera struct offsets from a
        /// cached file, falling back to signature scans if the cache is stale.
        /// </summary>
        public static void Initialize()
        {
            if (_staticInitDone)
                return;
            try
            {
                lock (CameraOffsetSync)
                {
                    if (_staticInitDone) return;
                    ResetCameraStructOffsetsToFallbacks();
                    if (!TryLoadCameraCache())
                    {
                        _allCamerasAddr = ResolveAllCamerasAddr();
                        ResolveCameraOffsets();
                    }
                    _cameraOffsetsPendingLiveValidation = true;
                    _cameraOffsetFailureStreak = 0;
                    _staticInitDone = true;
                }
            }
            catch (Exception ex)
            {
                Log.WriteLine($"[CameraManager] Static init failed: {ex.Message}");
            }
        }

        private static void ResetCameraStructOffsetsToFallbacks()
        {
            Camera.ViewMatrix = Camera.FallbackViewMatrix;
            Camera.FOV = Camera.FallbackFOV;
            Camera.AspectRatio = Camera.FallbackAspectRatio;
            _viewMatrixSource = "fallback";
            _fovSource = "fallback";
            _aspectRatioSource = "fallback";
        }

        private static void HandlePendingCameraOffsetFailure()
        {
            if (!_cameraOffsetsPendingLiveValidation
                || Interlocked.Increment(ref _cameraOffsetFailureStreak) < 3
                || DateTime.UtcNow < _nextCameraOffsetRescanUtc)
                return;

            lock (CameraOffsetSync)
            {
                if (!_cameraOffsetsPendingLiveValidation
                    || DateTime.UtcNow < _nextCameraOffsetRescanUtc)
                    return;

                _nextCameraOffsetRescanUtc = DateTime.UtcNow.AddSeconds(15);
                Interlocked.Exchange(ref _cameraOffsetFailureStreak, 0);
                _eftCameraManagerInstance = 0;
                _eftCameraManagerClassPtr = 0;
                _allCamerasAddr = ResolveAllCamerasAddr();
                ResetCameraStructOffsetsToFallbacks();
                ResolveCameraOffsets();
                Log.WriteLine(
                    "[CameraManager] Provisional camera offsets failed live semantics; " +
                    "signature resolution retried and cache remains untrusted.");
                RuntimeDiagnostics.Capture("camera-offset-rescan-after-live-failure");
            }
        }

        private static void ConfirmCameraOffsetsLiveValidated(ulong fpsCamera)
        {
            lock (CameraOffsetSync)
            {
                if (!_cameraOffsetsPendingLiveValidation) return;
                _cameraOffsetsPendingLiveValidation = false;
                Interlocked.Exchange(ref _cameraOffsetFailureStreak, 0);
                _viewMatrixSource = MarkCameraSourceLiveValidated(_viewMatrixSource);
                _fovSource = MarkCameraSourceLiveValidated(_fovSource);
                _aspectRatioSource = MarkCameraSourceLiveValidated(_aspectRatioSource);
                if (TryAllCamerasContainsCamera(fpsCamera))
                    _allCamerasSource = MarkCameraSourceLiveValidated(_allCamerasSource);
                else
                    Log.WriteLine(
                        "[CameraManager] Camera struct offsets passed live semantics, but AllCameras " +
                        "does not contain the resolved FPS camera; AllCameras cache commit rejected.");
                SaveCameraCache();
                Log.WriteLine(
                    $"[CameraManager] Camera offsets live-validated (VM=0x{Camera.ViewMatrix:X}, " +
                    $"FOV=0x{Camera.FOV:X}, AR=0x{Camera.AspectRatio:X}).");
                RuntimeDiagnostics.Capture("camera-offsets-live-validated");
            }
        }

        private static string MarkCameraSourceLiveValidated(string source) =>
            IsLiveValidatedCameraSource(source)
                ? source
                : $"live-semantic-validated:{source}";

        private static bool IsLiveValidatedCameraSource(string? source) =>
            !string.IsNullOrWhiteSpace(source)
            && source.StartsWith("live-semantic-validated:", StringComparison.Ordinal);

        private static bool TryAllCamerasContainsCamera(ulong camera)
        {
            if (!camera.IsValidVirtualAddress()
                || !_allCamerasAddr.IsValidVirtualAddress()
                || !Memory.TryReadPtr(_allCamerasAddr, out var list, false)
                || !Memory.TryReadPtr(list, out var items, false)
                || !Memory.TryReadValue<int>(list + 0x8, out var count, false)
                || !items.IsValidVirtualAddress()
                || count <= 0 || count > 1024)
                return false;

            for (int i = 0; i < count; i++)
            {
                if (Memory.TryReadPtr(items + (ulong)i * 8, out var item, false)
                    && item == camera)
                    return true;
            }
            return false;
        }

        #endregion

        #region WorldToScreen

        /// <summary>
        /// Translates a 3D world position to a 2D screen position using the live
        /// Unity <c>worldToCameraMatrix</c> basis + perspective from FOV/AspectRatio.
        /// </summary>
        public static bool WorldToScreen(ref Vector3 worldPos, out Vector2 scrPos, bool onScreenCheck = false, bool useTolerance = false)
        {
            scrPos = default;
            var frame = Volatile.Read(ref _cameraFrame);
            if (frame is null) return false;

            if (worldPos.LengthSquared() < 1f)
                return false;

            // World -> view space (Unity: camera looks down -Z in view space).
            float vx = Vector3.Dot(frame.Right,   worldPos) + frame.Translation.X;
            float vy = Vector3.Dot(frame.Up,      worldPos) + frame.Translation.Y;
            float vz = Vector3.Dot(frame.Forward, worldPos) + frame.Translation.Z;

            // In front of the camera: vz < 0. Reject points at/behind the near plane.
            float depth = -vz;
            if (depth < 0.1f)
                return false;

            if (frame.Fov < 1f || frame.Aspect < 0.01f || ViewportWidth <= 0 || ViewportHeight <= 0)
                return false;

            // Unity Camera.fieldOfView is the vertical FOV in degrees.
            float tanHalfV = MathF.Tan(frame.Fov * (MathF.PI / 360f)); // (FOV/2) * deg2rad
            float ndcX = vx / (depth * tanHalfV * frame.Aspect);
            float ndcY = vy / (depth * tanHalfV);

            var center = ViewportCenter;
            scrPos = new Vector2(
                center.X + ndcX * center.X,
                center.Y - ndcY * center.Y);

            if (onScreenCheck)
            {
                int left   = useTolerance ? -VIEWPORT_TOLERANCE : 0;
                int right  = useTolerance ? ViewportWidth  + VIEWPORT_TOLERANCE : ViewportWidth;
                int top    = useTolerance ? -VIEWPORT_TOLERANCE : 0;
                int bottom = useTolerance ? ViewportHeight + VIEWPORT_TOLERANCE : ViewportHeight;

                if (scrPos.X < left || scrPos.X > right ||
                    scrPos.Y < top  || scrPos.Y > bottom)
                {
                    scrPos = default;
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Returns the FOV magnitude (distance from screen center) for a screen point.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float GetFovMagnitude(Vector2 point)
            => Vector2.Distance(ViewportCenter, point);

        /// <summary>
        /// Builds a synthetic <see cref="ViewMatrix"/> (basis + translation in
        /// view-space) from a world-space eye position and EFT-style yaw/pitch.
        /// Used as a fallback when the live game ViewMatrix is not yet available.
        /// </summary>
        public static ViewMatrix BuildViewMatrix(Vector3 position, float yawDeg, float pitchDeg)
        {
            float yaw   =  yawDeg   * (MathF.PI / 180f);
            float pitch = -pitchDeg * (MathF.PI / 180f);
            float cy = MathF.Cos(yaw),   sy = MathF.Sin(yaw);
            float cp = MathF.Cos(pitch), sp = MathF.Sin(pitch);

            // World-space basis of the camera.
            var fwdWorld   = new Vector3(sy * cp, sp,  cy * cp); // looks toward
            var rightWorld = new Vector3(cy,      0f, -sy);
            var upWorld    = Vector3.Cross(fwdWorld, rightWorld);

            // Unity worldToCamera convention: Forward axis stored is -fwdWorld
            // (camera looks down -Z in view space).
            var fwdView = -fwdWorld;

            var vm = new ViewMatrix
            {
                Right       = rightWorld,
                Up          = upWorld,
                Forward     = fwdView,
                Translation = new Vector3(
                    -Vector3.Dot(rightWorld, position),
                    -Vector3.Dot(upWorld,    position),
                    -Vector3.Dot(fwdView,    position)),
            };
            return vm;
        }

        #endregion

        #region Scatter Read (Camera Worker)

        /// <summary>
        /// Updates camera data via VmmScatter — called from the camera worker.
        /// Reads ViewMatrix + FOV + AspectRatio from the FPS camera in a single batch.
        /// </summary>
        public void UpdateCamera()
        {
            // Detect a stale FPS camera (e.g. local player respawn rebuilt the
            // FPS camera GameObject and our cached pointer now points at freed
            // memory). Try to silently re-resolve before doing the scatter read.
            bool forced = Interlocked.Exchange(ref _refreshRequested, 0) != 0;
            if (forced)
            {
                // Reset rate-limit so the refresh actually runs even if a recent
                // refresh just happened, and clear readiness so consumers wait
                // until we have a fresh matrix from the new camera.
                _nextFpsRefreshUtc = default;
                IsReady = false;
                Volatile.Write(ref _cameraFrame, null);
                TryRefreshFpsCamera();
            }

            if (!FPSCamera.IsValidVirtualAddress() || !ValidateCameraMatrix(FPSCamera))
            {
                if (!TryRefreshFpsCamera())
                    return;
            }

            // Lazily resolve optic camera if it wasn't available at construction
            // (e.g. the local player started ADS through a scope mid-match).
            TryRefreshOpticCamera();

            ulong vmAddr = FPSCamera + Camera.ViewMatrix;

            using var scatter = Memory.GetScatter(VmmFlags.NOCACHE);
            scatter.PrepareReadValue<Matrix4x4>(vmAddr);
            scatter.PrepareReadValue<float>(FPSCamera + Camera.FOV);
            scatter.PrepareReadValue<float>(FPSCamera + Camera.AspectRatio);
            scatter.Execute();

            bool vmOk = false, fovOk = false, arOk = false;
            Matrix4x4 vm = default;
            float fov = 0f;
            float aspect = 0f;

            // Validate BEFORE writing to the cached basis — a half-page-fault read
            // can return true with all-zero / NaN bytes, and Update() would then
            // poison the live viewMatrix used by W2S and silently freeze WorldPosition
            // at garbage values. The previous code wrote first and validated second.
            if (scatter.ReadValue<Matrix4x4>(vmAddr, out vm)
                && IsPlausibleCameraMatrix(in vm))
            {
                vmOk = true;
            }

            if (scatter.ReadValue<float>(FPSCamera + Camera.FOV, out fov)
                && float.IsFinite(fov) && fov > 1f && fov < 180f)
            {
                fovOk = true;
            }

            if (scatter.ReadValue<float>(FPSCamera + Camera.AspectRatio, out aspect)
                && float.IsFinite(aspect) && aspect >= 0.25f && aspect <= 8f)
            {
                arOk = true;
            }

            if (vmOk && fovOk && arOk)
            {
                var frame = new CameraFrame(
                    new Vector3(vm.M11, vm.M21, vm.M31),
                    new Vector3(vm.M12, vm.M22, vm.M32),
                    new Vector3(vm.M13, vm.M23, vm.M33),
                    new Vector3(vm.M41, vm.M42, vm.M43),
                    fov,
                    aspect,
                    DateTime.UtcNow);
                Volatile.Write(ref _cameraFrame, frame);
                if (!IsReady)
                {
                    IsReady = true;
                    Log.WriteLine(
                        $"[CameraManager] READY — FPSCamera=0x{FPSCamera:X} FOV={frame.Fov:F1} AR={frame.Aspect:F3} " +
                        $"VM.T=<{frame.Translation.X:F2},{frame.Translation.Y:F2},{frame.Translation.Z:F2}> " +
                        $"viewport={ViewportWidth}x{ViewportHeight} — ESP enabled.");
                }
                else
                {
                    Log.WriteRateLimited(AppLogLevel.Info, "cam_heartbeat", TimeSpan.FromSeconds(15),
                        $"[CameraManager] Heartbeat OK — FOV={frame.Fov:F1} AR={frame.Aspect:F3} " +
                        $"T=<{frame.Translation.X:F1},{frame.Translation.Y:F1},{frame.Translation.Z:F1}>");
                }
            }
            else if (IsReady)
            {
                Log.WriteRateLimited(AppLogLevel.Warning, "cam_partial", TimeSpan.FromSeconds(5),
                    $"[CameraManager] Partial read — vm={vmOk} fov={fovOk} ar={arOk} (FPSCamera=0x{FPSCamera:X})");

                // ANY read failing is enough to suspect the FPS camera was rebuilt
                // (respawn / map transition). Waiting for all three to fail before
                // refreshing was the "stuck camera" cause — a freshly-freed camera
                // often keeps returning a plausible aspect ratio for a while while
                // the ViewMatrix slot already reads as zeros. Triggering on any
                // failure lets the 500 ms refresh rate-limit decide cadence
                // instead of relying on full-block failure detection.
                TryRefreshFpsCamera();
            }
        }

        #endregion

        #region Camera Resolution

        private DateTime _nextOpticProbeUtc;
        private DateTime _nextFpsRefreshUtc;

        /// <summary>
        /// Re-resolves the FPS camera pointer when the cached one becomes stale
        /// (e.g. local player respawn rebuilt the FPS camera GameObject). Rate-
        /// limited to avoid hammering during transient read failures. Tries the
        /// CameraManager.Instance singleton first, then falls back to the Unity
        /// AllCameras name scan.
        /// </summary>
        private bool TryRefreshFpsCamera()
        {
            var now = DateTime.UtcNow;
            if (now < _nextFpsRefreshUtc)
                return false;
            _nextFpsRefreshUtc = now.AddMilliseconds(500);

            // Try the singleton path first (cheap, direct field reads).
            if (_eftCameraManagerInstance.IsValidVirtualAddress() &&
                TryResolveViaCameraManagerInstance(out var fpsCam, out var opticCam) &&
                fpsCam.IsValidVirtualAddress())
            {
                if (fpsCam != FPSCamera)
                {
                    Log.WriteLine($"[CameraManager] FPSCamera refreshed: 0x{FPSCamera:X} -> 0x{fpsCam:X} (via Instance)");
                    FPSCamera = fpsCam;
                    if (opticCam.IsValidVirtualAddress())
                    {
                        OpticCamera = opticCam;
                        HasOpticCamera = true;
                    }
                }
                return true;
            }

            // Singleton may have been rebuilt too — re-find it once and retry.
            _eftCameraManagerInstance = FindCameraManagerInstance();
            if (_eftCameraManagerInstance.IsValidVirtualAddress() &&
                TryResolveViaCameraManagerInstance(out fpsCam, out opticCam) &&
                fpsCam.IsValidVirtualAddress())
            {
                Log.WriteLine($"[CameraManager] FPSCamera refreshed: 0x{FPSCamera:X} -> 0x{fpsCam:X} (Instance re-resolved)");
                FPSCamera = fpsCam;
                if (opticCam.IsValidVirtualAddress())
                {
                    OpticCamera = opticCam;
                    HasOpticCamera = true;
                }
                return true;
            }

            // Last resort: AllCameras name scan.
            if (TryResolveViaAllCamerasByName(out fpsCam, out opticCam) && fpsCam.IsValidVirtualAddress())
            {
                Log.WriteLine($"[CameraManager] FPSCamera refreshed: 0x{FPSCamera:X} -> 0x{fpsCam:X} (via AllCameras)");
                FPSCamera = fpsCam;
                if (opticCam.IsValidVirtualAddress())
                {
                    OpticCamera = opticCam;
                    HasOpticCamera = true;
                }
                return true;
            }

            return false;
        }

        /// <summary>
        /// Tries to (re)acquire the optic camera periodically while the manager is
        /// alive. The optic camera typically only exists while the local player is
        /// aiming through a scoped optic, and may come and go many times per match.
        /// </summary>
        private void TryRefreshOpticCamera()
        {
            // Probe at most ~2x/sec to avoid hammering the AllCameras list.
            var now = DateTime.UtcNow;
            if (now < _nextOpticProbeUtc)
                return;
            _nextOpticProbeUtc = now.AddMilliseconds(500);

            // If the current optic is still valid, keep it.
            if (OpticCamera.IsValidVirtualAddress() && ValidateCameraMatrix(OpticCamera))
            {
                if (!HasOpticCamera)
                {
                    HasOpticCamera = true;
                    Log.WriteLine($"[CameraManager] OpticCamera acquired: 0x{OpticCamera:X}");
                }
                return;
            }

            if (TryResolveOpticCamera(out var opticCam) && ValidateCameraMatrix(opticCam))
            {
                bool wasResolved = HasOpticCamera;
                OpticCamera = opticCam;
                HasOpticCamera = true;
                if (!wasResolved)
                    Log.WriteLine($"[CameraManager] OpticCamera acquired: 0x{OpticCamera:X}");
            }            else if (HasOpticCamera)
            {
                Log.WriteLine($"[CameraManager] OpticCamera lost (was 0x{OpticCamera:X}).");
                OpticCamera = 0;
                HasOpticCamera = false;
            }
        }

        /// <summary>
        /// Multi-path resolver — tries two known camera-source paths in order:
        ///   1) <c>EFT.CameraControl.CameraManager.Instance</c> (preferred —
        ///      direct field read, no name scan, gives both FPS + Optic cameras).
        ///   2) Unity <c>AllCameras</c> list + GameObject name search (fallback).
        /// The optic camera is optional — it only exists while the local player
        /// is aiming through a scoped optic.
        /// </summary>
        private static bool TryResolveCameras(out ulong fpsCamera, out ulong opticCamera)
        {
            // Path 1: EFT.CameraControl.CameraManager.Instance
            if (!_eftCameraManagerInstance.IsValidVirtualAddress())
                _eftCameraManagerInstance = FindCameraManagerInstance();

            if (_eftCameraManagerInstance.IsValidVirtualAddress() &&
                TryResolveViaCameraManagerInstance(out fpsCamera, out opticCamera))
            {
                Log.WriteLine($"[CameraManager] Using CameraManager.Instance — FPS: 0x{fpsCamera:X}, Optic: {(opticCamera != 0 ? $"0x{opticCamera:X}" : "deferred")}");
                return true;
            }

            // The cached singleton pointer can be stale at the start of a new match
            // (the previous instance was torn down on match end). Re-find it once
            // and retry before falling back to the slower AllCameras name scan —
            // otherwise we may sit in "FPS camera not resolvable yet" for the
            // entire camera-init budget and skeletons never get created.
            if (_eftCameraManagerInstance.IsValidVirtualAddress())
            {
                _eftCameraManagerInstance = FindCameraManagerInstance();
                if (_eftCameraManagerInstance.IsValidVirtualAddress() &&
                    TryResolveViaCameraManagerInstance(out fpsCamera, out opticCamera))
                {
                    Log.WriteLine($"[CameraManager] Using CameraManager.Instance (re-resolved) — FPS: 0x{fpsCamera:X}, Optic: {(opticCamera != 0 ? $"0x{opticCamera:X}" : "deferred")}");
                    return true;
                }
            }

            // Path 2: Unity AllCameras name search
            if (TryResolveViaAllCamerasByName(out fpsCamera, out opticCamera))
            {
                Log.WriteLine("[CameraManager] Using Unity AllCameras fallback.");
                return true;
            }

            fpsCamera = 0;
            opticCamera = 0;
            return false;
        }

        /// <summary>
        /// Reads the FPS + Optic cameras directly from <c>EFT.CameraControl.CameraManager.Instance</c>.
        /// Optic camera is optional.
        /// </summary>
        private static bool TryResolveViaCameraManagerInstance(out ulong fpsCamera, out ulong opticCamera)
        {
            fpsCamera = 0;
            opticCamera = 0;

            if (!_eftCameraManagerInstance.IsValidVirtualAddress())
                return false;

            // FPS camera (required)
            if (!Memory.TryReadPtr(_eftCameraManagerInstance + SDK.Offsets.EFTCameraManager.Camera, out var fpsCameraRef, false))
                return false;

            if (!TryReadObjectClassName(fpsCameraRef, out var name, 32) ||
                !string.Equals(name, "Camera", StringComparison.Ordinal))
                return false;

            if (!Memory.TryReadPtr(fpsCameraRef + ObjectClass.MonoBehaviourOffset, out fpsCamera, false))
                return false;

            if (!ValidateCameraMatrix(fpsCamera))
            {
                fpsCamera = 0;
                return false;
            }

            // Optic camera (optional)
            TryResolveOpticCameraFromInstance(out opticCamera);
            return true;
        }

        /// <summary>
        /// Best-effort optic camera resolution from <c>CameraManager.Instance</c>.
        /// Failures are silently ignored — optic camera is optional and only
        /// exists while the player is ADS through a scope.
        /// </summary>
        private static bool TryResolveOpticCameraFromInstance(out ulong opticCamera)
        {
            opticCamera = 0;

            if (!_eftCameraManagerInstance.IsValidVirtualAddress())
                return false;

            if (!Memory.TryReadPtr(_eftCameraManagerInstance + SDK.Offsets.EFTCameraManager.OpticCameraManager, out var opticCameraManager, false) ||
                !opticCameraManager.IsValidVirtualAddress())
                return false;

            if (!Memory.TryReadPtr(opticCameraManager + SDK.Offsets.OpticCameraManager.Camera, out var opticCameraRef, false) ||
                !opticCameraRef.IsValidVirtualAddress())
                return false;

            if (!TryReadObjectClassName(opticCameraRef, out var name, 32) ||
                !string.Equals(name, "Camera", StringComparison.Ordinal))
                return false;

            if (!Memory.TryReadPtr(opticCameraRef + ObjectClass.MonoBehaviourOffset, out opticCamera, false))
                return false;

            return true;
        }

        /// <summary>
        /// Reads a Unity ObjectClass-style name (objectClass -> [+0x0] -> [+0x10] -> cstr).
        /// </summary>
        private static bool TryReadObjectClassName(ulong objectClassPtr, out string? name, int maxLen)
        {
            name = null;
            if (!objectClassPtr.IsValidVirtualAddress())
                return false;

            if (!Memory.TryReadPtrChain(objectClassPtr, ObjClass_ToNamePtr, out var namePtr, false))
                return false;

            return Memory.TryReadString(namePtr, out name, maxLen, false) && !string.IsNullOrEmpty(name);
        }

        /// <summary>
        /// Pattern-scans <c>EFT.CameraControl.CameraManager::get_Instance</c> in
        /// GameAssembly.dll for the static-fields pointer and reads the singleton
        /// instance from it. No GOM lookup is needed.
        /// </summary>
        private static ulong FindCameraManagerInstance()
        {
            try
            {
                var gameAssemblyBase = Memory.GameAssemblyBase;
                if (!gameAssemblyBase.IsValidVirtualAddress())
                    return 0;

                ulong getInstanceRva = SDK.Offsets.EFTCameraManager.GetInstance_RVA;
                if (getInstanceRva == 0)
                    return 0;

                ulong methodAddr = gameAssemblyBase + getInstanceRva;

                Span<byte> methodBytes = stackalloc byte[128];
                if (!Memory.TryReadBuffer(methodAddr, methodBytes, false))
                    return 0;

                // Pattern 1: lea rcx, [rip+offset] -> Il2CppClass*
                for (int i = 0; i < methodBytes.Length - 7; i++)
                {
                    if (methodBytes[i] == 0x48 && methodBytes[i + 1] == 0x8D && methodBytes[i + 2] == 0x0D)
                    {
                        int disp32 = BitConverter.ToInt32(methodBytes.Slice(i + 3, 4));
                        ulong classMetadataAddr = methodAddr + (ulong)i + 7 + (ulong)disp32;

                        if (!Memory.TryReadPtr(classMetadataAddr, out var classPtr, false))
                            continue;

                        var knownOffset = SDK.Offsets.Il2CppClass.StaticFields;
                        ReadOnlySpan<uint> fallbackOffsets =
                        [
                            knownOffset - 0x10, knownOffset - 0x08,
                            knownOffset + 0x08, knownOffset + 0x10, knownOffset + 0x18,
                        ];

                        if (TryReadStaticInstance(classPtr, knownOffset, out var instance))
                        {
                            _eftCameraManagerClassPtr = classPtr;
                            return instance;
                        }

                        foreach (var offset in fallbackOffsets)
                        {
                            if (offset == knownOffset) continue;
                            if (TryReadStaticInstance(classPtr, offset, out instance))
                            {
                                _eftCameraManagerClassPtr = classPtr;
                                return instance;
                            }
                        }
                    }
                }

                // Pattern 2: mov rax, [rip+offset] -> direct static field
                for (int i = 32; i < methodBytes.Length - 7; i++)
                {
                    if (methodBytes[i] == 0x48 && methodBytes[i + 1] == 0x8B && methodBytes[i + 2] == 0x05)
                    {
                        int disp32 = BitConverter.ToInt32(methodBytes.Slice(i + 3, 4));
                        ulong staticFieldAddr = methodAddr + (ulong)i + 7 + (ulong)disp32;

                        if (!Memory.TryReadPtr(staticFieldAddr, out var instancePtr, false))
                            continue;

                        if (Memory.TryReadPtr(instancePtr + SDK.Offsets.EFTCameraManager.Camera, out var testCamera, false)
                            && testCamera.IsValidVirtualAddress())
                            return instancePtr;
                    }
                }

                return 0;
            }
            catch (Exception ex)
            {
                Log.WriteLine($"[CameraManager] FindInstance error: {ex.Message}");
                return 0;
            }
        }

        private static bool TryReadStaticInstance(ulong classPtr, uint staticFieldsOffset, out ulong instance)
        {
            instance = 0;

            if (!Memory.TryReadPtr(classPtr + staticFieldsOffset, out var staticFieldsPtr, false))
                return false;

            if (!Memory.TryReadPtr(staticFieldsPtr, out var instancePtr, false))
                return false;

            if (!Memory.TryReadPtr(instancePtr + SDK.Offsets.EFTCameraManager.Camera, out var testCamera, false) ||
                !testCamera.IsValidVirtualAddress())
                return false;

            instance = instancePtr;
            return true;
        }

        /// <summary>
        /// Resolves the FPS camera (required) and optic camera (optional) via the
        /// Unity AllCameras list + GameObject name search. The optic camera is only
        /// present when the local player is currently aiming through a scoped optic,
        /// so a missing optic is NOT a failure.
        /// </summary>
        private static bool TryResolveViaAllCamerasByName(out ulong fpsCamera, out ulong opticCamera)
        {
            fpsCamera = 0;
            opticCamera = 0;

            try
            {
                if (!_allCamerasAddr.IsValidVirtualAddress())
                    return false;

                if (!Memory.TryReadPtr(_allCamerasAddr, out var allCamerasPtr, false))
                    return false;

                if (!Memory.TryReadPtr(allCamerasPtr + 0x0, out var itemsPtr, false) ||
                    !Memory.TryReadValue<int>(allCamerasPtr + 0x8, out var count, false))
                    return false;

                if (!itemsPtr.IsValidVirtualAddress() || count <= 0 || count > 1024)
                    return false;

                FindCamerasByName(itemsPtr, count, out fpsCamera, out opticCamera);

                if (!fpsCamera.IsValidVirtualAddress() || !ValidateCameraMatrix(fpsCamera))
                    fpsCamera = 0;

                if (!opticCamera.IsValidVirtualAddress())
                    opticCamera = 0;

                // FPS camera is required; optic is optional.
                return fpsCamera != 0;
            }
            catch (Exception ex)
            {
                Log.WriteLine($"[CameraManager] AllCameras resolution error: {ex.Message}");
                fpsCamera = 0;
                opticCamera = 0;
                return false;
            }
        }

        /// <summary>
        /// Lazily resolves the optic camera from the AllCameras list. Used to pick up
        /// an optic camera that appeared after CameraManager was created (e.g. local
        /// player started ADS through a scope mid-match).
        /// </summary>
        private static bool TryResolveOpticCamera(out ulong opticCamera)
        {
            opticCamera = 0;

            // Prefer the singleton path \u2014 reads the optic ref directly off the
            // CameraManager.Instance + OpticCameraManager chain.
            if (_eftCameraManagerInstance.IsValidVirtualAddress() &&
                TryResolveOpticCameraFromInstance(out opticCamera) &&
                opticCamera.IsValidVirtualAddress())
            {
                return true;
            }

            try
            {
                if (!_allCamerasAddr.IsValidVirtualAddress())
                    return false;

                if (!Memory.TryReadPtr(_allCamerasAddr, out var allCamerasPtr, false))
                    return false;

                if (!Memory.TryReadPtr(allCamerasPtr + 0x0, out var itemsPtr, false) ||
                    !Memory.TryReadValue<int>(allCamerasPtr + 0x8, out var count, false))
                    return false;

                if (!itemsPtr.IsValidVirtualAddress() || count <= 0 || count > 1024)
                    return false;

                FindCamerasByName(itemsPtr, count, out _, out opticCamera);

                if (!opticCamera.IsValidVirtualAddress())
                    opticCamera = 0;

                return opticCamera != 0;
            }
            catch
            {
                opticCamera = 0;
                return false;
            }
        }

        /// <summary>
        /// Scans the AllCameras list for "FPS Camera" / "Optic Camera" / "BaseOpticCamera"
        /// style GameObject names.
        /// </summary>
        private static void FindCamerasByName(ulong itemsPtr, int count, out ulong fpsCamera, out ulong opticCamera)
        {
            fpsCamera = 0;
            opticCamera = 0;

            int max = Math.Min(count, 100);

            for (int i = 0; i < max; i++)
            {
                ulong entryAddr = itemsPtr + (uint)(i * 0x8);
                if (!Memory.TryReadPtr(entryAddr, out var cameraPtr, false))
                    continue;

                // cameraPtr is the Camera *component*; walk Component→GameObject.
                if (!Memory.TryReadPtr(cameraPtr + Comp_GameObject, out var gameObject, false))
                    continue;

                if (!Memory.TryReadPtr(gameObject + GO_Name, out var namePtr, false))
                    continue;

                // GameObject names are native C-strings (UTF-8), not Unity managed strings
                if (!Memory.TryReadString(namePtr, out var goName, 64, false) || string.IsNullOrEmpty(goName))
                    continue;

                bool hasCamera = goName.Contains("Camera", StringComparison.OrdinalIgnoreCase);
                if (!hasCamera)
                    continue;

                bool isOptic =
                    goName.Contains("Optic", StringComparison.OrdinalIgnoreCase) ||
                    goName.Contains("BaseOptic", StringComparison.OrdinalIgnoreCase);

                bool isFps =
                    !isOptic &&
                    goName.Contains("FPS", StringComparison.OrdinalIgnoreCase);

                if (isFps && fpsCamera == 0)
                    fpsCamera = cameraPtr;

                if (isOptic && opticCamera == 0)
                    opticCamera = cameraPtr;

                if (fpsCamera != 0 && opticCamera != 0)
                    break;
            }
        }

        /// <summary>
        /// DumpCameraOffsets has been removed; the live offsets (VM=0x88, FOV=0x188, AR=0x4F8)
        /// were confirmed during development and are now hard-coded in UnityOffsets.Camera.
        /// </summary>

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsFiniteMatrix(in Matrix4x4 m) =>
            float.IsFinite(m.M11) && float.IsFinite(m.M12) && float.IsFinite(m.M13) && float.IsFinite(m.M14) &&
            float.IsFinite(m.M21) && float.IsFinite(m.M22) && float.IsFinite(m.M23) && float.IsFinite(m.M24) &&
            float.IsFinite(m.M31) && float.IsFinite(m.M32) && float.IsFinite(m.M33) && float.IsFinite(m.M34) &&
            float.IsFinite(m.M41) && float.IsFinite(m.M42) && float.IsFinite(m.M43) && float.IsFinite(m.M44);

        /// <summary>Quick sanity check for a camera's view matrix.</summary>
        private static bool ValidateCameraMatrix(ulong cameraPtr)
        {
            if (!Memory.TryReadValue<Matrix4x4>(cameraPtr + Camera.ViewMatrix, out var vm, false))
                return false;

            return IsPlausibleCameraMatrix(in vm);
        }

        private static bool IsPlausibleCameraMatrix(in Matrix4x4 vm)
        {
            if (!IsFiniteMatrix(in vm))
                return false;

            if (vm.M11 == 0f && vm.M22 == 0f && vm.M33 == 0f && vm.M44 == 0f)
                return false;

            if (Math.Abs(vm.M41) > 5000f || Math.Abs(vm.M42) > 5000f || Math.Abs(vm.M43) > 5000f)
                return false;

            float rightNorm = new Vector3(vm.M11, vm.M12, vm.M13).LengthSquared();
            float upNorm = new Vector3(vm.M21, vm.M22, vm.M23).LengthSquared();
            float forwardNorm = new Vector3(vm.M31, vm.M32, vm.M33).LengthSquared();
            if (rightNorm is < 0.0625f or > 16f
                || upNorm is < 0.0625f or > 16f
                || forwardNorm is < 0.0625f or > 16f)
                return false;

            return true;
        }

        private static bool ValidateCameraSemantics(ulong cameraPtr)
        {
            if (!IsPlausibleCameraStructOffsets(
                    Camera.ViewMatrix, Camera.FOV, Camera.AspectRatio)
                || !ValidateCameraMatrix(cameraPtr)
                || !Memory.TryReadValue<float>(cameraPtr + Camera.FOV, out var fov, false)
                || !float.IsFinite(fov) || fov <= 1f || fov >= 180f
                || !Memory.TryReadValue<float>(cameraPtr + Camera.AspectRatio, out var aspect, false)
                || !float.IsFinite(aspect) || aspect < 0.25f || aspect > 8f)
                return false;
            return true;
        }

        private static bool IsPlausibleCameraStructOffsets(uint viewMatrix, uint fov, uint aspectRatio) =>
            IsPlausibleCameraStructOffset(viewMatrix)
            && IsPlausibleCameraStructOffset(fov)
            && IsPlausibleCameraStructOffset(aspectRatio)
            && viewMatrix != fov
            && viewMatrix != aspectRatio
            && fov != aspectRatio;

        private static bool IsPlausibleCameraStructOffset(uint value) =>
            value >= 0x10 && value < 0x1000 && (value & 0x3) == 0;

        #endregion

        #region AllCameras Resolution

        /// <summary>
        /// Candidate signatures for locating AllCameras global in UnityPlayer.dll.
        /// </summary>
        private static readonly (string Sig, int RelOff, int InstrLen, string Desc)[] AllCamerasSigs =
        [
            ("48 8B 1D ? ? ? ? 48 8B 73 ? 48 8B 43 ? 48 FF C6 ? ? ? 48 3B F0 76 ? 48 8B CB E8 ? ? ? ? ? ? ? 48 83 C1", 3, 7, "AllCameras: mov rax,[rip]; mov r14,imm; test ecx; jz; mov [rsp],rbx"),
        ];

        private static ulong ResolveAllCamerasAddr()
        {
            var unityBase = Memory.UnityBase;
            if (!unityBase.IsValidVirtualAddress())
                return 0;
            uint imageSize = Memory.GetModuleImageSize("UnityPlayer.dll");
            if (imageSize == 0) return 0;

            var signatureCandidates = new Dictionary<ulong, string>();

            foreach (var (sig, relOff, instrLen, desc) in AllCamerasSigs)
            {
                foreach (var sigAddr in Memory.FindSignatures(sig, "UnityPlayer.dll", 64))
                {
                    if (!Memory.TryReadValue<int>(sigAddr + (ulong)relOff, out var disp32, false))
                        continue;
                    long resolvedSigned = (long)sigAddr + instrLen + disp32;
                    if (resolvedSigned <= 0) continue;
                    ulong resolved = (ulong)resolvedSigned;
                    if (resolved < unityBase || resolved - unityBase >= imageSize
                        || !ValidateAllCamerasAddr(resolved))
                        continue;
                    signatureCandidates.TryAdd(resolved, desc);
                }
            }

            if (signatureCandidates.Count == 1)
            {
                var candidate = signatureCandidates.First();
                _allCamerasSource = $"signature:{candidate.Value}";
                Log.WriteLine($"[CameraManager] AllCameras located via unique validated sig: {candidate.Value}");
                return candidate.Key;
            }
            if (signatureCandidates.Count > 1)
            {
                Log.WriteLine(
                    $"[CameraManager] AllCameras sig scan ambiguous ({signatureCandidates.Count} structurally valid globals) — rejecting signatures.");
            }

            // Fallback: hardcoded offset
            var fallbackAddr = unityBase + AllCameras;
            if (AllCameras < imageSize && ValidateAllCamerasAddr(fallbackAddr))
            {
                _allCamerasSource = "structural-fallback";
                Log.WriteLine("[CameraManager] AllCameras sig scan missed — using structurally-validated fallback.");
                return fallbackAddr;
            }

            _allCamerasSource = "unresolved";
            Log.WriteLine("[CameraManager] AllCameras resolution FAILED.");
            return 0;
        }

        private static bool ValidateAllCamerasAddr(ulong addr)
        {
            if (!addr.IsValidVirtualAddress())
                return false;

            if (!Memory.TryReadPtr(addr, out var listPtr, false))
                return false;

            if (!Memory.TryReadPtr(listPtr, out var items, false))
                return false;

            if (!Memory.TryReadValue<int>(listPtr + 0x8, out var count, false))
                return false;

            return items.IsValidVirtualAddress() && count >= 0 && count < 1024;
        }

        #endregion

        #region Camera Struct Offset Sig Scan

        private readonly record struct CameraOffsetSig(
            string Sig,
            int OffsetPos,
            int DispSize,
            bool IsCallSite,
            int TargetBodyDispOffset,
            int TargetBodyDispSize,
            string Desc);

        private readonly record struct ResolvedCameraOffset(uint Offset, string Source);

        private static readonly CameraOffsetSig[] ViewMatrixSigs =
        [
            new("E8 ? ? ? ? 48 3B 58 ? 0F 83 ? ? ? ? ? ? ? 48 8D 0C 5D ? ? ? ? 48 03 CB ? ? ? ? E8 ? ? ? ? 4C 8B C7 49 FF C0 ? ? ? ? ? 75",
                0, 4, IsCallSite: true, TargetBodyDispOffset: 3, TargetBodyDispSize: 4,
                "ViewMatrix call-site: call GetWorldToCameraMatrix"),
        ];

        private static readonly CameraOffsetSig[] FovSigs =
        [
            new("83 B9 ? ? ? ? 02 75 ? F3 0F 10 81 ? ? ? ? C3 F3 0F 10 81 ? ? ? ? C3", 22, 4, IsCallSite: false, 0, 0,
                "GetFieldOfView: cmp [rcx+?],2; movss xmm0,[rcx+FOV]; ret"),
        ];

        private static readonly CameraOffsetSig[] AspectRatioSigs =
        [
            // Camera::ResetAspect tail:
            //   movss [rbx+AspectRatio], xmm1
            //   cmp   dword ptr [rbx+ProjectionType], 2
            //   mov   word  ptr [rbx+...], 101h
            new("F3 0F 11 8B ? ? ? ? 83 BB ? ? ? ? 02 66 C7 83 ? ? ? ? 01 01",
                4, 4, IsCallSite: false, 0, 0,
                "Camera::ResetAspect body: movss [rbx+AspectRatio],xmm1"),
            // Legacy call-site fallback (older builds)
            new("E8 ? ? ? ? F3 44 0F 59 05 ? ? ? ? F3 0F 59 C6",
                0, 4, IsCallSite: true, TargetBodyDispOffset: 4, TargetBodyDispSize: 4,
                "AspectRatio call-site: call get_aspect"),
        ];

        private static void ResolveCameraOffsets()
        {
            var unityBase = Memory.UnityBase;
            if (!unityBase.IsValidVirtualAddress())
                return;

            ApplyResolvedCameraOffset(
                TryResolveCameraOffset(ViewMatrixSigs, unityBase),
                "ViewMatrix", ref Camera.ViewMatrix, ref _viewMatrixSource);
            ApplyResolvedCameraOffset(
                TryResolveCameraOffset(FovSigs, unityBase),
                "FOV", ref Camera.FOV, ref _fovSource);
            ApplyResolvedCameraOffset(
                TryResolveCameraOffset(AspectRatioSigs, unityBase),
                "AspectRatio", ref Camera.AspectRatio, ref _aspectRatioSource);
        }

        private static void ApplyResolvedCameraOffset(
            ResolvedCameraOffset? resolved,
            string fieldName,
            ref uint target,
            ref string source)
        {
            if (resolved.HasValue)
            {
                uint previous = target;
                target = resolved.Value.Offset;
                source = resolved.Value.Source;
                Log.WriteLine(
                    $"[CameraManager] Camera.{fieldName} signature-resolved: " +
                    $"0x{previous:X} -> 0x{target:X} ({source}).");
            }
            else
            {
                Log.WriteLine(
                    $"[CameraManager] Camera.{fieldName} signature resolution failed/ambiguous — " +
                    $"provisional fallback 0x{target:X} requires live validation.");
            }
        }

        private static ResolvedCameraOffset? TryResolveCameraOffset(CameraOffsetSig[] sigs, ulong unityBase)
        {
            uint imageSize = Memory.GetModuleImageSize("UnityPlayer.dll");
            if (imageSize == 0) return null;
            var candidates = new Dictionary<uint, HashSet<string>>();

            foreach (var entry in sigs)
            {
                ulong[] matches;
                try { matches = Memory.FindSignatures(entry.Sig, "UnityPlayer.dll", 256); }
                catch (Exception ex)
                {
                    Log.WriteLine($"[CameraManager] Camera offset sig '{entry.Desc}' failed: {ex.Message}");
                    continue;
                }

                foreach (ulong sigAddr in matches)
                {
                    uint offset;
                    if (entry.IsCallSite)
                    {
                        if (!Memory.TryReadValue<int>(sigAddr + (ulong)entry.OffsetPos + 1, out var callRel32, false))
                            continue;
                        long callTargetSigned = (long)sigAddr + entry.OffsetPos + 5 + callRel32;
                        if (callTargetSigned <= 0) continue;
                        ulong callTarget = (ulong)callTargetSigned;
                        if (callTarget < unityBase || callTarget - unityBase >= imageSize)
                            continue;
                        offset = entry.TargetBodyDispSize switch
                        {
                            1 => Memory.TryReadValue<byte>(callTarget + (ulong)entry.TargetBodyDispOffset, out var b, false) ? b : 0u,
                            4 => Memory.TryReadValue<uint>(callTarget + (ulong)entry.TargetBodyDispOffset, out var u, false) ? u : 0u,
                            _ => 0,
                        };
                    }
                    else
                    {
                        offset = entry.DispSize switch
                        {
                            1 => Memory.TryReadValue<byte>(sigAddr + (ulong)entry.OffsetPos, out var b, false) ? b : 0u,
                            4 => Memory.TryReadValue<uint>(sigAddr + (ulong)entry.OffsetPos, out var u, false) ? u : 0u,
                            _ => 0,
                        };
                    }

                    if (!IsPlausibleCameraStructOffset(offset)) continue;
                    if (!candidates.TryGetValue(offset, out var evidence))
                    {
                        evidence = new HashSet<string>(StringComparer.Ordinal);
                        candidates[offset] = evidence;
                    }
                    evidence.Add(entry.Desc);
                }
            }

            if (candidates.Count == 1)
            {
                var candidate = candidates.First();
                string evidence = string.Join(" + ", candidate.Value);
                return new ResolvedCameraOffset(
                    candidate.Key,
                    $"signature-unique:{evidence}");
            }
            if (candidates.Count > 1)
            {
                Log.WriteLine(
                    "[CameraManager] Camera offset signature candidates are ambiguous: " +
                    string.Join(", ", candidates.Keys.Order().Select(value => $"0x{value:X}")));
            }
            return null;
        }

        #endregion

        #region Camera Offset Cache

        private static bool TryLoadCameraCache()
        {
            try
            {
                if (ArenaProgram.ForceOffsetRescan)
                {
                    Log.WriteLine("[CameraManager] Camera cache bypassed by -force-offset-rescan.");
                    return false;
                }

                if (!File.Exists(CameraCacheFilePath))
                    return false;

                var unityBase = Memory.UnityBase;
                if (!unityBase.IsValidVirtualAddress())
                    return false;

                var (timestamp, sizeOfImage) = Memory.ReadPeFingerprint(unityBase);
                if (timestamp == 0 || sizeOfImage == 0)
                    return false;

                var json = File.ReadAllText(CameraCacheFilePath);
                var cache = JsonSerializer.Deserialize<CameraOffsetCache>(json, _jsonOpts);
                if (cache is null)
                    return false;

                if (cache.SchemaVersion != CameraCacheSchemaVersion)
                {
                    Log.WriteLine($"[CameraManager] Camera cache schema mismatch (have={cache.SchemaVersion}, expected={CameraCacheSchemaVersion}) — will sig-scan.");
                    return false;
                }

                if (cache.UnityPlayerTimestamp != timestamp || cache.UnityPlayerSizeOfImage != sizeOfImage)
                {
                    Log.WriteLine("[CameraManager] Camera cache PE mismatch — will sig-scan.");
                    return false;
                }

                if (cache.AllCamerasRva == 0
                    || cache.AllCamerasRva >= sizeOfImage
                    || !IsPlausibleCameraStructOffsets(cache.ViewMatrix, cache.FOV, cache.AspectRatio)
                    || cache.Sources is null
                    || !cache.Sources.TryGetValue("AllCameras", out var allCamerasSource)
                    || !cache.Sources.TryGetValue("ViewMatrix", out var viewMatrixSource)
                    || !cache.Sources.TryGetValue("FOV", out var fovSource)
                    || !cache.Sources.TryGetValue("AspectRatio", out var aspectRatioSource)
                    || !IsLiveValidatedCameraSource(allCamerasSource)
                    || !IsLiveValidatedCameraSource(viewMatrixSource)
                    || !IsLiveValidatedCameraSource(fovSource)
                    || !IsLiveValidatedCameraSource(aspectRatioSource))
                {
                    Log.WriteLine("[CameraManager] Camera cache lacks complete live-validated provenance — will sig-scan.");
                    return false;
                }

                ulong resolvedAddr = unityBase + cache.AllCamerasRva;
                if (!ValidateAllCamerasAddr(resolvedAddr))
                {
                    Log.WriteLine("[CameraManager] Camera cache AllCameras validation failed — will sig-scan.");
                    return false;
                }

                _allCamerasAddr = resolvedAddr;
                Camera.ViewMatrix = cache.ViewMatrix;
                Camera.FOV = cache.FOV;
                Camera.AspectRatio = cache.AspectRatio;
                _allCamerasSource = allCamerasSource;
                _viewMatrixSource = viewMatrixSource;
                _fovSource = fovSource;
                _aspectRatioSource = aspectRatioSource;

                Log.WriteLine(
                    $"[CameraManager] Provisional offsets restored from exact-PE cache " +
                    $"(VM=0x{cache.ViewMatrix:X}, FOV=0x{cache.FOV:X}, AR=0x{cache.AspectRatio:X}); " +
                    "live camera semantics still required.");
                return true;
            }
            catch (Exception ex)
            {
                Log.WriteLine($"[CameraManager] Cache load failed: {ex.Message}");
                return false;
            }
        }

        private static void SaveCameraCache()
        {
            try
            {
                var unityBase = Memory.UnityBase;
                if (!unityBase.IsValidVirtualAddress() || !_allCamerasAddr.IsValidVirtualAddress())
                    return;

                var (timestamp, sizeOfImage) = Memory.ReadPeFingerprint(unityBase);
                ulong allCamerasRva = _allCamerasAddr - unityBase;
                if (timestamp == 0 || sizeOfImage == 0
                    || allCamerasRva == 0 || allCamerasRva >= sizeOfImage
                    || !ValidateAllCamerasAddr(_allCamerasAddr)
                    || !IsPlausibleCameraStructOffsets(
                        Camera.ViewMatrix, Camera.FOV, Camera.AspectRatio)
                    || _cameraOffsetsPendingLiveValidation
                    || !IsLiveValidatedCameraSource(_allCamerasSource)
                    || !IsLiveValidatedCameraSource(_viewMatrixSource)
                    || !IsLiveValidatedCameraSource(_fovSource)
                    || !IsLiveValidatedCameraSource(_aspectRatioSource))
                {
                    Log.WriteLine("[CameraManager] Camera cache save rejected: incomplete fingerprint/live provenance.");
                    return;
                }

                var cache = new CameraOffsetCache
                {
                    SchemaVersion = CameraCacheSchemaVersion,
                    UnityPlayerTimestamp = timestamp,
                    UnityPlayerSizeOfImage = sizeOfImage,
                    AllCamerasRva = allCamerasRva,
                    ViewMatrix = Camera.ViewMatrix,
                    FOV = Camera.FOV,
                    AspectRatio = Camera.AspectRatio,
                    Sources = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["AllCameras"] = _allCamerasSource,
                        ["ViewMatrix"] = _viewMatrixSource,
                        ["FOV"] = _fovSource,
                        ["AspectRatio"] = _aspectRatioSource,
                    },
                };

                var json = JsonSerializer.Serialize(cache, _jsonOpts);
                Directory.CreateDirectory(Path.GetDirectoryName(CameraCacheFilePath)!);
                string tempPath = CameraCacheFilePath + $".tmp-{Environment.ProcessId}-{Guid.NewGuid():N}";
                try
                {
                    File.WriteAllText(tempPath, json, new UTF8Encoding(false));
                    File.Move(tempPath, CameraCacheFilePath, overwrite: true);
                }
                finally
                {
                    try { if (File.Exists(tempPath)) File.Delete(tempPath); }
                    catch { }
                }
                Log.WriteLine($"[CameraManager] Cache saved → {CameraCacheFilePath}");
            }
            catch (Exception ex)
            {
                Log.WriteLine($"[CameraManager] Cache save failed: {ex.Message}");
            }
        }

        #endregion
    }
}
