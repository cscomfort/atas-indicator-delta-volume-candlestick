using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using System.Globalization;
using ATAS.Indicators;
using OFT.Attributes;
using OFT.Rendering.Context;
using OFT.Rendering.Tools;

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
// About text: set as both the class-level [Display(Description)] (what ATAS's built-ins use) and
// [Description] (what Utils.Common's Type.GetDescription() reads), so it shows whichever ATAS X uses.
[DisplayName("Spectraesque")]
[Display(Description = Spectraesque.AboutText)]
[Description(Spectraesque.AboutText)]
public class Spectraesque : Indicator
{
    // ATAS X renders this as HTML (allowed: a, b, strong, i, em, u, p, ul, ol, li, span, br; bare
    // URLs auto-link). With p/ul/li/br present, plain line breaks are NOT converted, so layout is
    // done with tags. ATAS caches About text per indicator, so changes need an ATAS X restart.
    internal const string AboutText =
        "<p>Spectraesque is based on FlowZoneTrader's Spectra Pro for NinjaTrader. Note that the " +
        "presentation and settings are based solely on visual references of the Spectra Pro " +
        "indicator not actual settings. Props where props are due—support Alex and FZT here:</p>" +
        "<ul>" +
        "<li>https://flowzonetrader.com</li>" +
        "<li>https://www.youtube.com/@FlowZoneTrader</li>" +
        "</ul>" +
        "<p><strong>Suggestions for Use</strong><br/>" +
        "Hide the default candle, footprint charts by right-clicking the chart and choosing " +
        "Mode → Hidden.</p>";

    // Default colors. Every color is user-settable (see Settings below); these are the defaults.
    private static readonly Color Blue      = Color.FromArgb(255, 0x21, 0x96, 0xF3); // #FF2196F3
    private static readonly Color Red       = Color.FromArgb(255, 0xF7, 0x14, 0x27); // #FFF71427
    private static readonly Color White     = Color.FromArgb(255, 0xFF, 0xFF, 0xFF); // #FFFFFFFF
    private static readonly Color SoftWhite = Color.FromArgb(255, 0xAB, 0xAE, 0xB8); // #FFABAEB8
    private static readonly Color Gold      = Color.FromArgb(94, 0xFF, 0xD1, 0x3C);  // ~37% #FFD13C, measured from reference

    // Profile colors at full intensity. Each level is drawn with alpha proportional to
    // its share of the bar's max (|delta| / max|delta|, volume / max volume), so the
    // heaviest level shows the full color and lighter levels fade toward the background.
    // Delta uses one shared max for both signs, so the heaviest red only reaches full
    // red if it is also the heaviest |delta| in the bar.
    private Color _posColor = Blue; // positive delta (Ask > Bid)
    private Color _negColor = Red;  // negative delta (Bid > Ask)
    private Color _volColor = White;

    // Candle: wick on all bars. Down = solid body. Up = hollow body (border, transparent
    // interior). One color for all of it.
    private Color _candleColor = SoftWhite;

    // Alpha floor (~10%) so the lightest non-zero levels stay faintly visible.
    // Measured from the reference image: bars under ~10% of max all render at this level.
    private const int MinAlpha = 26;

    // Gap between adjacent bar groups (delta | candle | volume), as a share of the
    // bar-to-bar pitch (BarsWidth + BarSpacing), clamped to [MinGapPx, MaxGapPx].
    private const double GapRatio = 0.15;
    private const int MinGapPx = 10;
    private const int MaxGapPx = 20;

    // POC (max-volume level) highlight: the POC bar keeps the volume gradient and a
    // translucent box is laid over it, with padding that scales with the bar width.
    private bool _showPocHighlight = true;
    private Color _pocHighlightColor = Gold;

    // Highlight padding = PocPadRatio x zone width, clamped to [PocMinPadPx, PocMaxPadPx].
    // 0.07 is the reference image's proportion (10px pad on a ~140px-wide profile).
    private const double PocPadRatio = 0.07;
    private const int PocMinPadPx = 2;
    private const int PocMaxPadPx = 10;

    // Delta value labels: on every delta row once rows are at least DeltaTextMinRowPx tall,
    // right-aligned DeltaTextPadPx inside the delta profile's inner (candle-side) edge.
    // RenderFont stores sizes in points and ATAS X's renderer converts back (x96/72), so
    // GraphicsUnit.Pixel gives exact pixel sizes; the default (points) renders 4/3 larger.
    // Sizes here are ATAS chart units, which on this Retina Mac render at ~3/4 of an
    // on-screen pixel (user observed the old 16-unit threshold showing at ~12px rows).
    // The threshold of 21 units = ~16 on-screen px. Font starts at DeltaTextMinPx at the
    // threshold and grows DeltaTextGrowth px per unit of extra row height, up to
    // DeltaTextMaxPx. One font per size, built once.
    private const int DeltaTextMinRowPx = 21;
    private const int DeltaTextPadPx = 2;
    private const double DeltaTextGrowth = 0.5;
    private const int DeltaTextMinPx = 8;
    private const int DeltaTextMaxPx = 12;
    private static readonly RenderFont[] DeltaFonts = CreateFonts(DeltaTextMinPx, DeltaTextMaxPx);

    // One Courier New font per pixel size in [minPx, maxPx], built once.
    private static RenderFont[] CreateFonts(int minPx, int maxPx)
    {
        var fonts = new RenderFont[maxPx - minPx + 1];
        for (var i = 0; i < fonts.Length; i++)
            fonts[i] = new RenderFont("Courier New", minPx + i, GraphicsUnit.Pixel);
        return fonts;
    }
    private static readonly Color DeltaTextColor = Color.FromArgb(255, 255, 255, 255); // #FFFFFF, for contrast
    private static readonly RenderStringFormat DeltaTextFormat = new()
    {
        Alignment = StringAlignment.Far,
        LineAlignment = StringAlignment.Center,
        FormatFlags = StringFormatFlags.NoWrap,
        Trimming = StringTrimming.None,
    };

    // Delta divergence: the candle's direction disagrees with its overall delta.
    //   Down candle (close < open), positive delta -> "positive divergence": delta shown above
    //                                                 the candle (default red).
    //   Up candle (close > open), negative delta   -> "negative divergence": delta shown below
    //                                                 the candle (default blue).
    // Label is the signed delta plus " Δ" (e.g. "-159 Δ") in Trebuchet MS, centered on the candle,
    // DivTextGapPx from the wick tip. Sized to match ATAS's built-in font setting "Trebuchet MS; 9px"
    // (e.g. Candle Statistics): despite the "px" label, ATAS's FontSetting builds its RenderFont in
    // points (the default unit), which renders x96/72 = 12 chart units, not 9. So this uses points
    // too, unlike the delta row labels, which use GraphicsUnit.Pixel.
    private const int DivTextAtasPx = 9;
    private const int DivTextGapPx = 9;
    private bool _showDivergence = true;
    private Color _divPosColor = Red;
    private Color _divNegColor = Blue;
    private static readonly RenderFont DivFont = new("Trebuchet MS", DivTextAtasPx); // points, like FontSetting
    private static readonly RenderStringFormat DivBelowFormat = new()
    {
        Alignment = StringAlignment.Center,
        LineAlignment = StringAlignment.Near,
        FormatFlags = StringFormatFlags.NoWrap,
        Trimming = StringTrimming.None,
    };
    private static readonly RenderStringFormat DivAboveFormat = new()
    {
        Alignment = StringAlignment.Center,
        LineAlignment = StringAlignment.Far,
        FormatFlags = StringFormatFlags.NoWrap,
        Trimming = StringTrimming.None,
    };

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

    // ---- Settings: one "Visualization" tab, one section (GroupName) per part of the bar ----
    // Saved with the chart. Conventions match ATAS X's own indicators: plain System.Drawing.Color
    // for color pickers, [Tab] for the tab, GroupName for sections, [VisibleWhen] to hide a
    // feature's colors while it is switched off. Toggles raise PropertyChanged so the panel can
    // re-evaluate [VisibleWhen].
    private const string VisualTab = "Visualization";

    [Display(Name = "Color", GroupName = "Candle", Order = 10,
        Description = "Wick, solid down-candle body and hollow up-candle border")]
    [Tab(TabName = VisualTab)]
    public Color CandleColor
    {
        get => _candleColor;
        set { _candleColor = value; RedrawChart(); }
    }

    // Profile scale: ticks per delta/volume profile row, independent of the chart's own scale.
    // Rows are aligned to multiples of the row size (like ATAS's chart scale) and drawn full
    // height, so the top/bottom rows can reach past the candle's high/low (as in ATAS's own
    // scaled footprint). The candle itself always stays at the chart's resolution.
    private const int MaxProfileScale = 1000;
    private int _profileScale = 1;

    [Display(Name = "Scale", GroupName = "Profiles", Order = 15,
        Description = "Ticks per row for the delta and volume profiles, independent of the chart's scale")]
    [Tab(TabName = VisualTab)]
    [System.ComponentModel.DataAnnotations.Range(1, MaxProfileScale)]
    public int ProfileScale
    {
        get => _profileScale;
        set { _profileScale = Math.Clamp(value, 1, MaxProfileScale); RedrawChart(); }
    }

    [Display(Name = "Positive Delta", GroupName = "Delta Profile", Order = 20,
        Description = "Price levels with more volume traded at the Ask than the Bid")]
    [Tab(TabName = VisualTab)]
    public Color PositiveDeltaColor
    {
        get => _posColor;
        set { _posColor = value; RedrawChart(); }
    }

    [Display(Name = "Negative Delta", GroupName = "Delta Profile", Order = 30,
        Description = "Price levels with more volume traded at the Bid than the Ask")]
    [Tab(TabName = VisualTab)]
    public Color NegativeDeltaColor
    {
        get => _negColor;
        set { _negColor = value; RedrawChart(); }
    }

    [Display(Name = "Color", GroupName = "Volume Profile", Order = 40)]
    [Tab(TabName = VisualTab)]
    public Color VolumeColor
    {
        get => _volColor;
        set { _volColor = value; RedrawChart(); }
    }

    [Display(Name = "Show POC", GroupName = "Volume Profile", Order = 50,
        Description = "Box around the price level with the most volume")]
    [Tab(TabName = VisualTab)]
    public bool ShowPocHighlight
    {
        get => _showPocHighlight;
        set { _showPocHighlight = value; RaisePropertyChanged(nameof(ShowPocHighlight)); RedrawChart(); }
    }

    [Display(Name = "POC Color", GroupName = "Volume Profile", Order = 60)]
    [Tab(TabName = VisualTab)]
    [VisibleWhen(nameof(ShowPocHighlight), true)]
    public Color PocHighlightColor
    {
        get => _pocHighlightColor;
        set { _pocHighlightColor = value; RedrawChart(); }
    }

    [Display(Name = "Enable", GroupName = "Delta Divergence", Order = 70,
        Description = "Label the bar delta when it disagrees with the candle's direction")]
    [Tab(TabName = VisualTab)]
    public bool ShowDivergence
    {
        get => _showDivergence;
        set { _showDivergence = value; RaisePropertyChanged(nameof(ShowDivergence)); RedrawChart(); }
    }

    [Display(Name = "Positive", GroupName = "Delta Divergence", Order = 80,
        Description = "Positive delta on a down candle; shown above the candle")]
    [Tab(TabName = VisualTab)]
    [VisibleWhen(nameof(ShowDivergence), true)]
    public Color PositiveDivergenceColor
    {
        get => _divPosColor;
        set { _divPosColor = value; RedrawChart(); }
    }

    [Display(Name = "Negative", GroupName = "Delta Divergence", Order = 90,
        Description = "Negative delta on an up candle; shown below the candle")]
    [Tab(TabName = VisualTab)]
    [VisibleWhen(nameof(ShowDivergence), true)]
    public Color NegativeDivergenceColor
    {
        get => _divNegColor;
        set { _divNegColor = value; RedrawChart(); }
    }

    public Spectraesque() : base(true)
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

        // Profile rows: one per price level, or ProfileScale ticks each. labelRowH is the
        // on-chart height of one profile row (measured from the chart's own price mapping),
        // which drives the delta-label threshold and font size.
        var tick = InstrumentInfo?.TickSize ?? 0m;
        var ticksPerRow = tick > 0 ? _profileScale : 1;
        var labelRowH = rowH;
        if (ticksPerRow > 1)
        {
            var size = tick * ticksPerRow;
            var refPrice = GetCandle(lastBar)?.Close ?? 0m;
            var refRow = Math.Floor(refPrice / size) * size;
            labelRowH = refPrice > 0
                ? Math.Max(rowH, chart.GetYByPrice(refRow, true) - chart.GetYByPrice(refRow + size, true))
                : rowH * ticksPerRow;
        }

        // Historical layout: every visible bar except the live one.
        if ((layout & DrawingLayouts.Historical) != 0)
        {
            var to = Math.Min(lastVisible, lastBar - 1);
            for (var bar = firstVisible; bar <= to; bar++)
                DrawBar(ctx, chart, bar, groupOffset, zone, gap, rowH, labelRowH, tick, ticksPerRow, _historicalBuf);
        }

        // LatestBar layout: the live bar, redrawn on ticks.
        if ((layout & DrawingLayouts.LatestBar) != 0 && lastBar >= firstVisible && lastBar <= lastVisible)
            DrawBar(ctx, chart, lastBar, groupOffset, zone, gap, rowH, labelRowH, tick, ticksPerRow, _latestBuf);
    }

    private void DrawBar(RenderContext ctx, IChart chart, int bar, int groupOffset, int zone, int gap, int rowH,
        int labelRowH, decimal tick, int ticksPerRow, LevelBuffer buf)
    {
        var candle = GetCandle(bar);
        if (candle == null) return;

        buf.Load(candle, chart, rowH, tick, ticksPerRow);

        var x = chart.GetXByBar(bar, true) + groupOffset;
        var candleLeft = x + zone;
        var candleRight = candleLeft + zone;
        var h = rowH > 3 ? rowH - 1 : rowH;

        var maxDelta = buf.MaxAbsDelta;
        var maxVol = buf.MaxVolume;

        var candleDelta = candle.Delta;
        int pocY = 0, pocW = 0, pocH = 0; // POC bar geometry, for the highlight box
        int profTop = int.MaxValue, profBottom = int.MinValue; // profile's vertical extent

        var showText = labelRowH >= DeltaTextMinRowPx;
        var fontPx = Math.Clamp(DeltaTextMinPx + (int)((labelRowH - DeltaTextMinRowPx) * DeltaTextGrowth), DeltaTextMinPx, DeltaTextMaxPx);
        var deltaFont = DeltaFonts[fontPx - DeltaTextMinPx];
        var textRight = candleLeft - DeltaTextPadPx;
        var textBoxW = Math.Max(3 * zone, 64);

        for (var i = 0; i < buf.Count; i++)
        {
            var y = buf.Y[i];
            var rh = buf.H[i];
            var ph = rh > 3 ? rh - 1 : rh; // 1px gap between rows once they're tall enough
            if (y < profTop) profTop = y;
            if (y + ph > profBottom) profBottom = y + ph;

            if (maxDelta > 0)
            {
                var d = buf.Delta[i];
                var ratio = (double)(Math.Abs(d) / maxDelta);
                var dw = (int)(zone * ratio);
                if (dw > 0)
                    ctx.FillRectangle(Shade(d >= 0 ? _posColor : _negColor, ratio), new Rectangle(candleLeft - dw, y, dw, ph));
            }

            // Delta value, drawn even when the bar is shorter than the text (or zero), so it
            // may extend past the bar. The layout box is deliberately wide so it never clips.
            if (showText)
                ctx.DrawString(buf.Label(buf.Delta[i]), deltaFont, DeltaTextColor,
                    new Rectangle(textRight - textBoxW, y, textBoxW, ph), DeltaTextFormat);

            if (maxVol > 0)
            {
                var ratio = (double)(buf.Volume[i] / maxVol);
                var vw = (int)(zone * ratio);
                if (vw > 0)
                {
                    if (i == buf.PocIndex) { pocY = y; pocW = vw; pocH = ph; }
                    ctx.FillRectangle(Shade(_volColor, ratio), new Rectangle(candleRight, y, vw, ph));
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
            ctx.FillRectangle(_candleColor, new Rectangle(wickX, yHigh, lineW, yTop - yHigh));
        if (yLow > bodyBottom)
            ctx.FillRectangle(_candleColor, new Rectangle(wickX, bodyBottom, lineW, yLow - bodyBottom));

        // Body: down = solid; up = hollow (border only, transparent interior).
        // A body too small to fit an interior inside its border is drawn solid.
        var innerW = bodyW - 2 * lineW;
        var innerH = bodyH - 2 * lineW;
        if (!up || innerW <= 0 || innerH <= 0)
        {
            ctx.FillRectangle(_candleColor, new Rectangle(bodyX, yTop, bodyW, bodyH));
        }
        else
        {
            ctx.FillRectangle(_candleColor, new Rectangle(bodyX, yTop, bodyW, lineW));                          // top
            ctx.FillRectangle(_candleColor, new Rectangle(bodyX, bodyBottom - lineW, bodyW, lineW));            // bottom
            ctx.FillRectangle(_candleColor, new Rectangle(bodyX, yTop + lineW, lineW, innerH));                 // left
            ctx.FillRectangle(_candleColor, new Rectangle(bodyX + bodyW - lineW, yTop + lineW, lineW, innerH)); // right
        }

        // POC highlight: translucent box over the POC bar (drawn after the profile, so it tints
        // the bar and any neighboring rows it covers, as in the reference). Padding scales with
        // the zone width; horizontally it also shrinks so the box never covers the candle
        // body or reaches past the midpoint of the gap to the next bar.
        if (_showPocHighlight && pocW > 0)
        {
            var pad = Math.Clamp((int)Math.Round(zone * PocPadRatio), PocMinPadPx, PocMaxPadPx);
            var padX = Math.Min(pad, Math.Min(candleRight - (bodyX + bodyW) - 1, gap / 2 - 1));
            if (padX < 0) padX = 0;
            ctx.FillRectangle(_pocHighlightColor,
                new Rectangle(candleRight - padX, pocY - pad, pocW + 2 * padX, pocH + 2 * pad));
        }

        // Delta divergence label. On the live bar this appears, flips or disappears as the
        // close and delta move, and is fixed once the bar closes.
        var divBelow = _showDivergence && candle.Close > candle.Open && candleDelta < 0;
        var divAbove = _showDivergence && candle.Close < candle.Open && candleDelta > 0;
        if (divBelow || divAbove)
        {
            var boxW = Math.Max(3 * zone, 160);
            var boxH = 3 * DivTextAtasPx; // ~2x the rendered line height, so the text never clips
            var boxX = bodyX + bodyW / 2 - boxW / 2;
            var label = buf.DivergenceLabel(candleDelta);
            // Measured from whichever reaches further, the wick or the profile rows (which can
            // extend past the wick when the profile scale is above 1), so the label never overlaps.
            if (divBelow)
            {
                var baseY = buf.Count > 0 ? Math.Max(yLow, profBottom) : yLow;
                ctx.DrawString(label, DivFont, _divNegColor, new Rectangle(boxX, baseY + DivTextGapPx, boxW, boxH), DivBelowFormat);
            }
            else
            {
                var baseY = buf.Count > 0 ? Math.Min(yHigh, profTop) : yHigh;
                ctx.DrawString(label, DivFont, _divPosColor, new Rectangle(boxX, baseY - DivTextGapPx - boxH, boxW, boxH), DivAboveFormat);
            }
        }
    }

    // Full color at ratio 1, fading linearly (via alpha) toward the chart background,
    // never below MinAlpha. Scales the color's own alpha, so a translucent color picked in
    // the settings stays translucent. Color is a struct, so this does not allocate.
    private static Color Shade(Color full, double ratio)
    {
        var a = (int)(full.A * ratio + 0.5);
        var floor = MinAlpha * full.A / 255;
        if (a < floor) a = floor;
        else if (a > full.A) a = full.A;
        return Color.FromArgb(a, full);
    }

    // Copies a candle's price levels into reusable profile rows, computing the per-bar maxima
    // along the way. Either one row per price level, or (ticksPerRow > 1) levels summed into
    // rows of ticksPerRow ticks. Arrays grow only when a bar needs more rows than ever seen
    // before, so steady-state rendering does not allocate here.
    private sealed class LevelBuffer
    {
        private readonly PriceVolumeInfo _cache = new();
        private const int MaxRows = 100_000; // sanity cap against a corrupt high/low

        public int[] Y = new int[128];  // row top
        public int[] H = new int[128];  // row height (before the 1px row gap)
        public decimal[] Volume = new decimal[128];
        public decimal[] Delta = new decimal[128];
        private bool[] _used = new bool[128];
        public int Count;
        public decimal MaxVolume;
        public decimal MaxAbsDelta;
        public int PocIndex; // index of the POC row in the arrays, or -1

        public void Load(IndicatorCandle candle, IChart chart, int rowH, decimal tickSize, int ticksPerRow)
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

            var levels = candle.GetAllPriceLevels(_cache);
            if (levels == null) return;

            if (ticksPerRow <= 1 || tickSize <= 0)
            {
                // One row per price level, at the chart's own row height.
                var maxVolIndex = -1;
                foreach (var l in levels)
                {
                    if (Count == Y.Length) Grow();

                    var d = l.Ask - l.Bid;
                    var ad = Math.Abs(d);
                    if (ad > MaxAbsDelta) MaxAbsDelta = ad;
                    if (l.Volume > MaxVolume) { MaxVolume = l.Volume; maxVolIndex = Count; }
                    if (hasAtasPoc && l.Price == atasPocPrice) PocIndex = Count;

                    Y[Count] = chart.GetYByPrice(l.Price, true);
                    H[Count] = rowH;
                    Volume[Count] = l.Volume;
                    Delta[Count] = d;
                    Count++;
                }

                if (PocIndex < 0) PocIndex = maxVolIndex;
                return;
            }

            // Grouped rows of ticksPerRow ticks, aligned to multiples of the row size (like ATAS's
            // chart scale) so rows line up across bars. Row k covers [k*size, (k+1)*size), counted
            // from the row holding the candle's low. Levels are summed into their row first, then
            // the non-empty rows are compacted to the front of the arrays (in place: the write
            // index never passes the read index).
            var size = tickSize * ticksPerRow;
            var baseK = Math.Floor(candle.Low / size);
            var n = (int)(Math.Floor(candle.High / size) - baseK) + 1;
            if (n <= 0 || n > MaxRows) return;
            while (n > Y.Length) Grow();
            Array.Clear(Volume, 0, n);
            Array.Clear(Delta, 0, n);
            Array.Clear(_used, 0, n);

            foreach (var l in levels)
            {
                var k = (int)(Math.Floor(l.Price / size) - baseK);
                if ((uint)k >= (uint)n) continue;
                Volume[k] += l.Volume;
                Delta[k] += l.Ask - l.Bid;
                _used[k] = true;
            }

            var atasPocK = hasAtasPoc ? (int)(Math.Floor(atasPocPrice / size) - baseK) : -1;
            var atasPocRow = -1;
            var maxVolRow = -1;
            for (var k = 0; k < n; k++)
            {
                if (!_used[k]) continue;
                var c = Count;
                var bottomPrice = (baseK + k) * size;
                var top = chart.GetYByPrice(bottomPrice + size - tickSize, true); // top of the row's highest tick
                Y[c] = top;
                H[c] = chart.GetYByPrice(bottomPrice, true) + rowH - top;          // to the bottom of its lowest tick
                Volume[c] = Volume[k];
                Delta[c] = Delta[k];

                var ad = Math.Abs(Delta[c]);
                if (ad > MaxAbsDelta) MaxAbsDelta = ad;
                if (Volume[c] > MaxVolume) { MaxVolume = Volume[c]; maxVolRow = c; }
                if (k == atasPocK) atasPocRow = c;
                Count++;
            }

            // POC = the heaviest row; on a tie, the row holding ATAS's own POC.
            PocIndex = atasPocRow >= 0 && Volume[atasPocRow] == MaxVolume ? atasPocRow : maxVolRow;
        }

        // Formatted delta labels, cached so steady-state rendering doesn't allocate strings.
        // Per buffer (i.e. per layout), so concurrent renders never share the dictionary.
        private readonly Dictionary<decimal, string> _labels = new();
        private const int MaxCachedLabels = 4096;

        public string Label(decimal value)
        {
            if (_labels.TryGetValue(value, out var s)) return s;
            if (_labels.Count >= MaxCachedLabels) _labels.Clear();
            s = value.ToString("0.########", CultureInfo.InvariantCulture);
            _labels[value] = s;
            return s;
        }

        // Signed bar delta plus the delta symbol, e.g. "+212 Δ" / "-159 Δ"; cached the same way.
        private readonly Dictionary<decimal, string> _divLabels = new();

        public string DivergenceLabel(decimal value)
        {
            if (_divLabels.TryGetValue(value, out var s)) return s;
            if (_divLabels.Count >= MaxCachedLabels) _divLabels.Clear();
            s = value.ToString("+0.########;-0.########;0", CultureInfo.InvariantCulture) + " Δ";
            _divLabels[value] = s;
            return s;
        }

        private void Grow()
        {
            var n = Y.Length * 2;
            Array.Resize(ref Y, n);
            Array.Resize(ref H, n);
            Array.Resize(ref _used, n);
            Array.Resize(ref Volume, n);
            Array.Resize(ref Delta, n);
        }
    }
}
