# Build and runtime dependencies

## Offline gate

Requirements: Windows x64 and the .NET 10 SDK. `global.json` requests SDK
**10.0.302** with roll-forward to a compatible later feature band.

```powershell
./verify-offline.ps1
```

This restores the solution's NuGet packages, builds Release, executes the
standalone offset-contract fixture, parses the four diagnostic/verification
scripts, and verifies that the executable and managed assembly were produced.
No application window, game process, or DMA session is started.

The offset contract separates **12 core** fields from **47 optional** fields.
It checks required-field membership, schema partitioning, metadata aliases,
and ambiguity handling. It does not measure live camera, transform, or respawn behavior.

## Build the application

```powershell
dotnet build ./eft-dma-radar-arena.sln -c Release
```

Output: `src-arena/bin/Release/net10.0-windows7.0/arena-dma-radar.exe`.
The project restores Silk.NET 2.23.0, ImGui.NET 1.91.6.1, SkiaSharp 3.119.2,
Svg.Skia 3.0.3, and Collections.Pooled.V2 2.2.2 from NuGet.
Arena SVG/JSON maps are retained and copied by the project.

## Restore external runtime components

Native DMA runtime files are not redistributed in this source-only repository.
Obtain a compatible, coherent [MemProcFS](https://github.com/ufrisk/MemProcFS)/
[LeechCore](https://github.com/ufrisk/LeechCore) distribution and the appropriate
device drivers. The original native folder contained `vmm.dll`, `leechcore.dll`,
`leechcore_driver.dll`, `FTD3XX.dll`, `dbghelp.dll`, `symsrv.dll`, `tinylz4.dll`,
and `vcruntime140.dll`; restore the files required by your chosen distribution
with their license notices, rather than mixing unrelated versions.

Place the required files under `lib/VmmSharpEx/native/` before building, or
alongside the executable in its output directory. The wrapper's existing
project definition copies native DLLs when they are present.

The embedded Neo Sans font is excluded. Skia uses a platform font and ImGui
uses its built-in font, preserving the existing system-symbol font merge.

## Run after restoring dependencies

```powershell
dotnet run --project ./src-arena/arena-dma-radar.csproj -c Release
```

Local configuration, logs, and caches are stored under
`%AppData%/eft-dma-radar-arena/`, outside the repository.
Use `-debug` for verbose startup logging. `-force-offset-rescan` bypasses
offset-bearing caches; `-require-critical-offsets` additionally requires the
complete runtime-resolvable schema. Core offset validation remains mandatory.

This publication's acceptance covers offline checks. It does not claim a new
live-game, two-match, or hardware validation session.
