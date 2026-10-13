# Third-party notices

The `src-arena/` application and upstream map assets are derived from
[HuiTeab/arena-dma-radar-silk](https://github.com/HuiTeab/arena-dma-radar-silk).
The upstream [PolyForm Noncommercial License 1.0.0](LICENSE) and source notices
are retained. This is publicly available source with noncommercial terms.

The `lib/VmmSharpEx/` managed wrapper retains its original attribution to Ulf
Frisk and Lone (Lone DMA) and is separately licensed under
[AGPL-3.0](lib/VmmSharpEx/LICENSE-AGPL-3.0.txt), as described in the root license.

Packages restored through NuGet retain their own licenses:

| Component | Upstream |
|---|---|
| Silk.NET | [dotnet/Silk.NET](https://github.com/dotnet/Silk.NET) |
| ImGui.NET | [ImGuiNET/ImGui.NET](https://github.com/ImGuiNET/ImGui.NET) |
| SkiaSharp | [mono/SkiaSharp](https://github.com/mono/SkiaSharp) |
| Svg.Skia | [wieslawsoltes/Svg.Skia](https://github.com/wieslawsoltes/Svg.Skia) |
| Collections.Pooled.V2 | See the license and repository metadata of the restored NuGet package. |

Native DLLs, drivers, and the Neo Sans font binary are excluded. Obtain external
runtime components from their upstream distributions and retain the applicable
license notices. The publication's packaging changes use platform/default fonts;
the application's and wrapper's existing source notices are preserved.
