using eft_dma_radar.Arena.Unity.IL2CPP;

var failures = new List<string>();

static void Expect(bool condition, string message, List<string> failures)
{
    if (!condition)
        failures.Add(message);
}

var cacheable = Il2CppDumper.GetCacheableOffsetKeys();
var core = Il2CppDumper.GetCoreRequiredOffsetKeys();
var optional = Il2CppDumper.GetOptionalOffsetKeys();

string[] expectedCore =
[
    "Special.TypeInfoTableRva",
    "Special.GamePlayerOwner_TypeIndex",
    "GamePlayerOwner._myPlayer",
    "Player.GameWorld",
    "Player._playerLookRaycastTransform",
    "ClientLocalGameWorld.MainPlayer",
    "ClientLocalGameWorld.RegisteredPlayers",
    "ObservedPlayerView.Id",
    "ObservedPlayerView.NickName",
    "ObservedPlayerView.Side",
    "ObservedPlayerView.IsAI",
    "ObservedPlayerView._playerLookRaycastTransform",
];

foreach (var key in expectedCore)
    Expect(core.Contains(key), $"Core set is missing '{key}'.", failures);

Expect(core.Count == expectedCore.Length,
    $"Core set has {core.Count} entries; expected {expectedCore.Length}.", failures);
Expect(!core.Overlaps(optional), "Core and optional sets overlap.", failures);

var classified = new HashSet<string>(core, StringComparer.Ordinal);
classified.UnionWith(optional);
var unclassified = cacheable.Except(classified, StringComparer.Ordinal).Order().ToArray();
var unknownCore = classified.Except(cacheable, StringComparer.Ordinal).Order().ToArray();
Expect(classified.SetEquals(cacheable),
    $"Core + optional mismatch. Unclassified=[{string.Join(", ", unclassified)}], " +
    $"unknown=[{string.Join(", ", unknownCore)}].", failures);

string[] expectedOptional =
[
    "ClientLocalGameWorld.LocationId",
    "ObservedPlayerView.ProfileId",
    "BifacialTransform.Original",
    "ObservedHealthController.HealthStatus",
    "ObservedHealthController.IsAlive",
    "ObservedHealthController.Player",
    "OpticCameraManager.Camera",
    "ProfileStatus.profileToken",
    "EFTCameraManager.GetInstance_RVA",
];

foreach (var key in expectedOptional)
    Expect(optional.Contains(key), $"Optional set is missing '{key}'.", failures);

var coreComplete = new HashSet<string>(cacheable, StringComparer.Ordinal);
coreComplete.ExceptWith(optional);
Expect(Il2CppDumper.GetMissingKeys(core, coreComplete).Count == 0,
    "A core-complete set was reported as missing core offsets.", failures);
Expect(Il2CppDumper.GetMissingKeys(optional, coreComplete).Count == optional.Count,
    "Optional omissions were not reported independently.", failures);

var oneCoreMissing = new HashSet<string>(cacheable, StringComparer.Ordinal);
oneCoreMissing.Remove("Player.GameWorld");
var missingCore = Il2CppDumper.GetMissingKeys(core, oneCoreMissing);
Expect(missingCore.SequenceEqual(["Player.GameWorld"]),
    "Missing core keys are not deterministic or correctly classified.", failures);

Expect(Il2CppDumper.IsCoverageAcceptableForStartup(coreComplete, requireAllOffsets: false),
    "Core-only coverage must be accepted in normal mode.", failures);
Expect(!Il2CppDumper.IsCoverageAcceptableForStartup(coreComplete, requireAllOffsets: true),
    "Core-only coverage must be rejected in strict mode so live resolution can continue.", failures);
Expect(Il2CppDumper.IsCoverageAcceptableForStartup(cacheable, requireAllOffsets: true),
    "Complete coverage must be accepted in strict mode.", failures);
Expect(!Il2CppDumper.IsCoverageAcceptableForStartup(oneCoreMissing, requireAllOffsets: false),
    "Missing core coverage must be rejected even in normal mode.", failures);

Expect(!Il2CppDumper.ShouldReplaceCompatibleCache(cacheable, coreComplete),
    "A partial live result must not replace a more complete compatible cache.", failures);
Expect(Il2CppDumper.ShouldReplaceCompatibleCache(coreComplete, cacheable),
    "A complete live result must replace a partial compatible cache.", failures);
Expect(Il2CppDumper.ShouldReplaceCompatibleCache(cacheable, cacheable),
    "An equally complete live result should refresh the compatible cache.", failures);

Expect(Il2CppDumper.SelectSingleIdentityMatch([false, true, false]) == 1,
    "A unique structural class identity was not selected.", failures);
Expect(Il2CppDumper.SelectSingleIdentityMatch([true, true]) == -1,
    "Ambiguous structural class identities must be rejected.", failures);
Expect(Il2CppDumper.SelectSingleIdentityMatch([false, false]) == -1,
    "A class must not be selected when no identity matches.", failures);

if (failures.Count > 0)
{
    foreach (var failure in failures)
        Console.Error.WriteLine($"FAIL: {failure}");
    return 1;
}

Console.WriteLine($"PASS: offset contract ({core.Count} core, {optional.Count} optional, {cacheable.Count} total).");
return 0;
