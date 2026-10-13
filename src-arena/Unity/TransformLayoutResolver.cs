using eft_dma_radar.Arena.Unity.IL2CPP;

namespace eft_dma_radar.Arena.Unity
{
    /// <summary>
    /// Resolves the native Unity TransformAccess/TransformHierarchy layout by
    /// validating a complete parent chain instead of trusting one build-specific
    /// constant. Arena and EFT mainline use different layouts even on nominally
    /// similar Unity versions.
    /// </summary>
    internal static class TransformLayoutResolver
    {
        internal const int MaxTransformIndex = 4_096;
        private const int MaxParentDepth = 512;
        private const float MaxCoordinateMagnitude = 4_096f;
        private const int RequiredIndependentSamples = 3;

        private readonly record struct Layout(
            string Name,
            uint Hierarchy,
            uint Index,
            uint Vertices,
            uint Indices);

        internal readonly record struct Resolution(
            ulong NativeTransform,
            ulong Hierarchy,
            int TransformIndex,
            ulong Vertices,
            ulong Indices,
            int[] ParentIndices,
            Vector3 WorldPosition);

        // First entry is the Arena layout recovered from the current UnityPlayer
        // disassembly. The remaining layouts are safe fallbacks seen in the local
        // source history and related Unity 6 builds. Every candidate must pass the
        // same structural validation before it can be selected.
        private static readonly Layout[] Layouts =
        [
            new("arena-0x58-0x50", 0x58, 0x60, 0x50, 0xA0),
            new("arena-legacy-local", 0x40, 0x48, 0xA0, 0x80),
            new("eft-silk6", 0x40, 0x48, 0x88, 0x70),
            new("unity6-base", 0x40, 0x48, 0x18, 0x20),
            new("arena-access+legacy-arrays", 0x58, 0x60, 0xA0, 0x80),
            new("arena-access+silk6-arrays", 0x58, 0x60, 0x88, 0x70),
            new("arena-access+base-arrays", 0x58, 0x60, 0x18, 0x20),
            new("legacy-access+arena-arrays", 0x40, 0x48, 0x50, 0xA0),
        ];

        private static int _selectedIndex = -1;
        private static readonly object SelectionSync = new();
        private static readonly Dictionary<int, HashSet<ulong>> CandidateEvidence = new();
        private static long _selectionGeneration;

        internal static string CurrentDescription
        {
            get
            {
                int index = Volatile.Read(ref _selectedIndex);
                if ((uint)index >= (uint)Layouts.Length) return "unresolved";
                var layout = Layouts[index];
                return $"{layout.Name}: access=+0x{layout.Hierarchy:X}/+0x{layout.Index:X}, hierarchy=+0x{layout.Vertices:X}/+0x{layout.Indices:X}";
            }
        }

        internal static void ResetForTargetProcessChange()
        {
            lock (SelectionSync)
            {
                Volatile.Write(ref _selectedIndex, -1);
                _selectionGeneration++;
                CandidateEvidence.Clear();
                ApplyLayout(Layouts[0]);
            }
        }

        internal static void InvalidateSelectedLayout(string reason)
        {
            int previous;
            lock (SelectionSync)
            {
                previous = Volatile.Read(ref _selectedIndex);
                Volatile.Write(ref _selectedIndex, -1);
                _selectionGeneration++;
                CandidateEvidence.Clear();
                ApplyLayout(Layouts[0]);
                Il2CppDumper.MarkFieldSource("Unity.TransformLayout", "unresolved:downstream-invalidated");
            }
            if ((uint)previous < (uint)Layouts.Length)
            {
                Log.WriteLine($"[TransformLayout] Invalidated '{Layouts[previous].Name}': {reason}.");
                RuntimeDiagnostics.Capture("transform-layout-invalidated");
            }
        }

        internal static bool TryResolve(ulong candidate, out Resolution resolution)
        {
            if (TryResolveNative(candidate, out resolution)) return true;

            if (Memory.TryReadPtr(candidate + 0x10, out var inner, false)
                && inner.IsValidVirtualAddress()
                && TryResolveNative(inner, out resolution))
                return true;

            resolution = default;
            return false;
        }

        internal static bool TryResolveNative(ulong nativeTransform, out Resolution resolution)
        {
            resolution = default;
            if (!nativeTransform.IsValidVirtualAddress()) return false;

            long evidenceGeneration = Volatile.Read(ref _selectionGeneration);
            int preferred = Volatile.Read(ref _selectedIndex);
            if ((uint)preferred < (uint)Layouts.Length
                && TryLayout(nativeTransform, Layouts[preferred], out resolution))
                return true;

            for (int i = 0; i < Layouts.Length; i++)
            {
                if (i == preferred) continue;
                if (!TryLayout(nativeTransform, Layouts[i], out resolution)) continue;

                int evidenceCount;
                bool selectedNow = false;
                lock (SelectionSync)
                {
                    // A validation that began before Reset/Invalidate may still
                    // return a coherent one-shot Resolution to its caller, but it
                    // must never repopulate global evidence for the new epoch.
                    if (evidenceGeneration != _selectionGeneration)
                        return true;

                    if (!CandidateEvidence.TryGetValue(i, out var transforms))
                    {
                        transforms = new HashSet<ulong>();
                        CandidateEvidence[i] = transforms;
                    }
                    // Bones in one skeleton share a TransformHierarchy. Counting
                    // native TransformAccess pointers would let three bones from
                    // the same hierarchy satisfy the supposed independent quorum.
                    transforms.Add(resolution.Hierarchy);
                    evidenceCount = transforms.Count;
                    int current = Volatile.Read(ref _selectedIndex);
                    if (evidenceCount >= RequiredIndependentSamples && current == -1)
                    {
                        Volatile.Write(ref _selectedIndex, i);
                        ApplyLayout(Layouts[i]);
                        Il2CppDumper.MarkFieldSource("Unity.TransformLayout", $"runtime-validated-quorum:{Layouts[i].Name}");
                        selectedNow = true;
                    }
                }
                if (selectedNow)
                {
                    Log.WriteLine(
                        $"[TransformLayout] Selected {CurrentDescription} after " +
                        $"{evidenceCount} independent hierarchy validations.");
                    RuntimeDiagnostics.Capture("transform-layout-selected");
                }
                return true;
            }

            return false;
        }

        private static bool TryLayout(ulong nativeTransform, Layout layout, out Resolution resolution)
        {
            resolution = default;

            if (!Memory.TryReadPtr(nativeTransform + layout.Hierarchy, out var hierarchy, false)
                || !hierarchy.IsValidVirtualAddress()
                || !Memory.TryReadValue<int>(nativeTransform + layout.Index, out var transformIndex, false)
                || transformIndex < 0
                || transformIndex > MaxTransformIndex)
                return false;

            if (!Memory.TryReadPtr(hierarchy + layout.Vertices, out var vertices, false)
                || !vertices.IsValidVirtualAddress()
                || !Memory.TryReadPtr(hierarchy + layout.Indices, out var indices, false)
                || !indices.IsValidVirtualAddress())
                return false;

            int count = transformIndex + 1;
            int[] parentIndices;
            TrsX[] trs;
            try
            {
                parentIndices = Memory.ReadArray<int>(indices, count, false);
                trs = Memory.ReadArray<TrsX>(vertices, count, false);
            }
            catch
            {
                return false;
            }

            if (parentIndices is null || trs is null
                || parentIndices.Length != count || trs.Length != count
                || !ValidateParentChain(trs, parentIndices, transformIndex))
                return false;

            var worldPosition = TrsX.ComputeWorldPosition(trs, parentIndices, transformIndex, MaxParentDepth);
            if (!IsFiniteAndBounded(worldPosition)) return false;

            resolution = new Resolution(
                nativeTransform,
                hierarchy,
                transformIndex,
                vertices,
                indices,
                parentIndices,
                worldPosition);
            return true;
        }

        private static bool ValidateParentChain(TrsX[] vertices, int[] parents, int start)
        {
            int current = start;
            for (int depth = 0; depth < MaxParentDepth; depth++)
            {
                if ((uint)current >= (uint)vertices.Length) return false;

                ref readonly var trs = ref vertices[current];
                if (!IsFiniteAndBounded(trs.T)
                    || !IsFiniteAndBounded(trs.S, 100f)
                    || !float.IsFinite(trs.Q.X) || !float.IsFinite(trs.Q.Y)
                    || !float.IsFinite(trs.Q.Z) || !float.IsFinite(trs.Q.W))
                    return false;

                float quaternionNorm = trs.Q.LengthSquared();
                if (quaternionNorm < 0.25f || quaternionNorm > 4f) return false;

                int parent = parents[current];
                if (parent == -1) return true;
                if (parent < 0 || parent >= parents.Length || parent == current) return false;
                current = parent;
            }

            return false;
        }

        private static bool IsFiniteAndBounded(Vector3 value, float max = MaxCoordinateMagnitude) =>
            float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z)
            && MathF.Abs(value.X) <= max && MathF.Abs(value.Y) <= max && MathF.Abs(value.Z) <= max;

        private static void ApplyLayout(Layout layout)
        {
            UnityOffsets.TransformAccess.HierarchyOffset = layout.Hierarchy;
            UnityOffsets.TransformAccess.IndexOffset = layout.Index;
            UnityOffsets.TransformHierarchy.VerticesOffset = layout.Vertices;
            UnityOffsets.TransformHierarchy.IndicesOffset = layout.Indices;
        }
    }
}
