# GOTY ESP · Arena

![C#](https://img.shields.io/badge/C%23-.NET%2010-512bd4?style=flat-square)
![Platform](https://img.shields.io/badge/platform-Windows%20x64-30363d?style=flat-square)
![Checks](https://github.com/justGoty/goty-esp-dma-arena/actions/workflows/checks.yml/badge.svg)
![License](https://img.shields.io/badge/core%20license-PolyForm%20Noncommercial-3fb950?style=flat-square)

A public, source-only snapshot of **GOTY Arena DMA ESP** for
**Escape from Tarkov: Arena**. Player ESP and radar rendering use
Silk.NET/OpenGL, ImGui.NET, and SkiaSharp on a Windows receiver.

## Inside

- **Player ESP:** corner boxes, names, distances, skeletons, and death filtering.
- **Camera pipeline:** FPS/optic projection paths with runtime validation and freshness handling.
- **Arena maps:** the upstream SVG/JSON map set and a separate Arena match-world model.
- **Diagnostics:** persistent logs, offset provenance, runtime snapshots, and collection scripts.
- **Offline verification:** solution restore/build, offset-contract checks, and script parsing without hardware.

## Refactored and extended

| Area | Technical changes |
|---|---|
| Offset validation | Separate core/optional gates, metadata identity checks, and PE-fingerprinted caches with provenance. |
| Data publication | Immutable scatter plans and atomically published player/bone frames reduce mixed-tick state. |
| Lifecycle | Attach generations, process heartbeat, worker drain, and reset checkpoints guard against old-session data. |
| ESP projection | Boxes and bones use the same skeleton frame, with bounded position and height fallbacks. |

## Start here

Install the **.NET 10 SDK** on Windows x64, then run:

```powershell
./verify-offline.ps1
```

The gate restores packages, builds Release, runs the offset-contract fixture,
and checks the diagnostic scripts. It does not launch the application, access
a DMA board, or attach to a game process.

Publication verification passed on **13 October 2026**: Release build with
**0 warnings / 0 errors**, the **12 core / 47 optional / 59 total** offset
contract, and all four PowerShell parser checks.

See [build and runtime dependencies](docs/BUILD.md) before running the full application.

## Snapshot and provenance

Source revision: `9dfd8e88177feca25690d6ea64f1f81796d51ab3`.
This publication starts with a new snapshot commit. Private history, internal
handoff/review documents, logs, user configuration, caches, native DLLs, and
the embedded font binary are excluded. Platform/default fonts are used instead.

Based on [HuiTeab/arena-dma-radar-silk](https://github.com/HuiTeab/arena-dma-radar-silk).
The upstream source notices and [PolyForm Noncommercial license](LICENSE)
are retained. The bundled VmmSharpEx wrapper is separately licensed under
AGPL-3.0; see [third-party notices](THIRD_PARTY.md).

This is an Arena-specific codebase. Offsets and layouts can change with updates;
an offline build does not establish current game or hardware compatibility.
