# SPEC: CandleDeltaProfile — ATAS X Custom Indicator (macOS)

## Goal

Build a C# custom indicator for ATAS X on macOS. For each bar, it renders three side-by-side zones:

| Left third | Middle third | Right third |
|---|---|---|
| Per-price **delta** bars (Ask − Bid), **right-aligned** toward the candle. Blue = positive, red = negative. | A normal **OHLC candle** (wick + body). Green = up, red = down. | Per-price **volume profile** bars, **left-aligned** from the candle. Gray. |

Bar lengths are proportional within each bar: delta is scaled to that bar's max |delta|, and volume to that bar's max volume.

**No text or numbers are drawn on any bar.**

## Reference
- See ./reference.png for rough idea of presentation/colors

## Confirmed environment (do not re-derive)

- macOS on Apple Silicon.
- App: `/Applications/ATAS X.app`
- ATAS assemblies: `/Applications/ATAS X.app/Contents/MonoBundle/`
  - Present: `ATAS.Indicators.dll`, `ATAS.DataFeedsCore.dll`, `Utils.Common.dll`, `OFT.Rendering.dll` (plus others)
- ATAS X runtime: `.NETCoreApp,Version=v10.0`. The target framework is therefore **`net10.0-windows`**.
- Installed SDK: .NET `10.0.401` at `/usr/local/share/dotnet`
- The indicators folder watched by ATAS X is `~/Library/Application Support/ATAS/Indicators`. ATAS X hot-reloads DLLs dropped there, with no restart needed.

## Constraints

- Do not reference WPF UI types (Window, UserControl, XAML), because ATAS X rejects them at load. `System.Windows.Media.Color` and `System.Drawing` primitives are allowed, since ATAS X auto-converts them.
- Reference ATAS DLLs with `<Private>false</Private>` so they are not copied to the output folder.
- Avoid LINQ and allocations in the per-bar render path (ATAS performance guidance).
- Nothing should require `sudo`.

## Project layout

```
CandleDeltaProfile/
├── CandleDeltaProfile.csproj
└── CandleDeltaProfile.cs
```

## CandleDeltaProfile.csproj

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0-windows</TargetFramework>
    <UseWPF>true</UseWPF>
    <EnableWindowsTargeting>true</EnableWindowsTargeting>
    <LangVersion>latest</LangVersion>
    <Nullable>disable</Nullable>
    <AtasDir>/Applications/ATAS X.app/Contents/MonoBundle</AtasDir>
  </PropertyGroup>

  <ItemGroup>
    <Reference Include="ATAS.Indicators"><HintPath>$(AtasDir)/ATAS.Indicators.dll</HintPath><Private>false</Private></Reference>
    <Reference Include="ATAS.DataFeedsCore"><HintPath>$(AtasDir)/ATAS.DataFeedsCore.dll</HintPath><Private>false</Private></Reference>
    <Reference Include="Utils.Common"><HintPath>$(AtasDir)/Utils.Common.dll</HintPath><Private>false</Private></Reference>
    <Reference Include="OFT.Rendering"><HintPath>$(AtasDir)/OFT.Rendering.dll</HintPath><Private>false</Private></Reference>
  </ItemGroup>

  <Target Name="DeployToAtas" AfterTargets="Build">
    <Copy SourceFiles="$(TargetPath)" DestinationFolder="$(HOME)/Library/Application Support/ATAS/Indicators" />
  </Target>
</Project>
```

## Reference implementation (CandleDeltaProfile.cs)

This is a starting point, not verified code. The API member names marked **VERIFY** below must be checked against the actual DLLs before the build is considered done.

```csharp
using System;
using System.ComponentModel;
using System.Drawing;
using ATAS.Indicators;
using OFT.Rendering.Context;
using OFT.Rendering.Tools;

[DisplayName("Candle Delta Profile")]
public class CandleDeltaProfile : Indicator
{
    private readonly Color _pos  = Color.FromArgb(255, 30, 144, 255);
    private readonly Color _neg  = Color.FromArgb(255, 220, 20, 60);
    private readonly Color _vol  = Color.FromArgb(255, 110, 110, 110);
    private readonly Color _bull = Color.FromArgb(255, 60, 200, 60);
    private readonly Color _bear = Color.FromArgb(255, 230, 40, 40);

    public CandleDeltaProfile() : base(true)
    {
        DenyToChangePanel = true;
        EnableCustomDrawing = true;
        SubscribeToDrawingEvents(DrawingLayouts.Historical | DrawingLayouts.LatestBar);
        DataSeries[0].IsHidden = true;
    }

    protected override void OnCalculate(int bar, decimal value) { }

    protected override void OnRender(RenderContext ctx, DrawingLayouts layout)
    {
        if (ChartInfo == null) return;

        var barW = (int)ChartInfo.PriceChartContainer.BarsWidth;
        if (barW < 9) return;
        var rowH = Math.Max(1, (int)ChartInfo.PriceChartContainer.PriceRowHeight);
        var zone = barW / 3;

        var lastBar = CurrentBar - 1;
        int from, to;
        if (layout == DrawingLayouts.LatestBar) { from = to = lastBar; }
        else { from = FirstVisibleBarNumber; to = Math.Min(LastVisibleBarNumber, lastBar - 1); }

        for (var bar = from; bar <= to; bar++)
            DrawBar(ctx, bar, zone, rowH);
    }

    private void DrawBar(RenderContext ctx, int bar, int zone, int rowH)
    {
        var c = GetCandle(bar);
        var levels = c.GetAllPriceLevels();

        decimal maxVol = 1, maxDelta = 1;
        foreach (var l in levels)
        {
            if (l.Volume > maxVol) maxVol = l.Volume;
            var ad = Math.Abs(l.Ask - l.Bid);
            if (ad > maxDelta) maxDelta = ad;
        }

        var x = ChartInfo.GetXByBar(bar);
        var candleLeft = x + zone;
        var candleRight = candleLeft + zone;
        var h = rowH > 3 ? rowH - 1 : rowH;

        foreach (var l in levels)
        {
            var y = ChartInfo.GetYByPrice(l.Price, false);

            var d = l.Ask - l.Bid;
            var dw = (int)(zone * Math.Abs(d) / maxDelta);
            if (dw > 0)
                ctx.FillRectangle(d >= 0 ? _pos : _neg, new Rectangle(candleLeft - dw, y, dw, h));

            var vw = (int)(zone * l.Volume / maxVol);
            if (vw > 0)
                ctx.FillRectangle(_vol, new Rectangle(candleRight, y, vw, h));
        }

        var col = c.Close >= c.Open ? _bull : _bear;
        var mid = candleLeft + zone / 2;
        var yHigh = ChartInfo.GetYByPrice(c.High, false);
        var yLow  = ChartInfo.GetYByPrice(c.Low, false) + rowH;
        ctx.DrawLine(new RenderPen(col), mid, yHigh, mid, yLow);

        var yTop = ChartInfo.GetYByPrice(Math.Max(c.Open, c.Close), false);
        var yBot = ChartInfo.GetYByPrice(Math.Min(c.Open, c.Close), false) + rowH;
        var bodyW = Math.Max(1, zone / 2);
        ctx.FillRectangle(col, new Rectangle(mid - bodyW / 2, yTop, bodyW, Math.Max(1, yBot - yTop)));
    }
}
```

### VERIFY against the actual assemblies

Before or during the first build, confirm these members exist with the signatures used above. Inspect the DLLs in `MonoBundle` directly (e.g. `dotnet tool install -g ilspycmd`, then `ilspycmd -t <TypeName> <dll>`) rather than guessing:

- `Indicator`: constructor `base(bool)`, `DenyToChangePanel`, `EnableCustomDrawing`, `SubscribeToDrawingEvents`, `OnRender(RenderContext, DrawingLayouts)`, `FirstVisibleBarNumber`, `LastVisibleBarNumber`, `CurrentBar`, `GetCandle`, `ChartInfo`
- `ChartInfo.PriceChartContainer.BarsWidth` and `.PriceRowHeight`
- `ChartInfo.GetXByBar(int, ...)` and `ChartInfo.GetYByPrice(decimal, bool)`, including whether Y is the top or the center of the price row
- `IndicatorCandle.GetAllPriceLevels()` and the element type's `Price`, `Volume`, `Ask`, `Bid`
- `RenderContext.FillRectangle(Color, Rectangle)` and `DrawLine(RenderPen, int, int, int, int)`, including which `Color` type they take
- The `RenderPen` namespace

Fix the code to match what the assemblies actually expose. Do not change the visual spec.

## Build and deploy

```bash
dotnet build
```

On success, the post-build target copies the DLL into `~/Library/Application Support/ATAS/Indicators`.

## Acceptance criteria

1. `dotnet build` completes with zero errors.
2. The DLL lands in `~/Library/Application Support/ATAS/Indicators`.
3. The indicator appears in ATAS X's indicator list as "Candle Delta Profile" and loads without a rejection error.
4. With chart setup done (below), each bar shows delta bars on the left (right-aligned), a candle in the middle, and a volume profile on the right (left-aligned), with no text.
5. Price rows line up vertically with the candle's high and low.
6. The latest bar updates live on ticks.

## Manual chart setup (user performs in ATAS X; not Claude Code's job)

- Chart type: Candles.
- Set the native candle body, border, and wick colors to fully transparent.
- Widen bar spacing until bars render (the indicator skips drawing below 9px bar width).

## Out of scope

- Numeric labels on bars
- Configurable colors or settings UI
- Distribution via ATAS Personal Area
