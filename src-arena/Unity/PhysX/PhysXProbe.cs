using System.Collections.Generic;
using System.Linq;
using eft_dma_radar.Arena.DMA;

namespace eft_dma_radar.Arena.Unity.PhysX
{
    /// <summary>
    /// One-shot PhysX SDK pointer discovery probe.
    ///
    /// <para>
    /// Walks <c>UnityPlayer.dll</c> looking for RIP-relative global pointer loads
    /// (<c>mov rax,[rip+rel32]</c>), then validates each unique resolved RVA by
    /// dereferencing it and checking that the resulting object is shaped like an
    /// <c>NpPhysics</c> singleton: a scene array of plausible size whose first
    /// entry contains a plausible rigid-actor count.
    /// </para>
    ///
    /// <para>
    /// Designed for <b>validation-first</b> behavior — the sig pattern is
    /// intentionally broad (matches thousands of global loads). The structural
    /// walk is what decides which candidate is real. This makes the probe resilient
    /// to engine patches that move code around without changing struct layouts.
    /// </para>
    ///
    /// <para>
    /// Triggered explicitly (F9). Never runs automatically. Pure read-only — cannot
    /// disturb the radar or the game.
    /// </para>
    /// </summary>
    internal static class PhysXProbe
    {
        // ── Configuration ────────────────────────────────────────────────────

        // Bounds on what "plausibly NpPhysics" looks like. These are loose enough
        // to accept anything we might see in Arena while still rejecting random
        // unrelated globals.
        private const uint MaxPlausibleScenes = 8;
        private const uint MaxPlausibleActorsPerScene = 200_000;

        // Struct offsets we expect inside NpPhysics / NpScene under PhysX 4.1.
        // We READ these; wrong values just mean "this candidate fails validation"
        // — never a crash. The probe is the safe place to learn whether these
        // hold on the live engine build.
        private const uint NpPhysics_SceneArrayData = 0x08;
        private const uint NpPhysics_SceneArraySize = 0x10;
        private const uint NpScene_RigidActorsSize  = 0x23D0;

        // Pattern: any 7-byte `mov rax,[rip+rel32]` instruction. The fourth byte
        // onward is the rel32 displacement we decode.
        private const string PtrLoadPattern   = "48 8B 05 ? ? ? ?";
        private const int    PtrLoadInstrLen  = 7;
        private const int    PtrLoadRelOffset = 3;

        // Hard cap on scan matches so a worst-case probe finishes promptly.
        private const int MaxMatches = 65536;

        // ── Entry point ──────────────────────────────────────────────────────

        /// <summary>
        /// Runs the probe and logs all validated SDK pointer candidates. Safe to
        /// call multiple times — the probe never mutates state.
        /// </summary>
        public static uint? Run()
        {
            var unityBase = Memory.UnityBase;
            uint processId = Memory.ProcessId;
            long attachGeneration = Memory.AttachGeneration;
            if (unityBase == 0)
            {
                Log.WriteLine("[PhysXProbe] UnityBase=0 (game not attached). Skipping.");
                return null;
            }

            Log.WriteLine($"[PhysXProbe] Scanning UnityPlayer.dll @ 0x{unityBase:X} for NpPhysics SDK pointer...");

            ulong[] sigAddrs;
            try
            {
                sigAddrs = Memory.FindSignatures(PtrLoadPattern, "UnityPlayer.dll", MaxMatches);
            }
            catch (Exception ex)
            {
                Log.WriteLine($"[PhysXProbe] Sig-scan failed: {ex.Message}");
                return null;
            }

            if (!IsSameTarget(processId, attachGeneration, unityBase))
            {
                Log.WriteLine("[PhysXProbe] Target changed during signature scan; result discarded.");
                return null;
            }

            if (sigAddrs.Length == 0)
            {
                Log.WriteLine("[PhysXProbe] No ptr-load sites found — sig pattern may need adjustment.");
                return null;
            }

            Log.WriteLine($"[PhysXProbe] Found {sigAddrs.Length} ptr-load sites; decoding unique RVAs...");

            // Dedupe by resolved RVA — many call sites can decode to the same global.
            var seenRvas = new HashSet<ulong>(sigAddrs.Length);
            var uniqueRvas = new List<ulong>();
            ulong unityImageSize = GetUnityImageSize(unityBase);
            foreach (var sigAddr in sigAddrs)
            {
                var rva = DecodeRipRelativeRva(sigAddr, unityBase);
                if (rva != 0 && rva < unityImageSize && seenRvas.Add(rva))
                    uniqueRvas.Add(rva);
            }

            Log.WriteLine($"[PhysXProbe] {uniqueRvas.Count} unique global RVAs to validate.");

            // Collect every survivor first, then aggregate by NpPhysics pointer. Many
            // RVAs can decode to the same singleton (Unity stores the SDK pointer in
            // several globals reached from different code paths); the singleton with
            // the most agreeing RVAs is the strongest candidate. We also flag any
            // candidate whose "NpPhysics" pointer lands inside UnityPlayer.dll itself
            // — those are coincidental hits walking through data-section bytes, not
            // a real heap-allocated PhysX singleton.
            var hits = new List<HitRecord>();
            ulong unityEnd = unityBase + unityImageSize;

            for (int candidateIndex = 0; candidateIndex < uniqueRvas.Count; candidateIndex++)
            {
                if ((candidateIndex & 0x1FF) == 0
                    && !IsSameTarget(processId, attachGeneration, unityBase))
                {
                    Log.WriteLine("[PhysXProbe] Target changed during candidate validation; result discarded.");
                    return null;
                }

                var rva = uniqueRvas[candidateIndex];
                if (TryValidate(unityBase + rva, out var npPhysics, out var sceneCount, out var firstSceneActors))
                {
                    bool inModule = npPhysics >= unityBase && npPhysics < unityEnd;
                    hits.Add(new HitRecord(rva, npPhysics, sceneCount, firstSceneActors, inModule));
                }
            }

            if (hits.Count == 0)
            {
                Log.WriteLine("[PhysXProbe] No candidate passed validation. " +
                              "Either no match in this build, or the struct offsets we tried " +
                              "(NpPhysics+0x08/0x10, NpScene+0x23D0) don't apply to Unity 6.");
                return null;
            }

            // Group by resolved NpPhysics pointer; sort groups so the strongest
            // candidate is reported last (i.e. ends the log with the answer).
            // Strength ordering:
            //   • valid (non-in-module) groups before in-module groups
            //   • more agreeing RVAs first
            //   • larger actor count first (real scene beats empty subscene)
            var groups = hits
                .GroupBy(h => h.NpPhysics)
                .Select(g => new
                {
                    NpPhysics = g.Key,
                    Rvas = g.OrderBy(h => h.Rva).ToArray(),
                    InModule = g.First().InModule,
                    Scenes = g.First().Scenes,
                    Actors = g.First().FirstSceneActors,
                })
                .OrderBy(g => g.InModule)
                .ThenByDescending(g => g.Rvas.Length)
                .ThenByDescending(g => g.Actors)
                .ToArray();

            foreach (var g in groups)
            {
                string verdict;
                if (g.InModule)
                    verdict = "SUSPECT (pointer is inside UnityPlayer.dll — likely a false positive)";
                else if (g.Rvas.Length >= 2 && g.Actors > 0)
                    verdict = "LIKELY (multiple RVAs agree + non-empty scene)";
                else if (g.Actors > 0)
                    verdict = "POSSIBLE (single RVA, non-empty scene)";
                else
                    verdict = "WEAK (single RVA, scene0 has zero actors)";

                var rvaList = string.Join(", ", g.Rvas.Select(h => $"0x{h.Rva:X8}"));
                Log.WriteLine(
                    $"[PhysXProbe] {verdict}  np_physics=0x{g.NpPhysics:X}  " +
                    $"scenes={g.Scenes}  maxScene.actors={g.Actors}  rvas=[{rvaList}]");
            }

            // Walk ranked groups until one passes the deeper actor/type/shape
            // validator. The probe remains report-only; SceneCache commits only
            // after its complete geometry walk succeeds.
            var ranked = groups
                .Where(g => !g.InModule)
                .OrderByDescending(g => g.Rvas.Length)
                .ThenByDescending(g => g.Actors)
                .ToArray();
            foreach (var candidate in ranked)
            {
                bool strong = candidate.Rvas.Length >= 2 && candidate.Actors > 0;
                if (strong && candidate.Rvas[0].Rva <= uint.MaxValue
                    && IsSameTarget(processId, attachGeneration, unityBase)
                    && TryValidateRvaDeep((uint)candidate.Rvas[0].Rva, out _, out var deepReason))
                {
                    uint resolved = (uint)candidate.Rvas[0].Rva;
                    Log.WriteLine(
                        $"[PhysXProbe] DEEP-VALIDATED report-only candidate: 0x{resolved:X8} " +
                        $"({candidate.Rvas.Length} agreeing globals, maxScene.actors={candidate.Actors}, {deepReason}).");
                    return resolved;
                }
                Log.WriteLine(
                    $"[PhysXProbe] Candidate 0x{candidate.Rvas[0].Rva:X8} was not eligible for commit " +
                    $"(strong={strong}, targetCurrent={IsSameTarget(processId, attachGeneration, unityBase)})." );
            }
            Log.WriteLine("[PhysXProbe] DONE — no ranked candidate passed deep validation. " +
                          "Re-check struct offsets for Unity 6 before adopting any RVA.");
            return null;
        }

        /// <summary>
        /// Structurally validates one UnityPlayer-relative SDK pointer RVA and
        /// returns the resolved NpPhysics singleton.
        /// </summary>
        internal static bool TryValidateRva(uint rva, out ulong npPhysics)
        {
            npPhysics = 0;
            ulong unityBase = Memory.UnityBase;
            return unityBase != 0
                && rva > 0
                && rva < GetUnityImageSize(unityBase)
                && TryValidate(unityBase + rva, out npPhysics, out _, out _);
        }

        /// <summary>
        /// Deep validation used before a candidate may become the live RVA. In
        /// addition to scene counts, samples actor arrays and requires plausible
        /// rigid concrete types plus at least one shaped actor.
        /// </summary>
        internal static bool TryValidateRvaDeep(uint rva, out ulong npPhysics, out string reason)
        {
            npPhysics = 0;
            reason = "unvalidated";
            ulong unityBase = Memory.UnityBase;
            if (unityBase == 0 || rva == 0 || rva >= GetUnityImageSize(unityBase)
                || !TryValidate(unityBase + rva, out var candidate, out var sceneCount, out _))
            {
                reason = "singleton/scene validation failed";
                return false;
            }

            if (!Memory.TryReadPtr(candidate + PhysXOffsets.NpPhysics_SceneArrayData, out var scenesArray, false)
                || !scenesArray.IsValidVirtualAddress())
            {
                reason = "scene array pointer invalid";
                return false;
            }

            int rigidActors = 0;
            int shapedActors = 0;
            for (uint sceneIndex = 0; sceneIndex < sceneCount; sceneIndex++)
            {
                if (!Memory.TryReadPtr(scenesArray + sceneIndex * 8, out var scene, false)
                    || !scene.IsValidVirtualAddress()
                    || !Memory.TryReadValue<uint>(scene + PhysXOffsets.NpScene_RigidActorsSize, out var actorCount, false)
                    || actorCount == 0 || actorCount > MaxPlausibleActorsPerScene
                    || !Memory.TryReadPtr(scene + PhysXOffsets.NpScene_RigidActorsData, out var actorArray, false)
                    || !actorArray.IsValidVirtualAddress())
                    continue;

                int samples = (int)Math.Min(actorCount, 128u);
                for (int sample = 0; sample < samples; sample++)
                {
                    ulong actorIndex = samples == 1
                        ? 0
                        : (ulong)sample * (actorCount - 1) / (ulong)(samples - 1);
                    if (!Memory.TryReadPtr(actorArray + actorIndex * 8, out var actor, false)
                        || !actor.IsValidVirtualAddress()
                        || !Memory.TryReadValue<ushort>(actor + PhysXOffsets.PxBase_ConcreteType, out var rawType, false)
                        || !((PxConcreteType)rawType).IsRigidActor())
                        continue;

                    rigidActors++;
                    if (Memory.TryReadValue<ushort>(
                            actor + PhysXOffsets.PxRigidActor_ShapeManager + PhysXOffsets.NpShapeManager_ShapesCount,
                            out var shapeCount, false)
                        && shapeCount is > 0 and <= 256)
                        shapedActors++;
                }
            }

            if (rigidActors < 2 || shapedActors < 1)
            {
                reason = $"actor structure too weak (rigid={rigidActors}, shaped={shapedActors})";
                return false;
            }

            npPhysics = candidate;
            reason = $"rigid={rigidActors}, shaped={shapedActors}";
            return true;
        }

        /// <summary>
        /// One validated candidate from the scan. Captured before aggregation so
        /// we can group / rank without re-reading memory.
        /// </summary>
        private sealed record HitRecord(
            ulong Rva,
            ulong NpPhysics,
            uint Scenes,
            uint FirstSceneActors,
            bool InModule);

        /// <summary>
        /// Pulls UnityPlayer.dll's image size from the loaded-module table so we
        /// can detect candidates whose "NpPhysics" pointer is actually inside the
        /// DLL itself (those are coincidental data-section hits, not real PhysX).
        /// Falls back to a generous 64 MB envelope if the live size isn't available
        /// — never under-reports, so the in-module flag stays conservative.
        /// </summary>
        private static ulong GetUnityImageSize(ulong unityBase)
        {
            uint size = Memory.GetModuleImageSize("UnityPlayer.dll");
            return size > 0 ? size : 0x4000000UL;
        }

        private static bool IsSameTarget(uint processId, long attachGeneration, ulong unityBase) =>
            processId != 0
            && processId == Memory.ProcessId
            && attachGeneration == Memory.AttachGeneration
            && unityBase != 0
            && unityBase == Memory.UnityBase;

        // ── Internals ────────────────────────────────────────────────────────

        /// <summary>
        /// Decodes a 7-byte <c>mov rax,[rip+rel32]</c> at <paramref name="sigAddr"/>
        /// into the target global's RVA inside <paramref name="moduleBase"/>.
        /// Returns 0 if the read failed or the target falls outside the module.
        /// </summary>
        private static ulong DecodeRipRelativeRva(ulong sigAddr, ulong moduleBase)
        {
            if (!Memory.TryReadValue<int>(sigAddr + (ulong)PtrLoadRelOffset, out var rel, false))
                return 0;
            ulong targetVa = sigAddr + (ulong)PtrLoadInstrLen + (ulong)(long)rel;
            return targetVa > moduleBase ? targetVa - moduleBase : 0;
        }

        /// <summary>
        /// Walks a candidate global at <paramref name="storageVa"/> as if it were
        /// the storage for an <c>NpPhysics*</c> singleton. Returns true with the
        /// resolved pointer and scene/actor counts when the structure is plausible.
        /// </summary>
        private static bool TryValidate(
            ulong storageVa,
            out ulong npPhysics,
            out uint sceneCount,
            out uint firstSceneActorCount)
        {
            npPhysics = 0;
            sceneCount = 0;
            firstSceneActorCount = 0;

            // 1. The storage must hold a valid VA.
            if (!Memory.TryReadPtr(storageVa, out var sdk, false) || !sdk.IsValidVirtualAddress())
                return false;

            // 2. The scene-array data pointer must be valid and the size small.
            if (!Memory.TryReadPtr(sdk + NpPhysics_SceneArrayData, out var scenesArrayPtr, false))
                return false;
            if (!Memory.TryReadValue<uint>(sdk + NpPhysics_SceneArraySize, out var nScenes, false))
                return false;
            if (nScenes == 0 || nScenes > MaxPlausibleScenes)
                return false;
            if (!scenesArrayPtr.IsValidVirtualAddress())
                return false;

            // 3. At least one scene must expose a plausible non-empty actor list.
            // Arena commonly keeps auxiliary scene[0] empty, so validating only
            // the first scene creates both false negatives and weak candidates.
            uint maxActors = 0;
            for (uint i = 0; i < nScenes; i++)
            {
                if (!Memory.TryReadPtr(scenesArrayPtr + i * 8, out var scene, false)
                    || !scene.IsValidVirtualAddress()
                    || !Memory.TryReadValue<uint>(scene + NpScene_RigidActorsSize, out var nActors, false)
                    || nActors > MaxPlausibleActorsPerScene)
                    continue;
                if (nActors > maxActors) maxActors = nActors;
            }
            if (maxActors == 0) return false;

            npPhysics = sdk;
            sceneCount = nScenes;
            firstSceneActorCount = maxActors;
            return true;
        }
    }
}
