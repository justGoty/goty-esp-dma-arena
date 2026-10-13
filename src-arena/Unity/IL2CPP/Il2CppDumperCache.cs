#pragma warning disable IDE0130

using System.IO;

namespace eft_dma_radar.Arena.Unity.IL2CPP
{
    public static partial class Il2CppDumper
    {
        private static readonly string CacheDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "eft-dma-radar-arena");

        private static readonly string CacheFilePath = Path.Combine(CacheDirectory, "il2cpp_offsets.json");
        private static readonly string DiagnosticsFilePath = Path.Combine(CacheDirectory, "offset-diagnostics-latest.json");

        // v3 stores only metadata/signature-resolved fields, requires an exact PE
        // fingerprint on every load path, and records per-field provenance.
        private const int CacheSchemaVersion = 3;
        private const int DiagnosticsSchemaVersion = 4;

        private static readonly Guid RadarAssemblyMvid =
            typeof(Il2CppDumper).Assembly.ManifestModule.ModuleVersionId;

        private sealed class OffsetCache
        {
            public int SchemaVersion { get; set; }
            public Guid RadarAssemblyMvid { get; set; }
            public ulong TypeInfoTableRva { get; set; }
            public uint GameAssemblyTimestamp { get; set; }
            public uint GameAssemblySizeOfImage { get; set; }
            public Dictionary<string, string> Fields { get; set; } = new(StringComparer.Ordinal);
            public Dictionary<string, string> Sources { get; set; } = new(StringComparer.Ordinal);
        }

        private static readonly JsonSerializerOptions _jsonOpts = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        // Captured before the first cache/live resolution mutates SDK.Offsets.
        // This makes a target-process restart deterministic: a partial resolver can
        // never inherit a value from the previous process generation.
        private static readonly Dictionary<string, string> _fallbackFields = CollectAllMutableFields();
        private static readonly ConcurrentDictionary<string, string> _fieldSources =
            new(StringComparer.Ordinal);

        internal static void ResetCachedOffsetsToFallbacks()
        {
            foreach (var (key, value) in _fallbackFields)
                TrySetRawField(key, value);

            _fieldSources.Clear();
            foreach (var key in _fallbackFields.Keys)
                _fieldSources[key] = "fallback";
        }

        internal static void MarkFieldSource(string key, string source)
        {
            if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(source))
                _fieldSources[key] = source;
        }

        internal static IReadOnlyDictionary<string, string> GetFieldSourcesSnapshot() =>
            new Dictionary<string, string>(_fieldSources, StringComparer.Ordinal);

        internal static IReadOnlyList<string> GetMissingCoreOffsetKeys()
        {
            return GetMissingKeys(GetCoreRequiredOffsetKeys(), GetVerifiedOffsetKeys());
        }

        internal static IReadOnlyList<string> GetMissingOptionalOffsetKeys() =>
            GetMissingKeys(GetOptionalOffsetKeys(), GetVerifiedOffsetKeys());

        internal static void SaveCache()
        {
            try
            {
                var (timestamp, sizeOfImage) = Memory.ReadPeFingerprint(Memory.GameAssemblyBase);
                if (timestamp == 0 || sizeOfImage == 0)
                {
                    WriteOffsetDiagnostics("live-resolution-invalid-pe-fingerprint");
                    Log.WriteLine(
                        "[Il2CppDumper] Live result has no valid GameAssembly PE fingerprint; " +
                        "preserving the last complete reusable cache.");
                    return;
                }
                var fields = CollectVerifiedFields();
                var sources = fields.Keys.ToDictionary(
                    key => key,
                    key => _fieldSources.TryGetValue(key, out var source) ? source : "resolved",
                    StringComparer.Ordinal);

                var missingCore = GetMissingKeys(GetCoreRequiredOffsetKeys(), fields.Keys);
                var missingOptional = GetMissingKeys(GetOptionalOffsetKeys(), fields.Keys);
                WriteOffsetDiagnostics(missingCore.Count > 0
                    ? "live-resolution-core-incomplete"
                    : missingOptional.Count > 0
                        ? "live-resolution-core-complete-degraded"
                        : "live-resolution-complete");

                if (missingCore.Count > 0)
                {
                    Log.WriteLine(
                        $"[Il2CppDumper] Live result lacks {missingCore.Count} core field(s); " +
                        "preserving the last complete reusable cache.");
                    return;
                }

                if (missingOptional.Count > 0
                    && HasMoreCompleteCompatibleCache(timestamp, sizeOfImage, fields.Keys))
                {
                    Log.WriteLine(
                        "[Il2CppDumper] Current live result is a strict subset of the existing " +
                        "compatible cache; preserving the more complete cache.");
                    return;
                }

                var cache = new OffsetCache
                {
                    SchemaVersion = CacheSchemaVersion,
                    RadarAssemblyMvid = RadarAssemblyMvid,
                    TypeInfoTableRva = SDK.Offsets.Special.TypeInfoTableRva,
                    GameAssemblyTimestamp = timestamp,
                    GameAssemblySizeOfImage = sizeOfImage,
                    Fields = fields,
                    Sources = sources,
                };

                Directory.CreateDirectory(CacheDirectory);
                WriteJsonAtomic(CacheFilePath, JsonSerializer.Serialize(cache, _jsonOpts));

                Log.WriteLine(
                    $"[Il2CppDumper] Verified cache saved ({fields.Count} fields, " +
                    $"optional missing={missingOptional.Count}): {CacheFilePath}");
            }
            catch (Exception ex)
            {
                Log.WriteLine($"[Il2CppDumper] Cache save FAILED: {ex.Message}");
            }
        }

        internal static bool TryLoadCache(ulong expectedRva) =>
            TryLoadCacheCore(Memory.GameAssemblyBase, expectedRva, "resolved-rva cache");

        internal static bool TryFastLoadCache(ulong gaBase) =>
            TryLoadCacheCore(gaBase, expectedRva: null, "PE fast cache");

        private static bool TryLoadCacheCore(ulong gaBase, ulong? expectedRva, string mode)
        {
            try
            {
                if (!File.Exists(CacheFilePath))
                {
                    Log.WriteLine("[Il2CppDumper] No verified cache file found — live dump required.");
                    return false;
                }

                var (timestamp, sizeOfImage) = Memory.ReadPeFingerprint(gaBase);
                if (timestamp == 0 || sizeOfImage == 0)
                {
                    Log.WriteLine("[Il2CppDumper] Cannot fingerprint GameAssembly — cache rejected.");
                    return false;
                }

                var json = File.ReadAllText(CacheFilePath);
                var cache = JsonSerializer.Deserialize<OffsetCache>(json, _jsonOpts);
                if (cache is null || cache.Fields is null || cache.Fields.Count == 0
                    || cache.Sources is null || cache.Sources.Count != cache.Fields.Count)
                {
                    Log.WriteLine("[Il2CppDumper] Cache file is empty or corrupt.");
                    return false;
                }
                if (cache.SchemaVersion != CacheSchemaVersion)
                {
                    Log.WriteLine($"[Il2CppDumper] Cache schema mismatch ({cache.SchemaVersion} != {CacheSchemaVersion}) — live dump required.");
                    return false;
                }
                if (cache.RadarAssemblyMvid != RadarAssemblyMvid)
                {
                    Log.WriteLine(
                        $"[Il2CppDumper] Cache resolver MVID mismatch ({cache.RadarAssemblyMvid} != {RadarAssemblyMvid}) — live dump required.");
                    return false;
                }
                if (cache.GameAssemblyTimestamp != timestamp || cache.GameAssemblySizeOfImage != sizeOfImage)
                {
                    Log.WriteLine(
                        $"[Il2CppDumper] PE fingerprint mismatch: cache=0x{cache.GameAssemblyTimestamp:X8}/0x{cache.GameAssemblySizeOfImage:X}, " +
                        $"live=0x{timestamp:X8}/0x{sizeOfImage:X} — live dump required.");
                    return false;
                }
                if (cache.TypeInfoTableRva == 0 ||
                    (expectedRva.HasValue && cache.TypeInfoTableRva != expectedRva.Value))
                {
                    Log.WriteLine(
                        $"[Il2CppDumper] Cache TypeInfo RVA mismatch: cached=0x{cache.TypeInfoTableRva:X}, " +
                        $"expected={(expectedRva.HasValue ? $"0x{expectedRva.Value:X}" : "non-zero")} — live dump required.");
                    return false;
                }

                var allowed = GetCacheableOffsetKeys();
                foreach (var (key, value) in cache.Fields)
                {
                    if (!allowed.Contains(key) || !IsPlausibleResolvedValue(key, value, sizeOfImage)
                        || !cache.Sources.TryGetValue(key, out var source)
                        || !IsVerifiedSource(source))
                    {
                        Log.WriteLine($"[Il2CppDumper] Cache field rejected: {key}={value}.");
                        return false;
                    }
                }

                if (!cache.Fields.TryGetValue("Special.TypeInfoTableRva", out var tableRvaRaw)
                    || !ulong.TryParse(tableRvaRaw, out var tableRvaFromFields)
                    || tableRvaFromFields != cache.TypeInfoTableRva)
                {
                    Log.WriteLine("[Il2CppDumper] Cache TypeInfo RVA fields disagree — live dump required.");
                    return false;
                }

                var missingCore = GetMissingKeys(GetCoreRequiredOffsetKeys(), cache.Fields.Keys);
                if (missingCore.Count > 0)
                {
                    Log.WriteLine($"[Il2CppDumper] Cache lacks core metadata fields: {string.Join(", ", missingCore)} — live dump required.");
                    return false;
                }

                var missingOptional = GetMissingKeys(GetOptionalOffsetKeys(), cache.Fields.Keys);
                if (!IsCoverageAcceptableForStartup(cache.Fields.Keys, ArenaProgram.RequireAllOffsets))
                {
                    Log.WriteLine(
                        $"[Il2CppDumper] Strict coverage requested, but cache lacks optional/feature fields: " +
                        $"{string.Join(", ", missingOptional)} — live dump required.");
                    return false;
                }

                ResetCachedOffsetsToFallbacks();
                int applied = 0;
                foreach (var (key, value) in cache.Fields)
                {
                    if (!TrySetRawField(key, value)) continue;
                    string original = cache.Sources[key];
                    MarkFieldSource(key, $"cache:{original}");
                    applied++;
                }

                if (applied != cache.Fields.Count)
                {
                    Log.WriteLine($"[Il2CppDumper] Cache application incomplete ({applied}/{cache.Fields.Count}) — live dump required.");
                    ResetCachedOffsetsToFallbacks();
                    return false;
                }

                _lastResolutionMode = mode;
                Log.WriteLine(
                    $"[Il2CppDumper] Cache accepted ({mode}) — {applied} verified fields, " +
                    $"PE=0x{timestamp:X8}/0x{sizeOfImage:X}.");
                WriteOffsetDiagnostics(mode);
                return true;
            }
            catch (Exception ex)
            {
                Log.WriteLine($"[Il2CppDumper] Cache load FAILED: {ex.Message}");
                ResetCachedOffsetsToFallbacks();
                return false;
            }
        }

        internal static void WriteOffsetDiagnostics(string status)
        {
            try
            {
                var (timestamp, sizeOfImage) = Memory.GameAssemblyBase.IsValidVirtualAddress()
                    ? Memory.ReadPeFingerprint(Memory.GameAssemblyBase)
                    : (0u, 0u);
                var values = CollectAllMutableFields();
                var coreRequired = GetCoreRequiredOffsetKeys();
                var optional = GetOptionalOffsetKeys();
                var cacheable = GetCacheableOffsetKeys();
                var fields = values
                    .OrderBy(kvp => kvp.Key, StringComparer.Ordinal)
                    .Select(kvp => new
                    {
                        Name = kvp.Key,
                        Value = kvp.Value,
                        Hex = ToHex(kvp.Value),
                        Source = _fieldSources.TryGetValue(kvp.Key, out var source) ? source : "fallback",
                        // Legacy field: v3 diagnostics treated the entire cacheable schema as required.
                        Required = cacheable.Contains(kvp.Key),
                        CoreRequired = coreRequired.Contains(kvp.Key),
                        Optional = optional.Contains(kvp.Key),
                        RuntimeResolvable = cacheable.Contains(kvp.Key),
                    })
                    .ToArray();

                var report = new
                {
                    SchemaVersion = DiagnosticsSchemaVersion,
                    CacheSchemaVersion,
                    GeneratedUtc = DateTime.UtcNow,
                    Status = status,
                    ResolutionMode = _lastResolutionMode,
                    ProcessId = Memory.ProcessId,
                    AttachGeneration = Memory.AttachGeneration,
                    GameAssemblyTimestamp = $"0x{timestamp:X8}",
                    GameAssemblySizeOfImage = $"0x{sizeOfImage:X}",
                    TypeInfoTableRva = $"0x{SDK.Offsets.Special.TypeInfoTableRva:X}",
                    // Legacy field: retain the v3 all-schema meaning for existing consumers.
                    MissingCritical = GetMissingKeys(
                        cacheable,
                        fields.Where(f => IsVerifiedSource(f.Source)
                            && IsPlausibleResolvedValue(f.Name, f.Value, sizeOfImage))
                              .Select(f => f.Name)),
                    MissingCore = GetMissingKeys(
                        coreRequired,
                        fields.Where(f => IsVerifiedSource(f.Source)
                            && IsPlausibleResolvedValue(f.Name, f.Value, sizeOfImage))
                              .Select(f => f.Name)),
                    MissingOptional = GetMissingKeys(
                        optional,
                        fields.Where(f => IsVerifiedSource(f.Source)
                            && IsPlausibleResolvedValue(f.Name, f.Value, sizeOfImage))
                              .Select(f => f.Name)),
                    Fields = fields,
                };

                Directory.CreateDirectory(CacheDirectory);
                WriteJsonAtomic(DiagnosticsFilePath, JsonSerializer.Serialize(report, _jsonOpts));
                Log.WriteLine($"[Il2CppDumper] Offset diagnostics → {DiagnosticsFilePath}");
            }
            catch (Exception ex)
            {
                Log.WriteLine($"[Il2CppDumper] Offset diagnostics write failed: {ex.Message}");
            }
        }

        private static Dictionary<string, string> CollectVerifiedFields()
        {
            var all = CollectAllMutableFields();
            var allowed = GetCacheableOffsetKeys();
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            uint imageSize = Memory.GetModuleImageSize("GameAssembly.dll");
            foreach (var (key, value) in all)
            {
                if (!allowed.Contains(key)) continue;
                if (!_fieldSources.TryGetValue(key, out var source)) continue;
                if (!IsVerifiedSource(source)) continue;
                if (!IsPlausibleResolvedValue(key, value, imageSize)) continue;
                result[key] = value;
            }
            return result;
        }

        private static Dictionary<string, string> CollectAllMutableFields()
        {
            var result = new Dictionary<string, string>(256, StringComparer.Ordinal);
            var offsetsType = typeof(SDK.Offsets);
            const BindingFlags bf = BindingFlags.Public | BindingFlags.Static;
            foreach (var nested in offsetsType.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
            {
                foreach (var fi in nested.GetFields(bf))
                {
                    if (fi.IsLiteral) continue;
                    var raw = fi.GetValue(null);
                    string? value = raw switch
                    {
                        uint[] arr when arr.Length > 0 => arr[0].ToString(),
                        uint u => u.ToString(),
                        ulong ul => ul.ToString(),
                        int i => i.ToString(),
                        _ => null,
                    };
                    if (value is not null)
                        result[$"{nested.Name}.{fi.Name}"] = value;
                }
            }
            return result;
        }

        private static bool TrySetRawField(string key, string rawValue)
        {
            var dot = key.IndexOf('.');
            if (dot <= 0 || dot == key.Length - 1) return false;

            var nested = typeof(SDK.Offsets).GetNestedType(
                key[..dot], BindingFlags.Public | BindingFlags.NonPublic);
            var fi = nested?.GetField(key[(dot + 1)..], BindingFlags.Public | BindingFlags.Static);
            if (fi is null || fi.IsLiteral) return false;

            try
            {
                if (fi.FieldType == typeof(uint) && uint.TryParse(rawValue, out var uv)) fi.SetValue(null, uv);
                else if (fi.FieldType == typeof(ulong) && ulong.TryParse(rawValue, out var ulv)) fi.SetValue(null, ulv);
                else if (fi.FieldType == typeof(int) && int.TryParse(rawValue, out var iv)) fi.SetValue(null, iv);
                else if (fi.FieldType == typeof(uint[]) && uint.TryParse(rawValue, out var av))
                {
                    var arr = (uint[]?)fi.GetValue(null);
                    if (arr is not { Length: > 0 }) return false;
                    arr[0] = av;
                }
                else return false;
                return true;
            }
            catch { return false; }
        }

        private static bool IsPlausibleResolvedValue(string key, string rawValue, uint imageSize)
        {
            if (!ulong.TryParse(rawValue, out var value)) return false;
            if (key == "Special.TypeInfoTableRva") return imageSize > 0 && value > 0 && value < imageSize;
            if (key == "Special.GamePlayerOwner_TypeIndex") return value > 0 && value < MaxClasses;
            if (key.EndsWith("_RVA", StringComparison.Ordinal)) return imageSize > 0 && value > 0 && value < imageSize;
            return value > 0 && value <= 0x10000;
        }

        private static bool IsVerifiedSource(string source)
        {
            if (string.IsNullOrWhiteSpace(source)) return false;
            return !source.Contains("fallback", StringComparison.OrdinalIgnoreCase)
                && !source.Contains("unresolved", StringComparison.OrdinalIgnoreCase);
        }

        private static IEnumerable<string> GetVerifiedOffsetKeys()
        {
            var values = CollectAllMutableFields();
            uint imageSize = Memory.GetModuleImageSize("GameAssembly.dll");
            return _fieldSources
                .Where(kvp => IsVerifiedSource(kvp.Value)
                    && values.TryGetValue(kvp.Key, out var value)
                    && IsPlausibleResolvedValue(kvp.Key, value, imageSize))
                .Select(kvp => kvp.Key)
                .ToArray();
        }

        private static bool HasMoreCompleteCompatibleCache(
            uint timestamp,
            uint sizeOfImage,
            IEnumerable<string> liveKeys)
        {
            try
            {
                if (!File.Exists(CacheFilePath)) return false;
                var existing = JsonSerializer.Deserialize<OffsetCache>(File.ReadAllText(CacheFilePath), _jsonOpts);
                if (existing is null
                    || existing.SchemaVersion != CacheSchemaVersion
                    || existing.RadarAssemblyMvid != RadarAssemblyMvid
                    || existing.GameAssemblyTimestamp != timestamp
                    || existing.GameAssemblySizeOfImage != sizeOfImage
                    || existing.TypeInfoTableRva != SDK.Offsets.Special.TypeInfoTableRva
                    || existing.Fields is null
                    || existing.Sources is null
                    || existing.Sources.Count != existing.Fields.Count)
                    return false;

                var allowed = GetCacheableOffsetKeys();
                foreach (var (key, value) in existing.Fields)
                {
                    if (!allowed.Contains(key)
                        || !IsPlausibleResolvedValue(key, value, sizeOfImage)
                        || !existing.Sources.TryGetValue(key, out var source)
                        || !IsVerifiedSource(source))
                        return false;
                }

                var live = new HashSet<string>(liveKeys, StringComparer.Ordinal);
                return !ShouldReplaceCompatibleCache(existing.Fields.Keys, live);
            }
            catch
            {
                return false;
            }
        }

        internal static List<string> GetMissingKeys(IEnumerable<string> required, IEnumerable<string> presentKeys)
        {
            var present = new HashSet<string>(presentKeys, StringComparer.Ordinal);
            return required
                .Where(key => !present.Contains(key))
                .OrderBy(key => key, StringComparer.Ordinal)
                .ToList();
        }

        internal static bool IsCoverageAcceptableForStartup(
            IEnumerable<string> presentKeys,
            bool requireAllOffsets)
        {
            var present = new HashSet<string>(presentKeys, StringComparer.Ordinal);
            if (GetCoreRequiredOffsetKeys().Any(key => !present.Contains(key)))
                return false;
            return !requireAllOffsets || GetOptionalOffsetKeys().All(present.Contains);
        }

        internal static bool ShouldReplaceCompatibleCache(
            IEnumerable<string> existingKeys,
            IEnumerable<string> liveKeys)
        {
            var existing = new HashSet<string>(existingKeys, StringComparer.Ordinal);
            var live = new HashSet<string>(liveKeys, StringComparer.Ordinal);
            return existing.Count <= live.Count || !live.All(existing.Contains);
        }

        private static string ToHex(string raw) =>
            ulong.TryParse(raw, out var value) ? $"0x{value:X}" : "invalid";

        private static void WriteJsonAtomic(string path, string json)
        {
            string tempPath = path + ".tmp";
            File.WriteAllText(tempPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(tempPath, path, overwrite: true);
        }
    }
}
