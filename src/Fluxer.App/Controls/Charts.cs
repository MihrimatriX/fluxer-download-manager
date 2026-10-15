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

        var edge = new Pen(new SolidColorBrush(Colors.Black, 0.35), 1);
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
                if (s.Start > 0) ctx.DrawLine(edge, new Point(x0, 0), new Point(x0, bounds.Height));
            }
        }
    }

    internal static Color Accent(Control c) =>
        c.TryFindResource("SystemAccentColor", c.ActualThemeVariant, out var v) && v is Color col ? col : Color.Parse("#0A84FF");
}

/// Area chart of the last minute of download speed.
public sealed class SpeedGraph : Control
{
    public static readonly StyledProperty<double[]> ValuesProperty =
        AvaloniaProperty.Register<SpeedGraph, double[]>(nameof(Values), []);

    static SpeedGraph() => AffectsRender<SpeedGraph>(ValuesProperty);

    public double[] Values { get => GetValue(ValuesProperty); set => SetValue(ValuesProperty, value); }

    public int Capacity { get; set; } = 240;

    public override void Render(DrawingContext ctx)
    {
        var b = new Rect(Bounds.Size);
        var accent = SegmentMap.Accent(this);
        var grid = new Pen(new SolidColorBrush(accent, 0.15), 1);
        for (var i = 1; i < 4; i++) ctx.DrawLine(grid, new Point(0, b.Height * i / 4), new Point(b.Width, b.Height * i / 4));

        var values = Values;
        if (values.Length < 2) return;
        var max = Math.Max(values.Max(), 1);
        var step = b.Width / (Capacity - 1);
        var x0 = b.Width - (values.Length - 1) * step;

        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.BeginFigure(new Point(x0, b.Height), true);
            for (var i = 0; i < values.Length; i++)
                g.LineTo(new Point(x0 + i * step, b.Height - values[i] / max * (b.Height - 14)));
            g.LineTo(new Point(b.Width, b.Height));
            g.EndFigure(true);
        }
        ctx.DrawGeometry(new SolidColorBrush(accent, 0.25), new Pen(new SolidColorBrush(accent), 1.5), geo);

        if (max < 1024) return;
        var label = new FormattedText(Loc.Speed((long)max), CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            Typeface.Default, 11, new SolidColorBrush(accent));
        ctx.DrawText(label, new Point(4, 0));
    }
}

/// Circular progress (0–100) with a track; Indeterminate spins a short arc.
public sealed class ProgressRing : Control
{
    public static readonly StyledProperty<double> ValueProperty = AvaloniaProperty.Register<ProgressRing, double>(nameof(Value));
    public static readonly StyledProperty<double> ThicknessProperty = AvaloniaProperty.Register<ProgressRing, double>(nameof(Thickness), 6);
    public static readonly StyledProperty<IBrush?> RingBrushProperty = AvaloniaProperty.Register<ProgressRing, IBrush?>(nameof(RingBrush));
    public static readonly StyledProperty<bool> IndeterminateProperty = AvaloniaProperty.Register<ProgressRing, bool>(nameof(Indeterminate));

    private double _spin;
    private readonly Avalonia.Threading.DispatcherTimer _timer;

    static ProgressRing() => AffectsRender<ProgressRing>(ValueProperty, ThicknessProperty, RingBrushProperty, IndeterminateProperty);

    public ProgressRing()
    {
        _timer = new Avalonia.Threading.DispatcherTimer(TimeSpan.FromMilliseconds(33), Avalonia.Threading.DispatcherPriority.Render, (_, _) =>
        {
            _spin = (_spin + 9) % 360;
            InvalidateVisual();
        });
    }

    public double Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Thickness { get => GetValue(ThicknessProperty); set => SetValue(ThicknessProperty, value); }
    public IBrush? RingBrush { get => GetValue(RingBrushProperty); set => SetValue(RingBrushProperty, value); }
    public bool Indeterminate { get => GetValue(IndeterminateProperty); set => SetValue(IndeterminateProperty, value); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IndeterminateProperty)
        {
            if (Indeterminate) _timer.Start(); else _timer.Stop();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _timer.Stop();
        base.OnDetachedFromVisualTree(e);
    }

    public override void Render(DrawingContext ctx)
    {
        var t = Thickness;
        var size = Math.Min(Bounds.Width, Bounds.Height) - t;
        if (size <= 0) return;
        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
        var r = size / 2;
        var brush = RingBrush ?? new SolidColorBrush(SegmentMap.Accent(this));
        var color = brush is ISolidColorBrush sc ? sc.Color : SegmentMap.Accent(this);
        ctx.DrawEllipse(null, new Pen(new SolidColorBrush(color, 0.16), t), center, r, r);

        double start, sweep;
        if (Indeterminate) { start = _spin; sweep = 90; }
        else { start = 0; sweep = Math.Clamp(Value, 0, 100) * 3.6; }
        if (sweep <= 0) return;
        if (sweep >= 359.9) { ctx.DrawEllipse(null, new Pen(brush, t), center, r, r); return; }

        Point At(double deg)
        {
            var rad = (deg - 90) * Math.PI / 180;
            return new Point(center.X + r * Math.Cos(rad), center.Y + r * Math.Sin(rad));
        }
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.BeginFigure(At(start), false);
            g.ArcTo(At(start + sweep), new Size(r, r), 0, sweep > 180, SweepDirection.Clockwise);
            g.EndFigure(false);
        }
        ctx.DrawGeometry(null, new Pen(brush, t, lineCap: PenLineCap.Round), geo);
    }
}
