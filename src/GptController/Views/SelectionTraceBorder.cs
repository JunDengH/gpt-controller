using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace GptController.Views;

public sealed class SelectionTraceBorder : FrameworkElement
{
    public static readonly DependencyProperty IsSelectedProperty =
        DependencyProperty.Register(
            nameof(IsSelected),
            typeof(bool),
            typeof(SelectionTraceBorder),
            new FrameworkPropertyMetadata(
                false,
                FrameworkPropertyMetadataOptions.AffectsRender,
                OnIsSelectedChanged));

    public static readonly DependencyProperty StrokeProperty =
        DependencyProperty.Register(
            nameof(Stroke),
            typeof(Brush),
            typeof(SelectionTraceBorder),
            new FrameworkPropertyMetadata(
                Brushes.Transparent,
                FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeThicknessProperty =
        DependencyProperty.Register(
            nameof(StrokeThickness),
            typeof(double),
            typeof(SelectionTraceBorder),
            new FrameworkPropertyMetadata(
                1d,
                FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CornerRadiusProperty =
        DependencyProperty.Register(
            nameof(CornerRadius),
            typeof(double),
            typeof(SelectionTraceBorder),
            new FrameworkPropertyMetadata(
                0d,
                FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly DependencyProperty TraceProgressProperty =
        DependencyProperty.Register(
            "TraceProgress",
            typeof(double),
            typeof(SelectionTraceBorder),
            new FrameworkPropertyMetadata(
                0d,
                FrameworkPropertyMetadataOptions.AffectsRender));

    public SelectionTraceBorder()
    {
        IsHitTestVisible = false;
        Loaded += (_, _) => UpdateSelectionState(animate: true);
    }

    public bool IsSelected
    {
        get => (bool)GetValue(IsSelectedProperty);
        set => SetValue(IsSelectedProperty, value);
    }

    public Brush Stroke
    {
        get => (Brush)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public double StrokeThickness
    {
        get => (double)GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    public double CornerRadius
    {
        get => (double)GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    private double TraceProgress
    {
        get => (double)GetValue(TraceProgressProperty);
        set => SetValue(TraceProgressProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);

        var thickness = Math.Max(0d, StrokeThickness);
        var progress = Math.Clamp(TraceProgress, 0d, 1d);
        if (!IsSelected ||
            progress <= 0d ||
            thickness <= 0d ||
            ActualWidth <= thickness ||
            ActualHeight <= thickness)
        {
            return;
        }

        var inset = thickness / 2d;
        var width = ActualWidth - thickness;
        var height = ActualHeight - thickness;
        var radius = Math.Clamp(
            CornerRadius - inset,
            0d,
            Math.Min(width, height) / 2d);
        var geometry = CreateRoundedRectangleGeometry(
            inset,
            inset,
            width,
            height,
            radius);
        var pen = new Pen(Stroke, thickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round
        };

        if (progress < 0.999d)
        {
            var perimeter =
                2d * (width + height - (4d * radius)) +
                (2d * Math.PI * radius);
            var normalizedPerimeter = perimeter / thickness;
            pen.DashStyle = new DashStyle(
                new DoubleCollection
                {
                    Math.Max(0.001d, normalizedPerimeter * progress),
                    normalizedPerimeter + 2d
                },
                0d);
        }

        drawingContext.DrawGeometry(null, pen, geometry);
    }

    private static void OnIsSelectedChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs args)
    {
        var border = (SelectionTraceBorder)dependencyObject;
        if (!border.IsLoaded)
        {
            return;
        }

        border.UpdateSelectionState(animate: (bool)args.NewValue);
    }

    private void UpdateSelectionState(bool animate)
    {
        BeginAnimation(TraceProgressProperty, null);

        if (!IsSelected)
        {
            TraceProgress = 0d;
            return;
        }

        if (!animate || !SystemParameters.ClientAreaAnimation)
        {
            TraceProgress = 1d;
            return;
        }

        TraceProgress = 1d;
        var animation = new DoubleAnimation(
            0d,
            1d,
            TimeSpan.FromMilliseconds(680d))
        {
            EasingFunction = new CubicEase
            {
                EasingMode = EasingMode.EaseInOut
            },
            FillBehavior = FillBehavior.Stop
        };
        BeginAnimation(
            TraceProgressProperty,
            animation,
            HandoffBehavior.SnapshotAndReplace);
    }

    private static Geometry CreateRoundedRectangleGeometry(
        double left,
        double top,
        double width,
        double height,
        double radius)
    {
        var right = left + width;
        var bottom = top + height;
        var figure = new PathFigure
        {
            StartPoint = new Point(left, top + radius),
            IsClosed = true,
            IsFilled = false
        };

        if (radius > 0d)
        {
            var corner = new Size(radius, radius);
            figure.Segments.Add(new ArcSegment(
                new Point(left + radius, top),
                corner,
                0d,
                false,
                SweepDirection.Clockwise,
                true));
            figure.Segments.Add(new LineSegment(
                new Point(right - radius, top),
                true));
            figure.Segments.Add(new ArcSegment(
                new Point(right, top + radius),
                corner,
                0d,
                false,
                SweepDirection.Clockwise,
                true));
            figure.Segments.Add(new LineSegment(
                new Point(right, bottom - radius),
                true));
            figure.Segments.Add(new ArcSegment(
                new Point(right - radius, bottom),
                corner,
                0d,
                false,
                SweepDirection.Clockwise,
                true));
            figure.Segments.Add(new LineSegment(
                new Point(left + radius, bottom),
                true));
            figure.Segments.Add(new ArcSegment(
                new Point(left, bottom - radius),
                corner,
                0d,
                false,
                SweepDirection.Clockwise,
                true));
            figure.Segments.Add(new LineSegment(
                new Point(left, top + radius),
                true));
        }
        else
        {
            figure.Segments.Add(new LineSegment(new Point(right, top), true));
            figure.Segments.Add(new LineSegment(new Point(right, bottom), true));
            figure.Segments.Add(new LineSegment(new Point(left, bottom), true));
            figure.Segments.Add(new LineSegment(new Point(left, top), true));
        }

        return new PathGeometry([figure]);
    }
}
