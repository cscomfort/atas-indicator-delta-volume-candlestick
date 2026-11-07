using System;
using System.ComponentModel;
using System.Drawing;
using ATAS.Indicators;
using OFT.Rendering.Context;

// Per bar, three equal zones, centered in the bar's slot with a gap to the next bar:
//   left   = per-price delta (Ask - Bid), right-aligned toward the candle
//   middle = OHLC candle
//   right  = per-price volume profile, left-aligned from the candle
// API notes (verified against ATAS X assemblies, .NET 10):
//   - ChartInfo is IChart; GetXByBar/GetYByPrice are extension methods in ATAS.Indicators.Extensions.
//   - GetYByPrice(price, isStartOfPriceLevel: true) returns the TOP edge of the price row;
//     false returns the row's center (used by built-ins for lines through a price).
//   - IndicatorCandle.GetAllPriceLevels(cacheItem) reuses one PriceVolumeInfo per yield,
//     and returns null when the candle has no per-price data.
[DisplayName("Candle Delta Profile")]
public class CandleDeltaProfile : Indicator
{
    // Profile colors at full intensity. Each level is drawn with alpha proportional to
    // its share of the bar's max (|delta| / max|delta|, volume / max volume), so the
    // heaviest level shows the full color and lighter levels fade toward the background.
    // Delta uses one shared max for both signs, so the heaviest red only reaches full
    // red if it is also the heaviest |delta| in the bar.
    private static readonly Color Pos  = Color.FromArgb(255, 0x29, 0x62, 0xFF); // #FF2962FF
    private static readonly Color Neg  = Color.FromArgb(255, 0xF7, 0x14, 0x27); // #FFF71427
    private static readonly Color Vol  = Color.FromArgb(255, 0xFF, 0xFF, 0xFF); // white

    // Candle: soft-white wick on all bars. Down = solid soft-white body.
    // Up = hollow body (soft-white border, transparent interior).
    private static readonly Color CandleWhite = Color.FromArgb(255, 0xAB, 0xAE, 0xB8); // #FFABAEB8 (soft white)

    // Alpha floor (~10%) so the lightest non-zero levels stay faintly visible.
    // Measured from the reference image: bars under ~10% of max all render at this level.
    private const int MinAlpha = 26;

    // Gap between adjacent bar groups (delta | candle | volume), as a share of the
    // bar-to-bar pitch (BarsWidth + BarSpacing), clamped to [MinGapPx, MaxGapPx].
    private const double GapRatio = 0.15;
    private const int MinGapPx = 10;
    private const int MaxGapPx = 20;

    // POC (max-volume level) style. Switch PocMode and rebuild to compare:
    //   DeltaColor - POC bar filled solid in the candle's overall delta color
    //                (blue > 0, red < 0, white if flat); tracks live delta until close.
    //   Highlight  - POC bar keeps the white gradient; a translucent gold box is laid
    //                over it, with padding that scales with the bar width.
    private enum PocStyle { DeltaColor, Highlight }
    private static readonly PocStyle PocMode = PocStyle.Highlight;
    private static readonly Color PocHighlight = Color.FromArgb(94, 0xFF, 0xD1, 0x3C); // ~37% #FFD13C, measured from reference

    // Highlight padding = PocPadRatio x zone width, clamped to [PocMinPadPx, PocMaxPadPx].
    // 0.07 is the reference image's proportion (10px pad on a ~140px-wide profile).
    private const double PocPadRatio = 0.07;
    private const int PocMinPadPx = 2;
    private const int PocMaxPadPx = 10;

    // Smallest zone width worth drawing (3 zones => 9px of content).
    private const int MinZonePx = 3;

    // Wick width (also the up-candle border width) as a share of the body width,
    // clamped to [MinWickPx, MaxWickPx].
    private const double WickRatio = 0.10;
    private const int MinWickPx = 1;
    private const int MaxWickPx = 10;

    // Separate scratch buffers per layout, so a Historical and a LatestBar render
    // can never trample each other's data.
    private readonly LevelBuffer _historicalBuf = new();
    private readonly LevelBuffer _latestBuf = new();

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
        var chart = ChartInfo;
        if (chart == null) return;
        var container = chart.PriceChartContainer;
        if (container == null) return;

        // Bar pitch = distance between adjacent bars' left edges (same formula ATAS's
        // built-in indicators use). The group gets the pitch minus the gap, centered on
        // the bar so it stays aligned with the time axis and crosshair.
        var barW = (int)container.BarsWidth;
        var pitch = (int)(container.BarsWidth + container.BarSpacing);
        var gap = Math.Clamp((int)Math.Round(pitch * GapRatio), MinGapPx, MaxGapPx);
        var zone = (pitch - gap) / 3;
        if (zone < MinZonePx) return;
        var groupOffset = barW / 2 - (3 * zone) / 2;
        var rowH = Math.Max(1, (int)container.PriceRowHeight);

        var lastBar = CurrentBar - 1;
        if (lastBar < 0) return;
        var firstVisible = Math.Max(0, container.FirstVisibleBarNumber);
        var lastVisible = Math.Min(container.LastVisibleBarNumber, lastBar);

        // Historical layout: every visible bar except the live one.
        if ((layout & DrawingLayouts.Historical) != 0)
        {
            var to = Math.Min(lastVisible, lastBar - 1);
            for (var bar = firstVisible; bar <= to; bar++)
                DrawBar(ctx, chart, bar, groupOffset, zone, gap, rowH, _historicalBuf);
        }

        // LatestBar layout: the live bar, redrawn on ticks.
        if ((layout & DrawingLayouts.LatestBar) != 0 && lastBar >= firstVisible && lastBar <= lastVisible)
            DrawBar(ctx, chart, lastBar, groupOffset, zone, gap, rowH, _latestBuf);
    }

    private void DrawBar(RenderContext ctx, IChart chart, int bar, int groupOffset, int zone, int gap, int rowH, LevelBuffer buf)
    {
        var candle = GetCandle(bar);
        if (candle == null) return;

        buf.Load(candle, chart);

        var x = chart.GetXByBar(bar, true) + groupOffset;
        var candleLeft = x + zone;
        var candleRight = candleLeft + zone;
        var h = rowH > 3 ? rowH - 1 : rowH;

        var maxDelta = buf.MaxAbsDelta;
        var maxVol = buf.MaxVolume;

        // DeltaColor POC: full intensity in the candle's overall delta color. The live bar
        // is redrawn on every tick, so it tracks the delta until the bar closes.
        var colorPoc = PocMode == PocStyle.DeltaColor;
        var candleDelta = candle.Delta;
        var pocColor = candleDelta > 0 ? Pos : candleDelta < 0 ? Neg : Vol;
        int pocY = 0, pocW = 0; // POC bar geometry, for the Highlight box

        for (var i = 0; i < buf.Count; i++)
        {
            var y = buf.Y[i];

            if (maxDelta > 0)
            {
                var d = buf.Delta[i];
                var ratio = (double)(Math.Abs(d) / maxDelta);
                var dw = (int)(zone * ratio);
                if (dw > 0)
                    ctx.FillRectangle(Shade(d >= 0 ? Pos : Neg, ratio), new Rectangle(candleLeft - dw, y, dw, h));
            }

            if (maxVol > 0)
            {
                var ratio = (double)(buf.Volume[i] / maxVol);
                var vw = (int)(zone * ratio);
                if (vw > 0)
                {
                    var isPoc = i == buf.PocIndex;
                    if (isPoc) { pocY = y; pocW = vw; }
                    ctx.FillRectangle(isPoc && colorPoc ? pocColor : Shade(Vol, ratio), new Rectangle(candleRight, y, vw, h));
                }
            }
        }

        // Candle spans from the top of the High row to the bottom of the Low row,
        // using the same row height as the delta/volume bars so edges line up.
        var up = candle.Close >= candle.Open;
        var mid = candleLeft + zone / 2;
        var bodyW = Math.Max(1, zone / 2);
        var bodyX = mid - bodyW / 2;

        // One line width, scaled to the body, for both the wick and the up-candle border.
        // When wider than 1px, match the body's width parity so the wick sits exactly
        // centered rather than half a pixel off.
        var lineW = Math.Clamp((int)Math.Round(bodyW * WickRatio), MinWickPx, MaxWickPx);
        if (lineW > 1 && ((bodyW - lineW) & 1) != 0) lineW--;
        var wickX = bodyX + (bodyW - lineW) / 2;

        var yHigh = chart.GetYByPrice(candle.High, true);
        var yLow = chart.GetYByPrice(candle.Low, true) + h;
        var yTop = chart.GetYByPrice(Math.Max(candle.Open, candle.Close), true);
        var yBot = chart.GetYByPrice(Math.Min(candle.Open, candle.Close), true) + h;
        var bodyH = Math.Max(1, yBot - yTop);
        var bodyBottom = yTop + bodyH;

        // Wick above and below the body only, so it doesn't show through a hollow body.
        if (yTop > yHigh)
            ctx.FillRectangle(CandleWhite, new Rectangle(wickX, yHigh, lineW, yTop - yHigh));
        if (yLow > bodyBottom)
            ctx.FillRectangle(CandleWhite, new Rectangle(wickX, bodyBottom, lineW, yLow - bodyBottom));

        // Body: down = solid; up = hollow (border only, transparent interior).
        // A body too small to fit an interior inside its border is drawn solid.
        var innerW = bodyW - 2 * lineW;
        var innerH = bodyH - 2 * lineW;
        if (!up || innerW <= 0 || innerH <= 0)
        {
            ctx.FillRectangle(CandleWhite, new Rectangle(bodyX, yTop, bodyW, bodyH));
        }
        else
        {
            ctx.FillRectangle(CandleWhite, new Rectangle(bodyX, yTop, bodyW, lineW));                          // top
            ctx.FillRectangle(CandleWhite, new Rectangle(bodyX, bodyBottom - lineW, bodyW, lineW));            // bottom
            ctx.FillRectangle(CandleWhite, new Rectangle(bodyX, yTop + lineW, lineW, innerH));                 // left
            ctx.FillRectangle(CandleWhite, new Rectangle(bodyX + bodyW - lineW, yTop + lineW, lineW, innerH)); // right
        }

        // Highlight POC: translucent gold box over the POC bar (drawn last, so it tints the
        // bar and any neighboring rows it covers, as in the reference). Padding scales with
        // the zone width; horizontally it also shrinks so the box never covers the candle
        // body or reaches past the midpoint of the gap to the next bar.
        if (!colorPoc && pocW > 0)
        {
            var pad = Math.Clamp((int)Math.Round(zone * PocPadRatio), PocMinPadPx, PocMaxPadPx);
            var padX = Math.Min(pad, Math.Min(candleRight - (bodyX + bodyW) - 1, gap / 2 - 1));
            if (padX < 0) padX = 0;
            ctx.FillRectangle(PocHighlight,
                new Rectangle(candleRight - padX, pocY - pad, pocW + 2 * padX, h + 2 * pad));
        }
    }

    // Full color at ratio 1, fading linearly (via alpha) toward the chart background,
    // never below MinAlpha. Color is a struct, so this does not allocate.
    private static Color Shade(Color full, double ratio)
    {
        var a = (int)(255 * ratio + 0.5);
        if (a < MinAlpha) a = MinAlpha;
        else if (a > 255) a = 255;
        return Color.FromArgb(a, full);
    }

    // Copies a candle's price levels into reusable arrays in a single enumeration pass,
    // computing the per-bar maxima along the way. Arrays grow only when a bar has more
    // levels than ever seen before, so steady-state rendering does not allocate here.
    private sealed class LevelBuffer
    {
        private readonly PriceVolumeInfo _cache = new();

        public int[] Y = new int[128];
        public decimal[] Volume = new decimal[128];
        public decimal[] Delta = new decimal[128];
        public int Count;
        public decimal MaxVolume;
        public decimal MaxAbsDelta;
        public int PocIndex; // index of the POC level in the arrays, or -1

        public void Load(IndicatorCandle candle, IChart chart)
        {
            Count = 0;
            MaxVolume = 0;
            MaxAbsDelta = 0;
            PocIndex = -1;

            // Prefer ATAS's own POC so ties resolve the same way as its footprint.
            // Read the price before enumerating, since enumeration reuses _cache.
            var atasPoc = candle.MaxVolumePriceInfo;
            var hasAtasPoc = atasPoc != null;
            var atasPocPrice = hasAtasPoc ? atasPoc.Price : 0m;
            var maxVolIndex = -1;

            var levels = candle.GetAllPriceLevels(_cache);
            if (levels == null) return;

            foreach (var l in levels)
            {
                if (Count == Y.Length) Grow();

                var d = l.Ask - l.Bid;
                var ad = Math.Abs(d);
                if (ad > MaxAbsDelta) MaxAbsDelta = ad;
                if (l.Volume > MaxVolume) { MaxVolume = l.Volume; maxVolIndex = Count; }
                if (hasAtasPoc && l.Price == atasPocPrice) PocIndex = Count;

                Y[Count] = chart.GetYByPrice(l.Price, true);
                Volume[Count] = l.Volume;
                Delta[Count] = d;
                Count++;
            }

            if (PocIndex < 0) PocIndex = maxVolIndex;
        }

        private void Grow()
        {
            var n = Y.Length * 2;
            Array.Resize(ref Y, n);
            Array.Resize(ref Volume, n);
            Array.Resize(ref Delta, n);
        }
    }
}
