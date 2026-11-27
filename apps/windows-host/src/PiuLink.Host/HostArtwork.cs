using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;
using SystemColors = System.Windows.SystemColors;

namespace PiuLink.Host;

public enum HostSymbol
{
    Overview, PrivateNetwork, StreamingHost, Displays, VirtualDisplay, WakeOnLan, AdvancedSettings, Diagnostics,
    Address, Invitation, Browser, Services, Device, Download, Microphone, Terminal, Configuration,
    Recovery, Driver, Shield, Brand
}

/// <summary>Embedded settings imagery and brand artwork. Only the visible illustration owns a short-lived animation clock.</summary>
public sealed class HostArtwork : FrameworkElement
{
    public static readonly DependencyProperty SymbolProperty = DependencyProperty.Register(nameof(Symbol), typeof(HostSymbol),
        typeof(HostArtwork), new FrameworkPropertyMetadata(HostSymbol.Overview, FrameworkPropertyMetadataOptions.AffectsRender, SymbolChanged));
    public static readonly DependencyProperty IllustratedProperty = DependencyProperty.Register(nameof(Illustrated), typeof(bool),
        typeof(HostArtwork), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty EngagedProperty = DependencyProperty.Register(nameof(Engaged), typeof(bool),
        typeof(HostArtwork), new PropertyMetadata(false, (d, e) => { if ((bool)e.NewValue) ((HostArtwork)d).Replay(); }));
    private static readonly DependencyProperty PhaseProperty = DependencyProperty.Register("Phase", typeof(double),
        typeof(HostArtwork), new FrameworkPropertyMetadata(1d, FrameworkPropertyMetadataOptions.AffectsRender));
    private static readonly DependencyPropertyKey PlateBrushKey = DependencyProperty.RegisterReadOnly(nameof(PlateBrush),
        typeof(Brush), typeof(HostArtwork), new PropertyMetadata(Brushes.Transparent));
    public static readonly DependencyProperty PlateBrushProperty = PlateBrushKey.DependencyProperty;
    public HostSymbol Symbol { get => (HostSymbol)GetValue(SymbolProperty); set => SetValue(SymbolProperty, value); }
    public bool Illustrated { get => (bool)GetValue(IllustratedProperty); set => SetValue(IllustratedProperty, value); }
    public bool Engaged { get => (bool)GetValue(EngagedProperty); set => SetValue(EngagedProperty, value); }
    public Brush PlateBrush => (Brush)GetValue(PlateBrushProperty);
    private bool _pairing;
    private HostSymbol _playingSymbol;

    private sealed record Ink(Brush Accent, Brush Shade, Pen Outline, Pen Fine, Brush Plate);
    private static readonly Dictionary<string, Geometry> Shapes = new(StringComparer.Ordinal);
    private static readonly Ink[] Palette =
    [
        MakeInk(145, 228, 199), MakeInk(132, 209, 247), MakeInk(255, 183, 132), MakeInk(161, 188, 255),
        MakeInk(200, 167, 252), MakeInk(248, 202, 116), MakeInk(170, 218, 189), MakeInk(115, 218, 225)
    ];
    private static Ink Inks(HostSymbol symbol) => Palette[symbol switch
    {
        HostSymbol.PrivateNetwork or HostSymbol.Address or HostSymbol.Browser => 1,
        HostSymbol.StreamingHost or HostSymbol.Microphone => 2,
        HostSymbol.Displays or HostSymbol.Recovery or HostSymbol.Device => 3,
        HostSymbol.VirtualDisplay or HostSymbol.Driver or HostSymbol.Invitation => 4,
        HostSymbol.WakeOnLan or HostSymbol.Shield => 5,
        HostSymbol.AdvancedSettings or HostSymbol.Terminal or HostSymbol.Configuration => 6,
        HostSymbol.Diagnostics or HostSymbol.Services => 7,
        _ => 0
    }];

    private static T Freeze<T>(T item) where T : Freezable { item.Freeze(); return item; }
    private static Pen Stroke(Brush brush, double width) => Freeze(new Pen(brush, width)
        { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round });
    private static Ink MakeInk(byte r, byte g, byte b)
    {
        Color Tint(double strength) => Color.FromRgb((byte)(20 + (r - 20) * strength),
            (byte)(22 + (g - 22) * strength), (byte)(25 + (b - 25) * strength));
        var accent = Freeze(new SolidColorBrush(Tint(1)));
        var shade = Freeze(new SolidColorBrush(Tint(.21)));
        var plate = Freeze(new LinearGradientBrush(Tint(.07), Tint(.15), 4));
        return new Ink(accent, shade, Stroke(accent, 1.65), Stroke(shade, 1), plate);
    }

    public HostArtwork()
    {
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
        IsHitTestVisible = false;
        Focusable = false;
        UpdatePlate();
        Loaded += (_, _) => { SystemParameters.StaticPropertyChanged += SettingsChanged; UpdatePlate(); if (Illustrated || Symbol == HostSymbol.Brand) Replay(); };
        Unloaded += (_, _) => { SystemParameters.StaticPropertyChanged -= SettingsChanged; BeginAnimation(PhaseProperty, null); };
        IsVisibleChanged += (_, _) => { if (!IsVisible) BeginAnimation(PhaseProperty, null); };
    }

    private void SettingsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SystemParameters.ClientAreaAnimation) or nameof(SystemParameters.HighContrast) or null or "")
        {
            BeginAnimation(PhaseProperty, null);
            UpdatePlate();
            InvalidateVisual();
        }
    }

    private static void SymbolChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        var art = (HostArtwork)sender;
        art.UpdatePlate();
        if (art.IsLoaded) art.Replay();
    }

    private void UpdatePlate()
    {
        Brush plate = Symbol == HostSymbol.Brand ? Brushes.White : Inks(Symbol).Plate;
        if (SystemParameters.HighContrast) plate = Symbol == HostSymbol.Brand ? SystemColors.WindowTextBrush : SystemColors.WindowBrush;
        SetValue(PlateBrushKey, plate);
    }

    public void Replay() => Play(false);
    public void PlayPairing() => Play(true);

    private void Play(bool pairing)
    {
        if (_pairing == pairing && _playingSymbol == Symbol && HasAnimatedProperties) return;
        BeginAnimation(PhaseProperty, null);
        _pairing = pairing;
        _playingSymbol = Symbol;
        if (!IsVisible || !IsLoaded || !HostMotion.Enabled) return;
        var animation = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(Illustrated ? 680 : 360))
            { FillBehavior = FillBehavior.Stop };
        animation.Completed += (_, _) => { if ((double)GetValue(PhaseProperty) >= 1) BeginAnimation(PhaseProperty, null); };
        BeginAnimation(PhaseProperty, animation);
    }

    private static Geometry Shape(string path)
    {
        if (!Shapes.TryGetValue(path, out var shape)) Shapes[path] = shape = Freeze(Geometry.Parse(path));
        return shape;
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        var ink = Inks(Symbol);
        if (SystemParameters.HighContrast)
        {
            var fg = SystemColors.WindowTextBrush;
            ink = new Ink(fg, SystemColors.WindowBrush, Stroke(fg, 1.65), Stroke(fg, 1), SystemColors.WindowBrush);
        }
        var wide = Illustrated && ActualWidth >= 110;
        var width = wide ? 160 : 24;
        var height = wide ? 64 : 24;
        var scale = Math.Min(ActualWidth / width, ActualHeight / height);
        dc.PushTransform(new MatrixTransform(scale, 0, 0, scale, (ActualWidth - width * scale) / 2, (ActualHeight - height * scale) / 2));
        var phase = (double)GetValue(PhaseProperty);
        var pairingWeight = _pairing ? SmoothStep(phase, 0, .16) * (1 - SmoothStep(phase, .73, 1)) : 0;
        if (pairingWeight < 1)
        {
            if (pairingWeight > 0) dc.PushOpacity(1 - pairingWeight);
            var scenePhase = _pairing ? 1 : phase;
            if (wide) DrawScene(dc, ink, scenePhase);
            else DrawGlyph(dc, ink, scenePhase);
            if (pairingWeight > 0) dc.Pop();
        }
        if (pairingWeight > 0)
        {
            dc.PushOpacity(pairingWeight);
            DrawPairing(dc, SmoothStep(phase, .11, .7), wide);
            dc.Pop();
        }
        dc.Pop();
    }

    private static double SmoothStep(double phase, double start, double end)
    {
        var p = Math.Clamp((phase - start) / (end - start), 0, 1);
        return p * p * (3 - 2 * p);
    }

    // The monochrome portals use the same master geometry as assets/brand/piulink-mark.svg.
    private static readonly Geometry LeftPortal = Shape("M448,350 L304,266 C244,231 186,274 186,342 V682 C186,750 244,793 304,758 L448,674 Z");
    private static readonly PathGeometry ConnectionPath = Freeze(PathGeometry.CreateFromGeometry(Shape("M85,29 H94 Q99,29 103,34 L107,38 H114")));
    private static readonly Geometry RightPortal = Shape("M576,350 L720,266 C780,231 838,274 838,342 V682 C838,750 780,793 720,758 L576,674 Z");
    private static readonly Pen PortalDark = Stroke(Brushes.Black, 88);
    private static void DrawBrand(DrawingContext dc, double spread)
    {
        var pen = SystemParameters.HighContrast ? Stroke(SystemColors.WindowBrush, 88) : PortalDark;
        dc.PushTransform(new ScaleTransform(24d / 1024, 24d / 1024));
        dc.PushTransform(new TranslateTransform(-spread,0)); dc.DrawGeometry(null, pen, LeftPortal); dc.Pop();
        dc.PushTransform(new TranslateTransform(spread,0)); dc.DrawGeometry(null, pen, RightPortal); dc.Pop();
        dc.DrawRectangle(pen.Brush, null, new Rect(448-spread,486,128+2*spread,52));
        dc.Pop();
    }

    private void DrawGlyph(DrawingContext dc, Ink ink, double t)
    {
        var beat = Math.Pow(Math.Sin(t * Math.PI), 2);
        var offset = Symbol switch
        {
            HostSymbol.Download => new Vector(0, 2.4 * beat),
            HostSymbol.VirtualDisplay or HostSymbol.Driver => new Vector(0, -1.8 * beat),
            HostSymbol.Displays or HostSymbol.Invitation => new Vector(1.8 * beat, 0),
            _ => new Vector()
        };
        dc.PushTransform(new TranslateTransform(offset.X, offset.Y));
        if (Symbol is HostSymbol.Recovery or HostSymbol.Configuration)
            dc.PushTransform(new RotateTransform(-12 * beat, 12, 12));
        else
            dc.PushTransform(new ScaleTransform(1 + .075 * beat, 1 + .075 * beat, 12, 12));
        if (Symbol == HostSymbol.Brand) DrawBrand(dc, 47 * beat);
        else SettingsIconSheet.Draw(dc, Symbol, new Rect(0, 0, 24, 24));
        dc.Pop();
        dc.Pop();
    }

    private static void DrawPairing(DrawingContext dc, double t, bool wide)
    {
        if (wide)
        {
            SettingsIconSheet.Draw(dc, HostSymbol.Device, new Rect(21 + 9*t, 9, 42, 46));
            SettingsIconSheet.Draw(dc, HostSymbol.Displays, new Rect(99 - 9*t, 10, 48, 44));
            dc.PushOpacity(t);
            SettingsIconSheet.Draw(dc, HostSymbol.Invitation, new Rect(68, 20, 25, 24));
            dc.Pop();
        }
        else
        {
            SettingsIconSheet.Draw(dc, HostSymbol.Invitation, new Rect(0, 2*(1-t), 24, 24));
        }
    }

    private void DrawScene(DrawingContext dc, Ink ink, double t)
    {
        var ease = SmoothStep(t, 0, 1);
        void Icon(HostSymbol symbol, double x, double y, double w, double h) =>
            SettingsIconSheet.Draw(dc, symbol, new Rect(x, y, w, h));
        void Detail(string path) => dc.DrawGeometry(null, ink.Fine, Shape(path));

        for (int x = 9; x < 155; x += 10)
            for (int y = 7; y < 61; y += 10)
                dc.DrawEllipse(ink.Shade, null, new Point(x,y), .7,.7);

        switch (Symbol)
        {
            case HostSymbol.Overview:
                Detail("M9,49 H16 L22,43 M138,18 H148 L154,12");
                dc.DrawGeometry(null, ink.Outline, ConnectionPath);
                ConnectionPath.GetPointAtFractionLength(ease, out var packet, out _);
                dc.DrawEllipse(ink.Accent, null, packet, 2.4, 2.4);
                Icon(Symbol, 20, 3, 66, 57);
                Icon(HostSymbol.Browser, 112, 23, 35, 30);
                break;
            case HostSymbol.PrivateNetwork:
                Icon(Symbol, 46, 3, 67, 58);
                var signal = new Point(60 + 20*ease, 48 - 34*ease);
                dc.PushOpacity(Math.Sin(Math.PI * t));
                dc.DrawEllipse(ink.Accent, null, signal, 2.2, 2.2);
                dc.Pop();
                break;
            case HostSymbol.StreamingHost:
                Detail("M15,22 H38 M9,31 H35 M19,40 H39 M122,25 H145 M125,36 H151");
                Icon(Symbol, 39 - 8*(1-ease), 4, 83, 55);
                break;
            case HostSymbol.Displays:
                Icon(Symbol, 22, 6, 58, 48);
                Icon(Symbol, 91 + 7*(1-ease), 13, 44, 40);
                dc.DrawLine(ink.Outline, new Point(39+62*ease,56), new Point(53+62*ease,56));
                break;
            case HostSymbol.VirtualDisplay:
                var lift = 1 - SmoothStep(t, .12, .94);
                dc.PushOpacity(.25 * Math.Sin(Math.PI*t));
                Icon(Symbol, 43, 5 + 7*(1-ease), 69, 53);
                dc.Pop();
                Icon(Symbol, 47, 4 + 8*lift, 69, 55);
                break;
            case HostSymbol.WakeOnLan:
                Icon(Symbol, 23, 9, 46, 46);
                for (int i = 0; i < 3; i++)
                {
                    var wave = Math.Sin(Math.PI * SmoothStep(t, .18*i, .45+.18*i));
                    dc.PushOpacity(.53 + .46*wave);
                    dc.PushTransform(new TranslateTransform(i*9 + 3*wave, 0));
                    dc.DrawGeometry(null, ink.Outline, Shape("M73,23 Q84,32 73,41"));
                    dc.Pop(); dc.Pop();
                }
                var receive = Math.Sin(Math.PI*SmoothStep(t,.68,1));
                Icon(HostSymbol.Device, 109, 12 - receive, 31, 39 + 2*receive);
                break;
            case HostSymbol.AdvancedSettings:
                Icon(HostSymbol.Terminal, 19, 25 - 6*SmoothStep(t,0,.7), 34, 30);
                Icon(Symbol, 53, 4 + 3*(1-ease), 54, 55);
                Icon(HostSymbol.Configuration, 112, 23 - 5*SmoothStep(t,.18,.95), 29, 31);
                break;
            case HostSymbol.Diagnostics:
                Icon(Symbol, 42, 3, 76, 56);
                var scan = 57 + 47*ease;
                dc.PushOpacity(Math.Sin(Math.PI*t));
                dc.DrawLine(ink.Outline, new Point(scan,17), new Point(scan,42));
                dc.Pop();
                break;
        }
    }
}
