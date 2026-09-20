using System.Collections;
using System.Collections.Specialized;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using EdgeRetails.Desktop.Services;

namespace EdgeRetails.Desktop.Controls;

public sealed class FinancialTrendChart : FrameworkElement
{
    public static readonly DependencyProperty PointsProperty =
        DependencyProperty.Register(
            nameof(Points),
            typeof(IEnumerable),
            typeof(FinancialTrendChart),
            new FrameworkPropertyMetadata(
                null,
                FrameworkPropertyMetadataOptions.AffectsRender,
                OnPointsChanged));

    public static readonly DependencyProperty ShowExpensesProperty =
        DependencyProperty.Register(
            nameof(ShowExpenses),
            typeof(bool),
            typeof(FinancialTrendChart),
            new FrameworkPropertyMetadata(
                true,
                FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ProfitSeriesLabelProperty =
        DependencyProperty.Register(
            nameof(ProfitSeriesLabel),
            typeof(string),
            typeof(FinancialTrendChart),
            new FrameworkPropertyMetadata(
                "Profit",
                FrameworkPropertyMetadataOptions.AffectsRender));

    public IEnumerable? Points
    {
        get => (IEnumerable?)GetValue(PointsProperty);
        set => SetValue(PointsProperty, value);
    }

    public bool ShowExpenses
    {
        get => (bool)GetValue(ShowExpensesProperty);
        set => SetValue(ShowExpensesProperty, value);
    }

    public string ProfitSeriesLabel
    {
        get => (string)GetValue(ProfitSeriesLabelProperty);
        set => SetValue(ProfitSeriesLabelProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);

        var points = Points?.Cast<object>()
            .OfType<ReportTrendPoint>()
            .ToArray() ?? [];

        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 140 || height <= 120)
        {
            return;
        }

        var textBrush = GetBrush("Brush.Text.Muted", Brushes.Gray);
        var primaryTextBrush = GetBrush("Brush.Text.Primary", Brushes.Black);
        var borderBrush = GetBrush("Brush.Border.Default", Brushes.LightGray);
        var salesBrush = GetBrush("Brush.Brand.Primary", Brushes.RoyalBlue);
        var profitBrush = GetBrush("Brush.Function.Success", Brushes.SeaGreen);
        var expenseBrush = GetBrush("Brush.Function.Expense", Brushes.DarkOrange);

        DrawLegend(
            drawingContext,
            salesBrush,
            profitBrush,
            expenseBrush,
            primaryTextBrush,
            width);

        var plot = new Rect(
            58,
            42,
            Math.Max(1, width - 82),
            Math.Max(1, height - 78));

        var allValues = points
            .SelectMany(point => ShowExpenses
                ? new[] { point.Sales, point.Profit, point.Expenses }
                : [point.Sales, point.Profit])
            .ToArray();

        if (points.Length == 0 || allValues.All(value => value == 0m))
        {
            DrawGrid(drawingContext, plot, borderBrush, textBrush, 0m, 1m);
            DrawCenteredMessage(
                drawingContext,
                "No activity in selected period",
                textBrush,
                plot);
            return;
        }

        var min = Math.Min(0m, allValues.Min());
        var max = Math.Max(0m, allValues.Max());
        if (max == min)
        {
            max = min + 1m;
        }

        var margin = Math.Max(1m, (max - min) * 0.08m);
        max += margin;
        if (min < 0m)
        {
            min -= margin;
        }

        DrawGrid(drawingContext, plot, borderBrush, textBrush, min, max);
        DrawXAxisLabels(drawingContext, plot, points, textBrush);

        DrawSeries(
            drawingContext,
            plot,
            points,
            point => point.Sales,
            salesBrush,
            min,
            max);

        DrawSeries(
            drawingContext,
            plot,
            points,
            point => point.Profit,
            profitBrush,
            min,
            max);

        if (ShowExpenses)
        {
            DrawSeries(
                drawingContext,
                plot,
                points,
                point => point.Expenses,
                expenseBrush,
                min,
                max);
        }
    }

    private void DrawLegend(
        DrawingContext dc,
        Brush salesBrush,
        Brush profitBrush,
        Brush expenseBrush,
        Brush textBrush,
        double width)
    {
        var font = GetTypeface();
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var x = Math.Max(4, width - (ShowExpenses ? 360 : 250));
        DrawLegendItem(dc, ref x, "Sales", salesBrush, textBrush, font, dpi);
        DrawLegendItem(dc, ref x, ProfitSeriesLabel, profitBrush, textBrush, font, dpi);
        if (ShowExpenses)
        {
            DrawLegendItem(dc, ref x, "Expenses", expenseBrush, textBrush, font, dpi);
        }
    }

    private static void DrawLegendItem(
        DrawingContext dc,
        ref double x,
        string label,
        Brush seriesBrush,
        Brush textBrush,
        Typeface font,
        double dpi)
    {
        dc.DrawEllipse(seriesBrush, null, new Point(x + 4, 13), 4, 4);
        x += 12;

        var text = CreateText(label, 11.5, textBrush, font, dpi);
        dc.DrawText(text, new Point(x, 5));
        x += text.Width + 22;
    }

    private void DrawGrid(
        DrawingContext dc,
        Rect plot,
        Brush borderBrush,
        Brush textBrush,
        decimal min,
        decimal max)
    {
        var gridPen = new Pen(borderBrush, 1);
        var font = GetTypeface();
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        const int lines = 4;
        for (var i = 0; i <= lines; i++)
        {
            var fraction = i / (double)lines;
            var y = plot.Top + (plot.Height * fraction);
            dc.DrawLine(
                gridPen,
                new Point(plot.Left, y),
                new Point(plot.Right, y));

            var value = max - ((max - min) * (decimal)fraction);
            var label = CreateText(
                FormatAxisValue(value),
                10.5,
                textBrush,
                font,
                dpi);
            dc.DrawText(
                label,
                new Point(Math.Max(2, plot.Left - label.Width - 8), y - (label.Height / 2)));
        }

        if (min < 0m && max > 0m)
        {
            var zeroY = ValueToY(0m, plot, min, max);
            var zeroPen = new Pen(textBrush, 1.15);
            dc.DrawLine(
                zeroPen,
                new Point(plot.Left, zeroY),
                new Point(plot.Right, zeroY));
        }
    }

    private void DrawXAxisLabels(
        DrawingContext dc,
        Rect plot,
        IReadOnlyList<ReportTrendPoint> points,
        Brush textBrush)
    {
        if (points.Count == 0)
        {
            return;
        }

        var font = GetTypeface();
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        for (var i = 0; i < points.Count; i++)
        {
            var labelValue = points[i].Label;
            if (string.IsNullOrWhiteSpace(labelValue))
            {
                continue;
            }

            var x = PointToX(i, points.Count, plot);
            var text = CreateText(labelValue, 10.5, textBrush, font, dpi);
            dc.DrawText(
                text,
                new Point(
                    Math.Clamp(x - (text.Width / 2), plot.Left, plot.Right - text.Width),
                    plot.Bottom + 8));
        }
    }

    private static void DrawSeries(
        DrawingContext dc,
        Rect plot,
        IReadOnlyList<ReportTrendPoint> points,
        Func<ReportTrendPoint, decimal> selector,
        Brush brush,
        decimal min,
        decimal max)
    {
        if (points.Count == 0)
        {
            return;
        }

        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            for (var i = 0; i < points.Count; i++)
            {
                var point = new Point(
                    PointToX(i, points.Count, plot),
                    ValueToY(selector(points[i]), plot, min, max));

                if (i == 0)
                {
                    context.BeginFigure(point, isFilled: false, isClosed: false);
                }
                else
                {
                    context.LineTo(point, isStroked: true, isSmoothJoin: true);
                }
            }
        }

        geometry.Freeze();
        var pen = new Pen(brush, 2.15)
        {
            LineJoin = PenLineJoin.Round,
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round
        };
        pen.Freeze();

        dc.DrawGeometry(null, pen, geometry);

        if (points.Count <= 12)
        {
            for (var i = 0; i < points.Count; i++)
            {
                var point = new Point(
                    PointToX(i, points.Count, plot),
                    ValueToY(selector(points[i]), plot, min, max));
                dc.DrawEllipse(brush, null, point, 2.8, 2.8);
            }
        }
    }

    private void DrawCenteredMessage(
        DrawingContext dc,
        string message,
        Brush brush,
        Rect plot)
    {
        var text = CreateText(
            message,
            13,
            brush,
            GetTypeface(),
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

        dc.DrawText(
            text,
            new Point(
                plot.Left + ((plot.Width - text.Width) / 2),
                plot.Top + ((plot.Height - text.Height) / 2)));
    }

    private Brush GetBrush(string key, Brush fallback) =>
        TryFindResource(key) as Brush ?? fallback;

    private Typeface GetTypeface()
    {
        var fontFamily = TryFindResource("Font.Primary") as FontFamily
            ?? new FontFamily("Segoe UI");
        return new Typeface(fontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    }

    private static FormattedText CreateText(
        string value,
        double size,
        Brush brush,
        Typeface typeface,
        double dpi) =>
        new(
            value,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            typeface,
            size,
            brush,
            dpi);

    private static double PointToX(
        int index,
        int count,
        Rect plot) =>
        count <= 1
            ? plot.Left + (plot.Width / 2)
            : plot.Left + ((plot.Width * index) / (count - 1));

    private static double ValueToY(
        decimal value,
        Rect plot,
        decimal min,
        decimal max)
    {
        var range = max - min;
        if (range == 0m)
        {
            return plot.Bottom;
        }

        var normalized = (double)((value - min) / range);
        return plot.Bottom - (normalized * plot.Height);
    }

    private static string FormatAxisValue(decimal value)
    {
        var absolute = Math.Abs(value);
        if (absolute >= 1_000_000m)
        {
            return $"Rs.{value / 1_000_000m:0.#}M";
        }

        if (absolute >= 1_000m)
        {
            return $"Rs.{value / 1_000m:0.#}K";
        }

        return $"Rs.{value:0}";
    }

    private static void OnPointsChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs args)
    {
        var chart = (FinancialTrendChart)dependencyObject;

        if (args.OldValue is INotifyCollectionChanged oldCollection)
        {
            oldCollection.CollectionChanged -= chart.OnCollectionChanged;
        }

        if (args.NewValue is INotifyCollectionChanged newCollection)
        {
            newCollection.CollectionChanged += chart.OnCollectionChanged;
        }

        chart.InvalidateVisual();
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        InvalidateVisual();
}
