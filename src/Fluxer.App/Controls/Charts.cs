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

    static SegmentMap() => AffectsRender<SegmentMap>(SegmentsProperty, TotalProperty);

    public SegmentRow[] Segments { get => GetValue(SegmentsProperty); set => SetValue(SegmentsProperty, value); }
    public long Total { get => GetValue(TotalProperty); set => SetValue(TotalProperty, value); }

    public override void Render(DrawingContext ctx)
    {
        var bounds = new Rect(Bounds.Size);
        var accent = Accent(this);
        ctx.DrawRectangle(new SolidColorBrush(accent, 0.12), null, bounds, 4, 4);
        var total = Total;
        if (total <= 0 || Segments.Length == 0) return;

        var fill = new SolidColorBrush(accent);
        var edge = new Pen(new SolidColorBrush(accent, 0.9), 1);
        using (ctx.PushClip(new RoundedRect(bounds, 4)))
        {
            foreach (var s in Segments)
            {
                var x0 = s.Start / (double)total * bounds.Width;
                var x1 = Math.Min(s.Position, s.End) / (double)total * bounds.Width;
                if (x1 > x0) ctx.FillRectangle(fill, new Rect(x0, 0, Math.Max(1, x1 - x0), bounds.Height));
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

        var label = new FormattedText(Loc.Speed((long)max), CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            Typeface.Default, 11, new SolidColorBrush(accent));
        ctx.DrawText(label, new Point(4, 0));
    }
}
