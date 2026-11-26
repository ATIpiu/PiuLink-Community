using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Size = System.Windows.Size;

namespace PiuLink.Host;

internal static class HostMotion
{
    internal static bool Enabled => SystemParameters.ClientAreaAnimation && !SystemParameters.HighContrast;

    internal static DoubleAnimationUsingKeyFrames Travel(double from, double to, double milliseconds, bool closing = false)
    {
        var animation = new DoubleAnimationUsingKeyFrames
        {
            Duration = TimeSpan.FromMilliseconds(milliseconds),
            FillBehavior = FillBehavior.Stop
        };
        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromPercent(0)));
        var curve = closing ? new KeySpline(.36, .04, .63, 1) : new KeySpline(.18, .78, .24, 1);
        animation.KeyFrames.Add(new SplineDoubleKeyFrame(to, KeyTime.FromPercent(1), curve));
        return animation;
    }
}

/// <summary>The navigation rows share a single marker in their scrollable coordinate space.</summary>
public sealed class NavigationMarker : Border
{
    private readonly TranslateTransform _offset = new();
    private readonly ScaleTransform _stretch = new(1, 1, 0, 8);
    private FrameworkElement? _selected;
    private bool _positioned;
    private int _revision;

    public NavigationMarker()
    {
        RenderTransform = new TransformGroup { Children = { _stretch, _offset } };
        Loaded += (_, _) =>
        {
            if (Parent is FrameworkElement parent) parent.SizeChanged += LayoutChanged;
            SystemParameters.StaticPropertyChanged += PreferencesChanged;
            Position(false);
        };
        Unloaded += (_, _) =>
        {
            if (Parent is FrameworkElement parent) parent.SizeChanged -= LayoutChanged;
            SystemParameters.StaticPropertyChanged -= PreferencesChanged;
            _positioned = false;
            ClearMotion();
        };
    }

    private void LayoutChanged(object sender, SizeChangedEventArgs e) => Position(false);
    private void PreferencesChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (!HostMotion.Enabled) Position(false);
    }

    public void Track(FrameworkElement destination)
    {
        if (_selected == destination) return;
        _selected = destination;
        Position(true);
    }

    private void ClearMotion()
    {
        ++_revision;
        _offset.BeginAnimation(TranslateTransform.YProperty, null);
        _stretch.BeginAnimation(ScaleTransform.ScaleYProperty, null);
    }

    private void Position(bool animate)
    {
        if (!IsLoaded || _selected is not { ActualHeight: > 0 } tab || Parent is not UIElement track) return;
        var target = tab.TranslatePoint(new System.Windows.Point(0, tab.ActualHeight / 2), track).Y - Height / 2;
        var from = _offset.Y;
        var stretch = _stretch.ScaleY;
        ClearMotion();
        if (animate && _positioned && HostMotion.Enabled && IsVisible && Math.Abs(target - from) > .5)
        {
            _offset.Y = from;
            _stretch.ScaleY = stretch;
            var distance = Math.Abs(target - from);
            var travel = HostMotion.Travel(from, target, Math.Clamp(155 + distance * .32, 170, 250));
            travel.FillBehavior = FillBehavior.HoldEnd;
            var length = new DoubleAnimationUsingKeyFrames { Duration = travel.Duration };
            length.KeyFrames.Add(new DiscreteDoubleKeyFrame(stretch, KeyTime.FromPercent(0)));
            length.KeyFrames.Add(new SplineDoubleKeyFrame(Math.Min(1.78, 1 + distance / 140),
                KeyTime.FromPercent(.33), new KeySpline(.2,.7,.3,1)));
            length.KeyFrames.Add(new SplineDoubleKeyFrame(1, KeyTime.FromPercent(1), new KeySpline(.2,.65,.3,1)));
            var revision = _revision;
            travel.Completed += (_, _) =>
            {
                if (_revision != revision) return;
                ClearMotion();
                _offset.Y = target;
                _stretch.ScaleY = 1;
            };
            _stretch.BeginAnimation(ScaleTransform.ScaleYProperty, length);
            _offset.BeginAnimation(TranslateTransform.YProperty, travel);
        }
        else
        {
            _offset.Y = target;
            _stretch.ScaleY = 1;
        }
        _positioned = true;
    }
}

/// <summary>Uses two equally wide columns when both controls have room, otherwise one.</summary>
public sealed class ResponsiveColumns : System.Windows.Controls.Panel
{
    private const double Gap = 16;
    private const double TwoColumnWidth = 640;

    protected override Size MeasureOverride(Size available) => Layout(available.Width, false);
    protected override Size ArrangeOverride(Size finalSize)
    {
        Layout(finalSize.Width, true);
        return finalSize;
    }

    private Size Layout(double width, bool arrange)
    {
        var columns = width >= TwoColumnWidth ? 2 : 1;
        var columnWidth = Math.Max(0, (width - Gap * (columns - 1)) / columns);
        var column = 0;
        double y = 0, rowHeight = 0, desiredWidth = 0;
        foreach (UIElement child in InternalChildren)
        {
            if (child.Visibility == Visibility.Collapsed) continue;
            if (!arrange) child.Measure(new Size(columnWidth, double.PositiveInfinity));
            var height = child.DesiredSize.Height;
            if (arrange) child.Arrange(new Rect(column * (columnWidth + Gap), y, columnWidth, height));
            rowHeight = Math.Max(rowHeight, height);
            desiredWidth = Math.Max(desiredWidth, child.DesiredSize.Width);
            if (++column == columns)
            {
                y += rowHeight + Gap;
                column = 0;
                rowHeight = 0;
            }
        }
        var heightUsed = column == 0 ? Math.Max(0, y - Gap) : y + rowHeight;
        return new Size(Math.Min(width, desiredWidth * columns + Gap * (columns - 1)), heightUsed);
    }
}

/// <summary>Reveal a section at its natural size, clipping only the moving edge.</summary>
public sealed class SectionBody : Decorator
{
    public static readonly DependencyProperty IsOpenProperty = DependencyProperty.Register(
        nameof(IsOpen), typeof(bool), typeof(SectionBody), new PropertyMetadata(false, OpenChanged));
    public static readonly DependencyProperty RevealAngleProperty = DependencyProperty.Register(
        nameof(RevealAngle), typeof(double), typeof(SectionBody),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsMeasure));
    public double RevealAngle => (double)GetValue(RevealAngleProperty);
    private int _revision;

    public bool IsOpen
    {
        get => (bool)GetValue(IsOpenProperty);
        set => SetValue(IsOpenProperty, value);
    }

    public SectionBody()
    {
        ClipToBounds = true;
        Visibility = Visibility.Collapsed;
        IsVisibleChanged += (_, _) => { if (!IsVisible && HasAnimatedProperties) Settle(); };
        Loaded += (_, _) => SystemParameters.StaticPropertyChanged += PreferencesChanged;
        Unloaded += (_, _) => { SystemParameters.StaticPropertyChanged -= PreferencesChanged; Settle(); };
    }

    private void PreferencesChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (!HostMotion.Enabled) Settle();
    }

    private void Settle()
    {
        ++_revision;
        BeginAnimation(RevealAngleProperty, null);
        SetValue(RevealAngleProperty, IsOpen ? 180d : 0d);
        Visibility = IsOpen ? Visibility.Visible : Visibility.Collapsed;
    }

    private static void OpenChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        var body = (SectionBody)sender;
        var open = (bool)e.NewValue;
        var from = body.RevealAngle;
        var to = open ? 180d : 0d;
        var animate = body.IsLoaded && System.Windows.Media.VisualTreeHelper.GetParent(body) is UIElement { IsVisible: true }
                      && HostMotion.Enabled;
        var revision = ++body._revision;
        body.BeginAnimation(RevealAngleProperty, null);
        body.IsEnabled = open;
        if (!animate || from == to)
        {
            body.SetValue(RevealAngleProperty, to);
            body.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            return;
        }
        body.SetValue(RevealAngleProperty, from);
        body.Visibility = Visibility.Visible;
        var remaining = Math.Abs(to - from) / 180;
        var reveal = HostMotion.Travel(from, to, 65 + (open ? 140 : 95) * remaining, closing: !open);
        reveal.FillBehavior = FillBehavior.HoldEnd;
        reveal.Completed += (_, _) => { if (body._revision == revision) body.Settle(); };
        body.BeginAnimation(RevealAngleProperty, reveal);
    }

    protected override Size MeasureOverride(Size available)
    {
        var size = base.MeasureOverride(new Size(available.Width, double.PositiveInfinity));
        return new Size(size.Width, size.Height * RevealAngle / 180);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        Child?.Arrange(new Rect(0, 0, finalSize.Width, Child.DesiredSize.Height));
        return finalSize;
    }
}
