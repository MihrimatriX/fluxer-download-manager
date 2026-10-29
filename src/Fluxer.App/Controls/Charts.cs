using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Fluxer.Core.Http;

namespace Fluxer.App.Controls;

/// One bar for the whole file; each segment paints its received part, so you can watch
/// connections fill their ranges and new segments appear when a slow one is split.
public sealed class SegmentMap : Control
{
    public static readonly StyledProperty<SegmentRow[]> SegmentsProperty =
        AvaloniaProperty.Register<SegmentMap, SegmentRow[]>(nameof(Segments), []);

    public static readonly StyledProperty<long> TotalProperty = AvaloniaProperty.Register<SegmentMap, long>(nameof(Total));

    /// Each connection in its own colour (matches the dots in the connection table).
    public static readonly StyledProperty<bool> ColorfulProperty = AvaloniaProperty.Register<SegmentMap, bool>(nameof(Colorful));

    static SegmentMap() => AffectsRender<SegmentMap>(SegmentsProperty, TotalProperty, ColorfulProperty);

    public SegmentRow[] Segments { get => GetValue(SegmentsProperty); set => SetValue(SegmentsProperty, value); }
    public long Total { get => GetValue(TotalProperty); set => SetValue(TotalProperty, value); }
    public bool Colorful { get => GetValue(ColorfulProperty); set => SetValue(ColorfulProperty, value); }

    private static readonly Color[] Palette =
    [
        Color.Parse("#3B82F6"), Color.Parse("#10B981"), Color.Parse("#F59E0B"), Color.Parse("#EC4899"),
        Color.Parse("#8B5CF6"), Color.Parse("#06B6D4"), Color.Parse("#EF4444"), Color.Parse("#84CC16"),
    ];

    public static Color ColorFor(int index) => Palette[index % Palette.Length];

    public override void Render(DrawingContext ctx)
    {
        var bounds = new Rect(Bounds.Size);
        var accent = Accent(this);
        var radius = Math.Min(6, bounds.Height / 2);
        ctx.DrawRectangle(new SolidColorBrush(accent, 0.12), null, bounds, radius, radius);
        var total = Total;
        if (total <= 0 || Segments.Length == 0) return;

        using (ctx.PushClip(new RoundedRect(bounds, radius)))
        {
            foreach (var s in Segments)
            {
                var color = Colorful ? ColorFor(s.Index) : accent;
                var x0 = s.Start / (double)total * bounds.Width;
                var xe = Math.Min(s.End, total) / (double)total * bounds.Width;
                var x1 = Math.Min(s.Position, s.End) / (double)total * bounds.Width;
                if (Colorful && xe > x1) ctx.FillRectangle(new SolidColorBrush(color, 0.18), new Rect(x1, 0, xe - x1, bounds.Height));
                if (x1 > x0) ctx.FillRectangle(new SolidColorBrush(color), new Rect(x0, 0, Math.Max(1, x1 - x0), bounds.Height));
            }
        }
    }

    internal static Color Accent(Control c) =>
        c.TryFindResource("SystemAccentColor", c.ActualThemeVariant, out var v) && v is Color col ? col : Color.Parse("#0A84FF");
}

/// Area chart of the last minute of download speed, optionally with the upload speed as a second line.
public sealed class SpeedGraph : Control
{
    public static readonly StyledProperty<double[]> ValuesProperty =
        AvaloniaProperty.Register<SpeedGraph, double[]>(nameof(Values), []);
    public static readonly StyledProperty<double[]> UpValuesProperty =
        AvaloniaProperty.Register<SpeedGraph, double[]>(nameof(UpValues), []);
    /// Large graph: gradient fill and the peak speed in the corner. Off: the detail panel's flat mini chart.
    public static readonly StyledProperty<bool> ShowScaleProperty = AvaloniaProperty.Register<SpeedGraph, bool>(nameof(ShowScale), true);
    /// Line colour; the accent when unset.
    public static readonly StyledProperty<IBrush?> LineBrushProperty = AvaloniaProperty.Register<SpeedGraph, IBrush?>(nameof(LineBrush));
    public IBrush? LineBrush { get => GetValue(LineBrushProperty); set => SetValue(LineBrushProperty, value); }

    static SpeedGraph() => AffectsRender<SpeedGraph>(ValuesProperty, UpValuesProperty, ShowScaleProperty, LineBrushProperty);

    public double[] Values { get => GetValue(ValuesProperty); set => SetValue(ValuesProperty, value); }
    public double[] UpValues { get => GetValue(UpValuesProperty); set => SetValue(UpValuesProperty, value); }
    public bool ShowScale { get => GetValue(ShowScaleProperty); set => SetValue(ShowScaleProperty, value); }

    public int Capacity { get; set; } = 240;

    public override void Render(DrawingContext ctx)
    {
        var b = new Rect(Bounds.Size);
        var accent = (LineBrush as ISolidColorBrush)?.Color ?? SegmentMap.Accent(this);
        var values = Values;
        if (values.Length < 2) return;
        var max = Math.Max(Math.Max(values.Max(), UpValues.Length > 0 ? UpValues.Max() : 0), 1) * 1.05;
        var step = b.Width / (Capacity - 1);
        var x0 = b.Width - (values.Length - 1) * step;
        var top = ShowScale ? 16 : 4;

        Point[] Points(double[] v)
        {
            var x = b.Width - (v.Length - 1) * step;
            var p = new Point[v.Length];
            for (var i = 0; i < v.Length; i++) p[i] = new Point(x + i * step, b.Height - 2 - v[i] / max * (b.Height - 2 - top));
            return p;
        }
        var pts = Points(values);

        // Catmull-Rom through the samples → smooth cubic Béziers (no sharp 4 Hz steps).
        void Curve(StreamGeometryContext g) => CurveThrough(g, pts);
        void CurveThrough(StreamGeometryContext g, Point[] pts)
        {
            for (var i = 0; i < pts.Length - 1; i++)
            {
                var p0 = pts[Math.Max(0, i - 1)];
                var p1 = pts[i];
                var p2 = pts[i + 1];
                var p3 = pts[Math.Min(pts.Length - 1, i + 2)];
                var c1 = new Point(p1.X + (p2.X - p0.X) / 6, Math.Min(b.Height, p1.Y + (p2.Y - p0.Y) / 6));
                var c2 = new Point(p2.X - (p3.X - p1.X) / 6, Math.Min(b.Height, p2.Y - (p3.Y - p1.Y) / 6));
                g.CubicBezierTo(c1, c2, p2);
            }
        }

        var area = new StreamGeometry();
        using (var g = area.Open())
        {
            g.BeginFigure(new Point(x0, b.Height), true);
            g.LineTo(pts[0]);
            Curve(g);
            g.LineTo(new Point(pts[^1].X, b.Height));
            g.EndFigure(true);
        }
        var line = new StreamGeometry();
        using (var g = line.Open())
        {
            g.BeginFigure(pts[0], false);
            Curve(g);
            g.EndFigure(false);
        }
        IBrush fill = ShowScale
            ? new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Color.FromArgb(90, accent.R, accent.G, accent.B), 0), new GradientStop(Color.FromArgb(0, accent.R, accent.G, accent.B), 1) },
            }
            : new SolidColorBrush(accent, 0.12);
        ctx.DrawGeometry(fill, null, area);
        ctx.DrawGeometry(null, new Pen(new SolidColorBrush(accent), ShowScale ? 2 : 1.5, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), line);

        if (UpValues is { Length: >= 2 } upValues && upValues.Any(v => v > 0))
        {
            var upPts = Points(upValues);
            var upLine = new StreamGeometry();
            using (var g = upLine.Open())
            {
                g.BeginFigure(upPts[0], false);
                CurveThrough(g, upPts);
                g.EndFigure(false);
            }
            var blue = this.TryFindResource("InfoBrush", ActualThemeVariant, out var r) && r is IBrush ib ? ib : Brushes.CornflowerBlue;
            ctx.DrawGeometry(null, new Pen(blue, 1.2, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), upLine);
        }

        if (!ShowScale || max < 1024) return;
        var label = new FormattedText(Loc.Speed((long)max), CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            Typeface.Default, 11, new SolidColorBrush(accent));
        ctx.DrawText(label, new Point(4, 0));
    }
}

/// Thin rounded progress bar for list rows (lighter than ProgressBar's template). Value 0–100, fill = Foreground;
/// Indeterminate shows a faint full bar.
public sealed class Bar : Control
{
    public static readonly StyledProperty<double> ValueProperty = AvaloniaProperty.Register<Bar, double>(nameof(Value));
    public static readonly StyledProperty<bool> IndeterminateProperty = AvaloniaProperty.Register<Bar, bool>(nameof(Indeterminate));
    public static readonly StyledProperty<IBrush?> ForegroundProperty = AvaloniaProperty.Register<Bar, IBrush?>(nameof(Foreground));
    public static readonly StyledProperty<IBrush?> BackgroundProperty = AvaloniaProperty.Register<Bar, IBrush?>(nameof(Background));

    static Bar() => AffectsRender<Bar>(ValueProperty, IndeterminateProperty, ForegroundProperty, BackgroundProperty);

    public double Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public bool Indeterminate { get => GetValue(IndeterminateProperty); set => SetValue(IndeterminateProperty, value); }
    public IBrush? Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    public IBrush? Background { get => GetValue(BackgroundProperty); set => SetValue(BackgroundProperty, value); }

    protected override Size MeasureOverride(Size availableSize) => new(0, double.IsNaN(Height) ? 6 : Height);

    public override void Render(DrawingContext ctx)
    {
        var b = new Rect(Bounds.Size);
        var r = b.Height / 2;
        if (Background is { } bg) ctx.DrawRectangle(bg, null, b, r, r);
        if (Foreground is not { } fg) return;
        if (Indeterminate)
        {
            using (ctx.PushOpacity(0.35)) ctx.DrawRectangle(fg, null, b, r, r);
            return;
        }
        var w = b.Width * Math.Clamp(Value, 0, 100) / 100;
        if (w > 0) ctx.DrawRectangle(fg, null, new Rect(0, 0, Math.Max(w, b.Height), b.Height), r, r);
    }
}

/// Calendar heatmap of the last 53 weeks: a column per week, Monday at the top, darker for more downloaded.
/// The level comes from the day's rank among days with traffic, so one huge day does not wash out the rest.
public sealed class Heatmap : Control
{
    public static readonly StyledProperty<IReadOnlyList<Fluxer.Core.Stats.DayTotal>> DaysProperty =
        AvaloniaProperty.Register<Heatmap, IReadOnlyList<Fluxer.Core.Stats.DayTotal>>(nameof(Days), []);

    static Heatmap() => AffectsRender<Heatmap>(DaysProperty);

    public IReadOnlyList<Fluxer.Core.Stats.DayTotal> Days { get => GetValue(DaysProperty); set => SetValue(DaysProperty, value); }

    private const double LabelWidth = 30, MonthHeight = 16, Gap = 2.5;
    public static readonly double[] Levels = [0.10, 0.32, 0.52, 0.76, 1.0];

    public Heatmap() => PointerMoved += (_, e) => ToolTip.SetTip(this, TipAt(e.GetPosition(this)));

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? 700 : availableSize.Width;
        return new Size(width, MonthHeight + 7 * Cell(width) - Gap);
    }

    private static double Cell(double width) => (width - LabelWidth) / 53;

    /// Level 0 (nothing) to 4 for each day.
    public static int[] LevelsOf(IReadOnlyList<Fluxer.Core.Stats.DayTotal> days)
    {
        var active = days.Where(d => d.Down > 0).Select(d => d.Down).ToArray();
        // Rank = how many days moved less; the busiest quarter gets the darkest colour.
        return [.. days.Select(d => d.Down <= 0 ? 0 : 1 + Math.Min(3, 4 * active.Count(v => v < d.Down) / active.Length))];
    }

    public override void Render(DrawingContext ctx)
    {
        var days = Days;
        if (days.Count == 0) return;
        var cell = Cell(Bounds.Width);
        var size = cell - Gap;
        var accent = SegmentMap.Accent(this);
        var faint = this.TryFindResource("FaintBrush", ActualThemeVariant, out var f) && f is IBrush fb ? fb : Brushes.Gray;
        var levels = LevelsOf(days);
        var culture = CultureInfo.CurrentCulture;
        var typeface = new Typeface(FontFamily.Default);

        void Text(string s, double x, double y) =>
            ctx.DrawText(new FormattedText(s, culture, FlowDirection.LeftToRight, typeface, 10, faint), new Point(x, y));

        for (var row = 0; row < 7; row += 2)
            Text(culture.DateTimeFormat.AbbreviatedDayNames[(row + 1) % 7], 0, MonthHeight + row * cell - 1);
        var today = DateOnly.FromDateTime(DateTime.Now);
        for (var i = 0; i < days.Count && days[i].Day <= today; i++)
        {
            int col = i / 7, row = i % 7;
            var x = LabelWidth + col * cell;
            // A month is named over its first full week; the partly shown month at the start has no room.
            if (row == 0 && col > 0 && days[i].Day.Month != days[i - 7].Day.Month && col < 51)
                Text(culture.DateTimeFormat.GetAbbreviatedMonthName(days[i].Day.Month), x, 0);
            var rect = new Rect(x, MonthHeight + row * cell, size, size);
            ctx.DrawRectangle(new SolidColorBrush(accent, Levels[levels[i]]), null, rect, 2, 2);
        }
    }

    private string? TipAt(Point p)
    {
        var cell = Cell(Bounds.Width);
        if (p.X < LabelWidth || p.Y < MonthHeight) return null;
        var i = (int)((p.X - LabelWidth) / cell) * 7 + (int)((p.Y - MonthHeight) / cell);
        if (i < 0 || i >= Days.Count) return null;
        var d = Days[i];
        var date = d.Day.ToString("D", CultureInfo.CurrentCulture);
        return d.Down > 0 || d.Files > 0
            ? $"{date}\n↓ {Loc.Bytes(d.Down)}" + (d.Up > 0 ? $"  ↑ {Loc.Bytes(d.Up)}" : "") + (d.Files > 0 ? $"\n{string.Format(CultureInfo.CurrentCulture, Strings.StatsFiles, d.Files)}" : "")
            : date;
    }
}
