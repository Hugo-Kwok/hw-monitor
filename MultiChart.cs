using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace BetterHwMonitor;

/// <summary>One curve in a chart.</summary>
public sealed class ChartSeries
{
    public ChartSeries(SensorRecord sensor, Color color, bool rightAxis)
    {
        Sensor = sensor;
        RightAxis = rightAxis;
        SetColor(color);
    }

    public SensorRecord Sensor { get; }
    public bool RightAxis { get; }

    /// <summary>Name chosen by the user for this graph only; null = original sensor name.</summary>
    public string? CustomName { get; set; }

    public string Label => string.IsNullOrWhiteSpace(CustomName) ? Sensor.DisplayName : CustomName!;
    public Color Color { get; private set; }
    public SolidColorBrush Brush { get; private set; } = null!;
    public Pen Pen { get; private set; } = null!;

    public void SetColor(Color color)
    {
        Color = color;
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        var pen = new Pen(brush, 1.6) { LineJoin = PenLineJoin.Round };
        pen.Freeze();
        Brush = brush;
        Pen = pen;
    }
}

/// <summary>
/// Lightweight real-time line chart: any number of series, a left and an
/// optional right Y-axis (one unit each), and a hover readout.
/// </summary>
public sealed class MultiChart : FrameworkElement
{
    private const double MarginLeft = 66, MarginRight = 66, MarginTop = 10, MarginBottom = 24;
    private const double LabelSize = 11;

    private static readonly Typeface Face = new("Segoe UI");
    private static readonly Typeface BoldFace =
        new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private static readonly double[] TimeSteps =
        { 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600, 7200, 10800, 21600, 43200, 86400 };

    private Point? _mouse;

    // Theme colors, re-read from the application resources on every render
    private Pen _framePen = null!, _gridPen = null!, _cursorPen = null!;
    private Brush _labelBrush = null!, _tipBackground = null!, _tipText = null!, _background = null!;

    public MultiChart(DataStore store)
    {
        Store = store;
        ClipToBounds = true;
        SnapsToDevicePixels = true;
    }

    public DataStore Store { get; }
    public List<ChartSeries> Series { get; } = new();

    /// <summary>Visible time span in seconds; 0 = everything recorded.</summary>
    public double WindowSeconds { get; set; } = 300;

    /// <summary>Manual Y-axis limits; null = automatic.</summary>
    public double? LeftMin { get; set; }
    public double? LeftMax { get; set; }
    public double? RightMin { get; set; }
    public double? RightMax { get; set; }

    private static T Frozen<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        _mouse = e.GetPosition(this);
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _mouse = null;
        InvalidateVisual();
    }

    private FormattedText Text(string s, Brush brush, double size = LabelSize, bool bold = false) =>
        new(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, bold ? BoldFace : Face, size, brush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

    private sealed record Axis(double Lo, double Hi, List<double> Ticks, int Decimals, string Unit);

    private readonly record struct Mapper(Rect Plot, double TStart, double TEnd)
    {
        public double X(double t) => Plot.Left + (t - TStart) / (TEnd - TStart) * Plot.Width;
        public double Y(double v, Axis a) => Plot.Bottom - (v - a.Lo) / (a.Hi - a.Lo) * Plot.Height;
    }

    private Brush Res(string key, Brush fallback) => TryFindResource(key) as Brush ?? fallback;

    private void LoadTheme()
    {
        _background = Res("ChartBackgroundBrush", Brushes.White);
        _labelBrush = Res("ChartLabelBrush", Brushes.Gray);
        _tipBackground = Res("ChartTipBrush", Brushes.White);
        _tipText = Res("ChartTipTextBrush", Brushes.Black);
        _framePen = new Pen(Res("ChartFrameBrush", Brushes.Gray), 1);
        _gridPen = new Pen(Res("ChartGridBrush", Brushes.LightGray), 1);
        _cursorPen = new Pen(Res("ChartCursorBrush", Brushes.Gray), 1);
    }

    protected override void OnRender(DrawingContext dc)
    {
        LoadTheme();
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRectangle(_background, null, new Rect(0, 0, w, h)); // also makes the control hit-testable
        if (w < MarginLeft + MarginRight + 20 || h < MarginTop + MarginBottom + 20) return;

        var plot = new Rect(MarginLeft, MarginTop, w - MarginLeft - MarginRight, h - MarginTop - MarginBottom);
        var times = Store.Times;

        if (Series.Count == 0 || times.Count < 2)
        {
            dc.DrawRectangle(null, _framePen, plot);
            var hint = Text(Series.Count == 0
                ? "Drag sensors here, or select this graph and double-click a sensor"
                : "Collecting data...", _labelBrush, 13);
            dc.DrawText(hint, new Point(plot.Left + (plot.Width - hint.Width) / 2,
                                        plot.Top + (plot.Height - hint.Height) / 2));
            return;
        }

        double tEnd = times[^1];
        double tStart = WindowSeconds > 0 ? tEnd - WindowSeconds : times[0];
        if (tEnd - tStart < 1e-6) tStart = tEnd - 1;
        int i0 = Math.Max(0, LowerBound(times, tStart) - 1); // one extra point so the line reaches the edge
        int i1 = times.Count - 1;

        var leftSeries = Series.Where(s => !s.RightAxis).ToList();
        var rightSeries = Series.Where(s => s.RightAxis).ToList();
        Axis? leftAxis = leftSeries.Count > 0 ? MakeAxis(leftSeries, i0, i1, LeftMin, LeftMax) : null;
        Axis? rightAxis = rightSeries.Count > 0 ? MakeAxis(rightSeries, i0, i1, RightMin, RightMax) : null;
        Axis gridAxis = leftAxis ?? rightAxis!;
        var m = new Mapper(plot, tStart, tEnd);

        // Horizontal grid lines and Y labels
        foreach (var v in gridAxis.Ticks)
        {
            double y = Math.Round(m.Y(v, gridAxis)) + 0.5;
            dc.DrawLine(_gridPen, new Point(plot.Left, y), new Point(plot.Right, y));
        }
        if (leftAxis != null) DrawYLabels(dc, leftAxis, m, plot, rightSide: false);
        if (rightAxis != null) DrawYLabels(dc, rightAxis, m, plot, rightSide: true);

        // Vertical grid lines and time labels, aligned to the wall clock
        double span = tEnd - tStart;
        double step = TimeSteps.FirstOrDefault(s => span / s <= 7);
        if (step == 0) step = 86400;
        // Show the weekday once the visible range spans more than a day
        string timeFormat = span > 86400 ? "ddd HH:mm" : step >= 60 ? "HH:mm" : "HH:mm:ss";
        double clockOffset = Store.StartTime.TimeOfDay.TotalSeconds;
        double firstTick = Math.Ceiling((tStart + clockOffset) / step) * step - clockOffset;
        for (double t = firstTick; t <= tEnd + 1e-9; t += step)
        {
            double x = Math.Round(m.X(t)) + 0.5;
            dc.DrawLine(_gridPen, new Point(x, plot.Top), new Point(x, plot.Bottom));
            var label = Text(Store.StartTime.AddSeconds(t).ToString(timeFormat, CultureInfo.CurrentCulture), _labelBrush);
            double lx = Math.Max(0, Math.Min(w - label.Width, x - label.Width / 2));
            dc.DrawText(label, new Point(lx, plot.Bottom + 4));
        }

        // Curves
        dc.PushClip(new RectangleGeometry(plot));
        foreach (var s in Series)
            DrawSeries(dc, s, s.RightAxis ? rightAxis! : leftAxis!, m, times, i0, i1, plot.Width);
        dc.Pop();

        dc.DrawRectangle(null, _framePen, plot);

        if (_mouse is Point mp && plot.Contains(mp))
            DrawHover(dc, mp, m, plot, times, i0, i1, leftAxis, rightAxis);
    }

    private Axis MakeAxis(List<ChartSeries> series, int i0, int i1, double? fixedMin, double? fixedMax)
    {
        double lo = double.PositiveInfinity, hi = double.NegativeInfinity;
        foreach (var s in series)
        {
            var hist = s.Sensor.History;
            int last = Math.Min(i1, hist.Count - 1);
            for (int i = i0; i <= last; i++)
            {
                float v = hist[i];
                if (float.IsNaN(v)) continue;
                if (v < lo) lo = v;
                if (v > hi) hi = v;
            }
        }

        if (double.IsInfinity(lo)) { lo = 0; hi = 1; }
        bool nonNegative = lo >= 0;
        if (hi - lo < 1e-9)
        {
            double pad = Math.Max(Math.Abs(lo) * 0.05, 0.5);
            lo -= pad;
            hi += pad;
        }
        else
        {
            double pad = (hi - lo) * 0.05;
            lo -= pad;
            hi += pad;
        }
        if (nonNegative && lo < 0) lo = 0;

        double step = NiceStep((hi - lo) / 5);
        double nlo = Math.Floor(lo / step) * step;
        double nhi = Math.Ceiling(hi / step) * step;
        if (nhi - nlo < step) nhi = nlo + step;

        var ticks = new List<double>();
        int n = (int)Math.Round((nhi - nlo) / step);
        for (int k = 0; k <= n; k++) ticks.Add(nlo + k * step);

        // Manual limits replace the automatic ones (either or both)
        if (fixedMin.HasValue || fixedMax.HasValue)
        {
            double mlo = fixedMin ?? nlo;
            double mhi = fixedMax ?? nhi;
            if (mhi > mlo)
            {
                nlo = mlo;
                nhi = mhi;
                step = NiceStep((nhi - nlo) / 5);
                ticks.Clear();
                for (double v = Math.Ceiling(nlo / step - 1e-9) * step; v <= nhi + step * 1e-6; v += step)
                    ticks.Add(Math.Abs(v) < step * 1e-9 ? 0 : v);
                if (ticks.Count == 0) { ticks.Add(nlo); ticks.Add(nhi); }
            }
        }

        int decimals = step >= 1 ? 0 : (int)Math.Ceiling(-Math.Log10(step) - 1e-9);
        return new Axis(nlo, nhi, ticks, decimals, series[0].Sensor.Unit);
    }

    private static double NiceStep(double raw)
    {
        if (raw <= 0 || double.IsNaN(raw) || double.IsInfinity(raw)) return 1;
        double exp = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        double f = raw / exp;
        double nice = f < 1.5 ? 1 : f < 3 ? 2 : f < 7 ? 5 : 10;
        return nice * exp;
    }

    private void DrawYLabels(DrawingContext dc, Axis axis, Mapper m, Rect plot, bool rightSide)
    {
        foreach (var v in axis.Ticks)
        {
            double y = m.Y(v, axis);
            string s = v.ToString("F" + axis.Decimals, CultureInfo.CurrentCulture);
            if (axis.Unit.Length > 0) s += " " + axis.Unit;
            var label = Text(s, _labelBrush);
            double x = rightSide ? plot.Right + 5 : plot.Left - 5 - label.Width;
            dc.DrawText(label, new Point(x, y - label.Height / 2));
            if (rightSide)
                dc.DrawLine(_framePen, new Point(plot.Right, Math.Round(y) + 0.5), new Point(plot.Right + 3, Math.Round(y) + 0.5));
        }
    }

    private static void DrawSeries(DrawingContext dc, ChartSeries s, Axis axis, Mapper m,
                                   List<double> times, int i0, int i1, double plotWidth)
    {
        var hist = s.Sensor.History;
        int last = Math.Min(i1, hist.Count - 1);
        if (last < i0) return;

        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            bool down = false;
            int count = last - i0 + 1;

            if (count <= plotWidth * 2)
            {
                for (int i = i0; i <= last; i++)
                {
                    float v = hist[i];
                    if (float.IsNaN(v)) { down = false; continue; }
                    var p = new Point(m.X(times[i]), m.Y(v, axis));
                    if (!down) { ctx.BeginFigure(p, false, false); down = true; }
                    else ctx.LineTo(p, true, false);
                }
            }
            else
            {
                // Too many points: keep min and max per pixel column so spikes stay visible.
                int col = 0;
                double cmin = 0, cmax = 0;
                int minIdx = 0, maxIdx = 0;
                bool has = false;
                for (int i = i0; i <= last + 1; i++)
                {
                    bool end = i > last;
                    float v = end ? float.NaN : hist[i];
                    int c = end ? int.MaxValue : (int)m.X(times[i]);

                    if (has && (end || c != col || float.IsNaN(v)))
                    {
                        double x = col + 0.5;
                        double ya = m.Y(minIdx <= maxIdx ? cmin : cmax, axis);
                        double yb = m.Y(minIdx <= maxIdx ? cmax : cmin, axis);
                        if (!down) { ctx.BeginFigure(new Point(x, ya), false, false); down = true; }
                        else ctx.LineTo(new Point(x, ya), true, false);
                        if (yb != ya) ctx.LineTo(new Point(x, yb), true, false);
                        has = false;
                    }

                    if (end) break;
                    if (float.IsNaN(v)) { down = false; continue; }

                    if (!has)
                    {
                        col = c;
                        cmin = cmax = v;
                        minIdx = maxIdx = i;
                        has = true;
                    }
                    else
                    {
                        if (v < cmin) { cmin = v; minIdx = i; }
                        if (v > cmax) { cmax = v; maxIdx = i; }
                    }
                }
            }
        }
        geo.Freeze();
        dc.DrawGeometry(null, s.Pen, geo);
    }

    private void DrawHover(DrawingContext dc, Point mp, Mapper m, Rect plot, List<double> times,
                           int i0, int i1, Axis? leftAxis, Axis? rightAxis)
    {
        double t = m.TStart + (mp.X - plot.Left) / plot.Width * (m.TEnd - m.TStart);
        int idx = LowerBound(times, t);
        if (idx > i1) idx = i1;
        if (idx > 0 && Math.Abs(times[idx - 1] - t) < Math.Abs(times[idx] - t)) idx--;
        if (idx < i0) idx = i0;

        double x = m.X(times[idx]);
        dc.DrawLine(_cursorPen, new Point(x, plot.Top), new Point(x, plot.Bottom));

        // Horizontal crosshair line at the cursor, with the axis value(s) at that height
        double hy = Math.Round(mp.Y) + 0.5;
        dc.DrawLine(_cursorPen, new Point(plot.Left, hy), new Point(plot.Right, hy));
        if (leftAxis != null) DrawAxisTag(dc, leftAxis, mp.Y, plot, rightSide: false);
        if (rightAxis != null) DrawAxisTag(dc, rightAxis, mp.Y, plot, rightSide: true);

        var lines = new List<(FormattedText Text, Brush? Swatch)>
        {
            (Text(Store.StartTime.AddSeconds(times[idx]).ToString(
                      times[^1] - times[0] > 86400 ? "ddd HH:mm:ss" : "HH:mm:ss", CultureInfo.CurrentCulture),
                  _tipText, LabelSize, bold: true), null),
        };

        foreach (var s in Series)
        {
            var hist = s.Sensor.History;
            double? v = idx < hist.Count && !float.IsNaN(hist[idx]) ? hist[idx] : null;
            if (v is double dv)
            {
                var axis = s.RightAxis ? rightAxis! : leftAxis!;
                dc.DrawEllipse(s.Brush, null, new Point(x, m.Y(dv, axis)), 3, 3);
            }
            lines.Add((Text($"{s.Label}: {s.Sensor.Format(v)}", _tipText), s.Brush));
        }

        double boxW = lines.Max(l => l.Text.Width) + 28;
        double boxH = lines.Sum(l => l.Text.Height) + 8;
        double bx = x + 12;
        if (bx + boxW > ActualWidth) bx = x - 12 - boxW;
        if (bx < 0) bx = 0;
        double by = Math.Max(plot.Top, Math.Min(mp.Y - boxH / 2, plot.Bottom - boxH));

        dc.DrawRoundedRectangle(_tipBackground, _framePen, new Rect(bx, by, boxW, boxH), 3, 3);
        double yy = by + 4;
        foreach (var (text, swatch) in lines)
        {
            if (swatch != null)
                dc.DrawRectangle(swatch, null, new Rect(bx + 7, yy + text.Height / 2 - 4, 10, 8));
            dc.DrawText(text, new Point(bx + 22, yy));
            yy += text.Height;
        }
    }

    /// <summary>Small box in the axis label area showing the axis value at the cursor height.</summary>
    private void DrawAxisTag(DrawingContext dc, Axis axis, double y, Rect plot, bool rightSide)
    {
        double value = axis.Lo + (plot.Bottom - y) / plot.Height * (axis.Hi - axis.Lo);
        string s = value.ToString("F" + Math.Min(axis.Decimals + 1, 3), CultureInfo.CurrentCulture);
        if (axis.Unit.Length > 0) s += " " + axis.Unit;

        var text = Text(s, _tipText, LabelSize, bold: true);
        double w = text.Width + 8, h = text.Height + 2;
        double bx = rightSide ? plot.Right + 1 : plot.Left - 1 - w;
        double by = Math.Max(plot.Top, Math.Min(y - h / 2, plot.Bottom - h));

        dc.DrawRoundedRectangle(_tipBackground, _framePen, new Rect(bx, by, w, h), 2, 2);
        dc.DrawText(text, new Point(bx + 4, by + 1));
    }

    /// <summary>First index whose value is >= x.</summary>
    private static int LowerBound(List<double> a, double x)
    {
        int lo = 0, hi = a.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (a[mid] < x) lo = mid + 1; else hi = mid;
        }
        return lo;
    }
}
