using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;

namespace Fluxer.App.Controls;

/// Week × 24 h grid of speed-profile cells. Click or drag paints cells with <see cref="BrushIndex"/>;
/// <see cref="Schedule"/> is one digit per hour, Monday 00:00 first.
public sealed class ScheduleGrid : Control
{
    public static readonly StyledProperty<string> ScheduleProperty =
        AvaloniaProperty.Register<ScheduleGrid, string>(nameof(Schedule), "", defaultBindingMode: BindingMode.TwoWay);
    public static readonly StyledProperty<int> BrushIndexProperty = AvaloniaProperty.Register<ScheduleGrid, int>(nameof(BrushIndex));

    private const double LabelWidth = 40, HeaderHeight = 18, CellHeight = 22, Gap = 3;
    private bool _painting;

    static ScheduleGrid()
    {
        AffectsRender<ScheduleGrid>(ScheduleProperty);
        FocusableProperty.OverrideDefaultValue<ScheduleGrid>(true);
    }

    public ScheduleGrid() => Cursor = new Cursor(StandardCursorType.Hand);

    public string Schedule { get => GetValue(ScheduleProperty); set => SetValue(ScheduleProperty, value); }
    public int BrushIndex { get => GetValue(BrushIndexProperty); set => SetValue(BrushIndexProperty, value); }

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 600 : availableSize.Width, HeaderHeight + 7 * (CellHeight + Gap));

    private double CellWidth => (Bounds.Width - LabelWidth - 23 * Gap) / 24;

    /// Profile colours: accent, blue, grey (the brush swatches use the same).
    public static IBrush CellBrush(Control c, int profile)
    {
        IBrush Find(string key, IBrush fallback) => c.TryFindResource(key, c.ActualThemeVariant, out var v) && v is IBrush b ? b : fallback;
        var color = profile switch
        {
            0 => (Find("SystemControlHighlightAccentBrush", Brushes.Orange) as ISolidColorBrush)?.Color ?? Colors.Orange,
            1 => (Find("InfoBrush", Brushes.CornflowerBlue) as ISolidColorBrush)?.Color ?? Colors.CornflowerBlue,
            _ => Color.Parse("#5B6478"),
        };
        return new SolidColorBrush(color, profile == 0 ? 0.75 : 0.55);
    }

    public override void Render(DrawingContext ctx)
    {
        var faint = this.TryFindResource("FaintBrush", ActualThemeVariant, out var f) && f is IBrush fb ? fb : Brushes.Gray;
        var subtle = this.TryFindResource("SubtleBrush", ActualThemeVariant, out var s) && s is IBrush sb ? sb : Brushes.Gray;
        var typeface = new Typeface(FontFamily.Default);
        var w = CellWidth;
        if (w <= 0) return;

        for (var h = 0; h < 24; h += 3)
        {
            var t = new FormattedText(h.ToString("00", CultureInfo.InvariantCulture), CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, 10, faint);
            ctx.DrawText(t, new Point(LabelWidth + h * (w + Gap) + (w - t.Width) / 2, 0));
        }
        var days = Strings.DayNames.Split('|');
        var brushes = Enumerable.Range(0, 3).Select(i => CellBrush(this, i)).ToArray();
        var sched = Schedule;
        for (var d = 0; d < 7; d++)
        {
            var y = HeaderHeight + d * (CellHeight + Gap);
            var label = new FormattedText(days.ElementAtOrDefault(d) ?? "", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, 12, subtle);
            ctx.DrawText(label, new Point(0, y + (CellHeight - label.Height) / 2));
            for (var h = 0; h < 24; h++)
            {
                var i = d * 24 + h;
                var v = i < sched.Length ? Math.Clamp(sched[i] - '0', 0, 2) : 1;
                ctx.DrawRectangle(brushes[v], null, new Rect(LabelWidth + h * (w + Gap), y, w, CellHeight), 5, 5);
            }
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _painting = true;
        e.Pointer.Capture(this);
        Paint(e.GetPosition(this));
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_painting) Paint(e.GetPosition(this));
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _painting = false;
        e.Pointer.Capture(null);
    }

    private void Paint(Point p)
    {
        var w = CellWidth;
        var h = (int)Math.Floor((p.X - LabelWidth) / (w + Gap));
        var d = (int)Math.Floor((p.Y - HeaderHeight) / (CellHeight + Gap));
        if (h is < 0 or > 23 || d is < 0 or > 6) return;
        var cells = (Schedule.Length == 168 ? Schedule : Core.Downloads.AppSettings.DefaultWeek()).ToCharArray();
        var digit = (char)('0' + Math.Clamp(BrushIndex, 0, 2));
        if (cells[d * 24 + h] == digit) return;
        cells[d * 24 + h] = digit;
        Schedule = new string(cells);
    }
}
