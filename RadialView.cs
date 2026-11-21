using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace RadialMenu1;

/// <summary>
/// Само круглое меню: диск с секторами, плавно скользящая подсветка,
/// значки, название в центре, точки режимов. Используется и в главном окне,
/// и в окне настроек (как живой предпросмотр).
/// </summary>
public sealed class RadialView : Canvas
{
    // ---------- размеры ----------
    public const double ViewSize = 400;
    private const double C = ViewSize / 2;      // центр
    private const double OuterR = 150;          // внешний радиус диска
    internal const double GlassDiameter = OuterR * 2;
    private const double InnerR = 63;
    private const double CursorR = 173;
    private const double EscapeMargin = 35;
    private static readonly Rect SettingsArea = new(C - 47, C + 8, 94, 27);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);
    private const double IconR = (OuterR + InnerR) / 2;
    private const double IconBox = 45;

    // ---------- пружины ----------
    private const double SpringK = 380;         // появление и значки
    private const double SpringDamp = 22;
    private const double SlideK = 480;          // скольжение подсветки
    private const double SlideDamp = 32;

    // ---------- события для окна-хозяина ----------
    public event Action<Entry>? ItemInvoked;
    public event Action? CloseRequested;
    public event Action? SettingsRequested;
    public event Action? CloseFinished;
    public event Action<Mode, int, int>? EntryMoveRequested;

    /// <summary>Режим предпросмотра для окна настроек: меню всегда открыто.</summary>
    public bool IsPreview { get; set; }

    /// <summary>Какой режим показывать. Если не задано, берётся активный режим.</summary>
    public Func<Mode?>? ModeSource { get; set; }

    private Mode? _submenu;

    public bool IsOpen => _open;
    internal bool IsFullyOpen => _open && _menuT >= 0.96;

    public double SetMenuScale(int percent)
    {
        _displayScale = percent / 100.0;
        LayoutTransform = new ScaleTransform(_displayScale, _displayScale);
        return ViewSize * _displayScale;
    }

    // ---------- элементы ----------
    private readonly Canvas _menu = new() { Width = ViewSize, Height = ViewSize, IsHitTestVisible = false };
    private readonly ScaleTransform _menuScale = new(1, 1);
    private readonly Ellipse _disc = new();
    private readonly Ellipse _glassBackdrop = new() { Width = GlassDiameter, Height = GlassDiameter };
    private readonly Ellipse _glassSheen = new() { Width = OuterR * 2, Height = OuterR * 2 };
    private readonly Ellipse _cursorGlow = new() { Width = GlassDiameter, Height = GlassDiameter };
    private readonly RadialGradientBrush _cursorGlowBrush = new() { RadiusX = 0.30, RadiusY = 0.30 };
    private readonly RotateTransform _sheenRotation = new(0, 0.5, 0.5);
    private readonly System.Windows.Shapes.Path _highlight = new();
    private readonly RotateTransform _highlightRotation = new(0, C, C);
    private readonly System.Windows.Shapes.Path _dividers = new();
    private readonly Canvas _iconLayer = new();
    private readonly Ellipse _centerDisc = new();
    private readonly Grid _centerHost = new();
    private readonly TextBlock _centerText = new();
    private readonly Canvas _dots = new();
    private readonly Border _settingsPill = new() { CornerRadius = new CornerRadius(13.5), BorderThickness = new Thickness(1) };
    private readonly TextBlock _settingsLabel = new() { Text = "Настройки", FontSize = 11.5, FontWeight = FontWeights.SemiBold,
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private Brush? _pillNormal;
    private Brush? _pillActive;
    private Brush? _tileNormal;
    private Brush? _tileBorder;
    private readonly Image _dragIcon = new()
    {
        Width = 30, Height = 30, Opacity = 0.85, IsHitTestVisible = false, Visibility = Visibility.Collapsed
    };
    private readonly DropShadowEffect _discShadow = new() { BlurRadius = 26, ShadowDepth = 5, Direction = 270, Opacity = 0.50 };
    private readonly DropShadowEffect _highlightGlow = new() { BlurRadius = 18, ShadowDepth = 0, Opacity = 0.55 };
    private readonly DropShadowEffect _centerGlow = new() { BlurRadius = 16, ShadowDepth = 0, Opacity = 0.42 };

    private sealed class Slot
    {
        public Entry Entry = null!;
        public Grid Holder = null!;
        public Border Tile = null!;
        public Image Icon = null!;
        public ScaleTransform Scale = null!;
        public double T;            // 0 = в центре, 1 = на месте
        public double V;
        public double Delay;
        public double Hover;
        public double HoverTarget;
    }

    private readonly List<Slot> _slots = new();

    // ---------- состояние ----------
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _last;
    private double _changedAt;
    private bool _open;
    private bool _rendering;
    private bool _animationsEnabled;
    private bool _glass;
    private double _displayScale = 1;
    private Mode? _previewDragMode;
    private int _previewDragIndex;
    private Point _previewDragStart;
    private bool _previewDragging;

    private double _menuT, _menuV;

    private int _hover = -1;
    private bool _hoverCenter;
    private bool _hoverGear;
    private double _gearHover;

    private double _hlAngle, _hlVel, _hlTarget;
    private double _hlOpacity, _hlTargetOpacity;

    private double Now() => _clock.Elapsed.TotalSeconds;

    public RadialView()
    {
        Width = ViewSize;
        Height = ViewSize;
        Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)); // чтобы ловить мышь по всей площади

        _menu.RenderTransformOrigin = new Point(0.5, 0.5);
        _menu.RenderTransform = _menuScale;

        // диск
        _disc.Width = OuterR * 2;
        _disc.Height = OuterR * 2;
        _disc.StrokeThickness = 1;
        Canvas.SetLeft(_disc, C - OuterR);
        Canvas.SetTop(_disc, C - OuterR);
        Canvas.SetLeft(_glassBackdrop, C - OuterR);
        Canvas.SetTop(_glassBackdrop, C - OuterR);
        Canvas.SetLeft(_glassSheen, C - OuterR);
        Canvas.SetTop(_glassSheen, C - OuterR);
        Canvas.SetLeft(_cursorGlow, C - OuterR);
        Canvas.SetTop(_cursorGlow, C - OuterR);
        _cursorGlow.Fill = _cursorGlowBrush;

        // подсветка и разделители
        _highlight.Visibility = Visibility.Collapsed;
        _highlight.RenderTransform = _highlightRotation;
        _dividers.StrokeThickness = 1;
        _dividers.StrokeStartLineCap = _dividers.StrokeEndLineCap = PenLineCap.Round;

        // центральный круг
        _centerDisc.Width = InnerR * 2;
        _centerDisc.Height = InnerR * 2;
        _centerDisc.StrokeThickness = 1.5;

        Canvas.SetLeft(_centerDisc, C - InnerR);
        Canvas.SetTop(_centerDisc, C - InnerR);

        // название в центре
        _centerHost.Width = 110;
        _centerHost.Height = 30;
        Canvas.SetLeft(_centerHost, C - 55);
        Canvas.SetTop(_centerHost, C - 37);
        _centerText.TextAlignment = TextAlignment.Center;
        _centerText.TextWrapping = TextWrapping.NoWrap;
        _centerText.TextTrimming = TextTrimming.CharacterEllipsis;
        _centerText.MaxHeight = 50;
        _centerText.HorizontalAlignment = HorizontalAlignment.Center;
        _centerText.VerticalAlignment = VerticalAlignment.Center;
        _centerText.FontSize = 15;
        _centerText.FontWeight = FontWeights.SemiBold;
        _centerText.FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI");
        _centerHost.Children.Add(_centerText);

        // точки режимов
        _dots.Width = 100;
        _dots.Height = 6;
        Canvas.SetLeft(_dots, C - 50);
        Canvas.SetTop(_dots, C - 3);

        _menu.Children.Add(_glassBackdrop);
        _menu.Children.Add(_disc);
        _menu.Children.Add(_glassSheen);
        _menu.Children.Add(_highlight);
        _menu.Children.Add(_dividers);
        _menu.Children.Add(_centerDisc);
        _menu.Children.Add(_cursorGlow);
        _menu.Children.Add(_iconLayer);
        _menu.Children.Add(_centerHost);
        _menu.Children.Add(_dots);
        _settingsPill.Child = _settingsLabel;
        _settingsPill.Width = SettingsArea.Width;
        _settingsPill.Height = SettingsArea.Height;
        Canvas.SetLeft(_settingsPill, SettingsArea.X);
        Canvas.SetTop(_settingsPill, SettingsArea.Y);
        _menu.Children.Add(_settingsPill);

        Children.Add(_menu);
        Children.Add(_dragIcon);

        Loaded += (s, e) =>
        {
            ConfigStore.Changed -= OnConfigChanged;
            ConfigStore.Changed += OnConfigChanged;
            if (IsPreview) Open();
        };
        Unloaded += (s, e) =>
        {
            ConfigStore.Changed -= OnConfigChanged;
            StopRendering();
        };

        Rebuild(false);
    }

    private void OnConfigChanged() => Rebuild(false);

    // =====================================================================
    //  Какой режим сейчас показываем
    // =====================================================================
    private Mode CurrentMode
    {
        get
        {
            if (_submenu != null) return _submenu;
            var picked = ModeSource?.Invoke();
            if (picked != null) return picked;

            var cfg = ConfigStore.Current;
            int i = Math.Clamp(cfg.ActiveMode, 0, cfg.Modes.Count - 1);
            return cfg.Modes[i];
        }
    }

    // =====================================================================
    //  Открыть / закрыть / переключить режим
    // =====================================================================
    public void Open()
    {
        if (_submenu != null)
        {
            _submenu = null;
            Rebuild(false);
        }
        RefreshAppearance();
        MoveCursorGlow(new Point(C, C));
        if (!_open && _menuT > 0.05)
        {
            // меню ещё закрывалось: просто разворачиваем обратно
            _open = true;
            _changedAt = Now() - 10;
            StartRendering();
            return;
        }

        _open = true;
        _changedAt = Now();
        foreach (var s in _slots)
        {
            s.T = 0;
            s.V = 0;
            s.Hover = 0;
            s.HoverTarget = 0;
        }
        _menuT = 0;
        _menuV = 0;
        _hover = -1;
        _hoverCenter = false;
        _hoverGear = false;
        _gearHover = 0;
        _hlOpacity = 0;
        _hlTargetOpacity = 0;
        _hlVel = 0;
        UpdateCenterText();
        ApplyVisuals();
        StartRendering();
    }

    public void BeginClose()
    {
        if (!_open) return;

        _open = false;
        if (IsMouseCaptured) ReleaseMouseCapture();
        foreach (var s in _slots) s.HoverTarget = 0;
        _hover = -1;
        _hoverCenter = false;
        _hoverGear = false;
        _hlTargetOpacity = 0;
        UpdateCenterText();
        StartRendering();
    }

    public void SwitchMode(int direction)
    {
        var cfg = ConfigStore.Current;
        int n = cfg.Modes.Count;
        if (!_open || IsPreview) return;
        if (n < 2)
        {
            TryGoBack();
            return;
        }

        _submenu = null;
        cfg.ActiveMode = ((cfg.ActiveMode + direction) % n + n) % n;
        ConfigStore.Save();
        Rebuild(true);
    }

    public void SetMode(int index)
    {
        var cfg = ConfigStore.Current;
        if (!_open || IsPreview || index < 0 || index >= cfg.Modes.Count) return;
        if (index == cfg.ActiveMode)
        {
            TryGoBack();
            return;
        }

        _submenu = null;
        cfg.ActiveMode = index;
        ConfigStore.Save();
        Rebuild(true);
    }

    internal bool TryGoBack()
    {
        if (_submenu == null) return false;
        _submenu = null;
        Rebuild(true);
        return true;
    }

    // =====================================================================
    //  Построение значков, разделителей, точек
    // =====================================================================
    public void Rebuild(bool animate)
    {
        if (_previewDragMode != null) CancelPreviewDrag();
        var config = ConfigStore.Current;
        if (!config.SubmenusEnabled) _submenu = null;
        var mode = CurrentMode;
        var palette = MenuPalette.From(config);
        ApplyAppearance(palette);

        _iconLayer.Children.Clear();
        _slots.Clear();

        int i = 0;
        foreach (var entry in mode.Items)
        {
            if (entry.Submenu != null && !config.SubmenusEnabled) continue;

            var image = new Image
            {
                Source = IconService.Get(entry, palette.Text),
                Width = 30,
                Height = 30,
                Stretch = Stretch.Uniform
            };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);

            var scale = new ScaleTransform(1, 1);
            var holder = new Grid
            {
                Width = IconBox,
                Height = IconBox,
                Opacity = 0,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = scale
            };
            var tile = new Border { CornerRadius = new CornerRadius(14), Background = _tileNormal,
                BorderBrush = _tileBorder, BorderThickness = new Thickness(1) };
            holder.Children.Add(tile);
            holder.Children.Add(image);
            _iconLayer.Children.Add(holder);

            _slots.Add(new Slot
            {
                Entry = entry,
                Holder = holder,
                Tile = tile,
                Icon = image,
                Scale = scale,
                Delay = i * 0.03,
                T = (_open && !animate) ? 1 : 0
            });
            i++;
        }

        _hover = -1;
        _hoverCenter = false;
        _hoverGear = false;
        _hlAngle = 0;
        _hlVel = 0;
        _hlTarget = 0;
        _hlOpacity = 0;
        _hlTargetOpacity = 0;
        if (animate) _changedAt = Now();

        BuildDividers(_slots.Count);
        BuildDots(palette);
        UpdateCenterText();
        ApplyVisuals();
        if (_open) StartRendering();
    }

    internal void RefreshAppearance()
    {
        var palette = MenuPalette.From(ConfigStore.Current);
        ApplyAppearance(palette);
        BuildDots(palette);
        if (_rendering && !_animationsEnabled) StartRendering();
    }

    internal void SetGlassBackdrop(ImageSource? image)
    {
        _glassBackdrop.Fill = image == null ? null : new ImageBrush(image) { Stretch = Stretch.Fill };
    }

    private void ApplyAppearance(MenuPalette palette)
    {
        var config = ConfigStore.Current;
        double opacity = 1 - config.TransparencyPercent / 100.0;
        _animationsEnabled = config.AnimationsEnabled;
        _glass = config.Theme == MenuTheme.Glass;
        _glassBackdrop.Opacity = _glass && _animationsEnabled ? opacity : 0;
        if (IsPreview) SetGlassBackdrop(_glass && _animationsEnabled && opacity > 0 ? GlassBackdrop.Preview.Value : null);

        if (_glass)
        {
            _disc.Fill = palette.HasCustomBackground
                ? new LinearGradientBrush(WithAlpha(palette.Control, 124), WithAlpha(palette.Disc, 208), 125)
                : new LinearGradientBrush(Color.FromArgb(22, 226, 242, 255), Color.FromArgb(76, 12, 18, 32), 125);
            _disc.Fill.Opacity = opacity;
            _disc.Stroke = new LinearGradientBrush(Color.FromArgb(210, 243, 251, 255),
                Color.FromArgb(70, 127, 145, 170), 135);
            _centerDisc.Fill = palette.HasCustomBackground
                ? new LinearGradientBrush(WithAlpha(palette.Center, 242), WithAlpha(palette.Center, 255), 112)
                : new LinearGradientBrush(Color.FromArgb(150, 34, 43, 57), Color.FromArgb(215, 13, 18, 30), 112);
            _centerDisc.Fill.Opacity = opacity;
            var sheen = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0), EndPoint = new Point(1, 1), RelativeTransform = _sheenRotation,
                Opacity = opacity
            };
            sheen.GradientStops.Add(new GradientStop(Color.FromArgb(110, 255, 255, 255), 0));
            sheen.GradientStops.Add(new GradientStop(Color.FromArgb(16, 231, 243, 255), 0.20));
            sheen.GradientStops.Add(new GradientStop(Color.FromArgb(0, 203, 242, 255), 0.43));
            sheen.GradientStops.Add(new GradientStop(Color.FromArgb(0, 203, 242, 255), 0.78));
            sheen.GradientStops.Add(new GradientStop(Color.FromArgb(46, 129, 219, 255), 1));
            _glassSheen.Fill = sheen;
            _glassSheen.Visibility = Visibility.Visible;
        }
        else
        {
            _disc.Fill = new LinearGradientBrush(palette.Control, palette.Disc, 120) { Opacity = opacity };
            _disc.Stroke = new SolidColorBrush(palette.Rim);
            _centerDisc.Fill = new SolidColorBrush(palette.Center) { Opacity = opacity };
            _glassSheen.Visibility = Visibility.Collapsed;
            _glassSheen.Fill = null;
        }

        _dividers.Stroke = new SolidColorBrush(palette.Rim) { Opacity = 0.42 };
        Color accent = palette.Accent;
        _cursorGlowBrush.GradientStops.Clear();
        _cursorGlowBrush.GradientStops.Add(new GradientStop(Color.FromArgb(108, accent.R, accent.G, accent.B), 0));
        _cursorGlowBrush.GradientStops.Add(new GradientStop(Color.FromArgb(52, accent.R, accent.G, accent.B), 0.40));
        _cursorGlowBrush.GradientStops.Add(new GradientStop(Color.FromArgb(0, accent.R, accent.G, accent.B), 1));
        _highlight.Fill = new SolidColorBrush(_glass && !palette.HasCustomBackground ? Colors.White : palette.Highlight)
            { Opacity = opacity * (_glass ? 0.17 : 0.85) };
        _highlight.Stroke = new SolidColorBrush(_glass && !palette.HasCustomColors ? Colors.White : palette.Accent) { Opacity = 0.29 };
        _highlight.StrokeThickness = 1;
        _centerDisc.Stroke = new SolidColorBrush(_glass ? Colors.White : palette.Rim) { Opacity = 0.54 };
        bool textChanged = _centerText.Foreground is not SolidColorBrush text || text.Color != palette.Text;
        _centerText.Foreground = _settingsLabel.Foreground = new SolidColorBrush(palette.Text);
        _pillNormal = new SolidColorBrush(palette.Control) { Opacity = Math.Max(0.25, opacity * 0.82) };
        _pillActive = new SolidColorBrush(palette.Selected);
        _settingsPill.Background = _hoverGear ? _pillActive : _pillNormal;
        _settingsPill.BorderBrush = new SolidColorBrush(palette.Text) { Opacity = 0.26 };
        _tileNormal = new SolidColorBrush(_glass ? palette.Center : palette.Control) { Opacity = 0.59 * opacity };
        _tileBorder = new SolidColorBrush(palette.Text) { Opacity = 0.15 };
        foreach (var slot in _slots)
        {
            slot.Tile.Background = _tileNormal;
            slot.Tile.BorderBrush = _tileBorder;
            if (textChanged) slot.Icon.Source = IconService.Get(slot.Entry, palette.Text);
        }
        _highlightGlow.Color = _centerGlow.Color = palette.Accent;
        _disc.Effect = _animationsEnabled && !_glass && opacity > 0 ? _discShadow : null;
        _highlight.Effect = _animationsEnabled && !_glass ? _highlightGlow : null;
        _centerDisc.Effect = _animationsEnabled && !_glass ? _centerGlow : null;
    }

    private static Color WithAlpha(Color color, byte alpha) => Color.FromArgb(alpha, color.R, color.G, color.B);

    private void BuildDividers(int n)
    {
        _highlight.Data = n switch
        {
            0 => null,
            1 => FullRing(),
            _ => RingSector(-Math.PI / n + 0.035, Math.PI / n - 0.035)
        };
        if (n < 2)
        {
            _dividers.Data = null;
            return;
        }

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            double span = 2 * Math.PI / n;
            for (int i = 0; i < n; i++)
            {
                double a = (i + 0.5) * span;
                ctx.BeginFigure(P(InnerR + 11, a), false, false);
                ctx.LineTo(P(OuterR - 11, a), true, false);
            }
        }
        geometry.Freeze();
        _dividers.Data = geometry;
    }

    private void BuildDots(MenuPalette palette)
    {
        _dots.Children.Clear();

        var cfg = ConfigStore.Current;
        int active = cfg.Modes.IndexOf(CurrentMode);
        int count = Math.Min(cfg.Modes.Count, 7);
        if (count <= 1 || active < 0) return;
        active -= Math.Clamp(active - count / 2, 0, cfg.Modes.Count - count);

        const double dot = 5;
        const double gap = 6;
        double total = count * dot + (count - 1) * gap;
        for (int i = 0; i < count; i++)
        {
            var d = new Ellipse
            {
                Width = dot,
                Height = dot,
                Fill = new SolidColorBrush(i == active ? palette.Text : palette.Rim)
            };
            Canvas.SetLeft(d, 50 - total / 2 + i * (dot + gap));
            Canvas.SetTop(d, 0);
            _dots.Children.Add(d);
        }
    }

    private void UpdateCenterText()
    {
        _settingsLabel.Text = _submenu == null ? "Настройки" : "Назад";
        _centerText.Text = (_hover >= 0 && _hover < _slots.Count)
            ? _slots[_hover].Entry.Name
            : CurrentMode.Name;
    }

    // =====================================================================
    //  Мышь
    // =====================================================================
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (!IsPreview || !_open || _slots.Count < 2) return;

        int index = PreviewSectorAt(e.GetPosition(this));
        if (index < 0 || !CaptureMouse()) return;

        _previewDragMode = CurrentMode;
        _previewDragIndex = index;
        _previewDragStart = e.GetPosition(Window.GetWindow(this));
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var point = e.GetPosition(this);
        if (_previewDragMode != null)
        {
            UpdatePreviewDrag(e, point);
            MoveCursorGlow(point);
            return;
        }

        if (_open && !IsPreview && IsMouseCaptured)
        {
            var center = new Point(C, C);
            var offset = point - center;
            if (ShouldEscape(offset))
            {
                e.Handled = true;
                CloseRequested?.Invoke();
                return;
            }
            if (offset.LengthSquared > CursorR * CursorR)
            {
                offset.Normalize();
                // Оставляем запас для округления координат до целых пикселей.
                point = center + offset * (CursorR - 1 / _displayScale);
                var screenPoint = PointToScreen(point);
                SetCursorPos((int)Math.Round(screenPoint.X), (int)Math.Round(screenPoint.Y));
            }
        }

        UpdatePointer(point);
        MoveCursorGlow(point);
    }

    private bool ShouldEscape(Vector offset)
    {
        double escapeRadius = CursorR + Math.Max(EscapeMargin, 14 / _displayScale);
        return offset.LengthSquared > escapeRadius * escapeRadius;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (e.OriginalSource != this) return;
        if (IsPreview) CancelPreviewDrag();
        else if (_open) CloseRequested?.Invoke();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        SetPointer(-1, false, false);
        _cursorGlow.Visibility = Visibility.Collapsed;
    }

    private void MoveCursorGlow(Point point)
    {
        var offset = point - new Point(C, C);
        if (offset.Length > OuterR)
        {
            offset.Normalize();
            offset *= OuterR;
        }
        Point origin = new(0.5 + offset.X / GlassDiameter, 0.5 + offset.Y / GlassDiameter);
        _cursorGlowBrush.Center = _cursorGlowBrush.GradientOrigin = origin;
        _cursorGlow.Visibility = Visibility.Visible;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_open) return;

        if (IsPreview)
        {
            if (_previewDragMode == null) return;
            FinishPreviewDrag(e.GetPosition(this));
            e.Handled = true;
            return;
        }

        UpdatePointer(e.GetPosition(this));
        if (InvokeSector()) return;
        if ((_hoverCenter || _hoverGear) && TryGoBack()) return;
        if (_hoverCenter) SwitchMode(1);
        else if (_hoverGear) SettingsRequested?.Invoke();
        else CloseRequested?.Invoke();
    }

    internal void SelectAtRelease(Point point)
    {
        if (!_open || IsPreview) return;
        if (!ShouldEscape(point - new Point(C, C)))
        {
            UpdatePointer(point);
            if (InvokeSector()) return;
        }
        CloseRequested?.Invoke();
    }

    private bool InvokeSector()
    {
        if (_hover < 0 || _hover >= _slots.Count) return false;
        var entry = _slots[_hover].Entry;
        if (entry.Submenu is { } submenu)
        {
            if (!ConfigStore.Current.SubmenusEnabled) return false;
            _submenu = submenu;
            Rebuild(true);
        }
        else ItemInvoked?.Invoke(entry);
        return true;
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        if (!_open || IsPreview) return;
        e.Handled = true;
        SettingsRequested?.Invoke();
    }

    private void UpdatePreviewDrag(MouseEventArgs e, Point point)
    {
        if (!_previewDragging)
        {
            var offset = e.GetPosition(Window.GetWindow(this)) - _previewDragStart;
            if (Math.Abs(offset.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(offset.Y) < SystemParameters.MinimumVerticalDragDistance) return;

            _previewDragging = true;
            var entry = _slots[_previewDragIndex].Entry;
            _dragIcon.Source = IconService.Get(entry, MenuPalette.From(ConfigStore.Current).Text);
        }

        int target = PreviewSectorAt(point);
        _dragIcon.Visibility = target >= 0 ? Visibility.Visible : Visibility.Collapsed;
        Canvas.SetLeft(_dragIcon, point.X - _dragIcon.Width / 2);
        Canvas.SetTop(_dragIcon, point.Y - _dragIcon.Height / 2);
        SetPointer(target, false, false);
        e.Handled = true;
    }

    private void CancelPreviewDrag()
    {
        if (_previewDragMode == null) return;
        _previewDragMode = null;
        _previewDragging = false;
        _dragIcon.Visibility = Visibility.Collapsed;
        _dragIcon.Source = null;
        if (IsMouseCaptured) ReleaseMouseCapture();
        SetPointer(-1, false, false);
    }

    private void FinishPreviewDrag(Point point)
    {
        if (_previewDragMode is not { } mode) return;
        int from = _previewDragIndex;
        int target = _previewDragging ? PreviewSectorAt(point) : -1;
        CancelPreviewDrag();
        if (target >= 0 && target != from)
        {
            int sourceIndex = mode.Items.IndexOf(_slots[from].Entry);
            int targetIndex = mode.Items.IndexOf(_slots[target].Entry);
            if (sourceIndex >= 0 && targetIndex >= 0)
                EntryMoveRequested?.Invoke(mode, sourceIndex, targetIndex);
        }
    }

    private int PreviewSectorAt(Point point)
    {
        Vector offset = point - new Point(C, C);
        return offset.LengthSquared <= InnerR * InnerR || offset.LengthSquared > OuterR * OuterR
            ? -1 : SectorAt(offset.X, offset.Y);
    }

    private int SectorAt(double dx, double dy)
    {
        if (_slots.Count == 0) return -1;
        double span = 2 * Math.PI / _slots.Count;
        double angle = Math.Atan2(dx, -dy); // 0 = вверх, по часовой
        if (angle < 0) angle += 2 * Math.PI;
        return (int)Math.Floor((angle + span / 2) / span) % _slots.Count;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        SwitchMode(e.Delta > 0 ? -1 : 1);
    }

    private void UpdatePointer(Point p)
    {
        double dx = p.X - C;
        double dy = p.Y - C;
        double dist = Math.Sqrt(dx * dx + dy * dy);

        int idx = -1;
        bool center = false;
        bool gear = false;

        if (SettingsArea.Contains(p))
        {
            gear = true;
        }
        else if (dist <= InnerR)
        {
            center = true;
        }
        else if (!IsPreview || dist <= OuterR)
        {
            idx = SectorAt(dx, dy);
        }

        SetPointer(idx, center, gear);
    }

    private void SetPointer(int idx, bool center, bool gear)
    {
        if (!_open) return;
        if (idx == _hover && center == _hoverCenter && gear == _hoverGear) return;

        if (idx != _hover)
        {
            if (_hover >= 0 && _hover < _slots.Count) _slots[_hover].HoverTarget = 0;
            _hover = idx;

            if (idx >= 0)
            {
                _slots[idx].HoverTarget = 1;

                // подсветка едет по кратчайшему пути
                double span = 2 * Math.PI / _slots.Count;
                _hlTarget += Math.IEEERemainder(idx * span - _hlTarget, 2 * Math.PI);
                if (_hlOpacity < 0.05)
                {
                    _hlAngle = _hlTarget;   // подсветка была невидима: появляется сразу на месте
                    _hlVel = 0;
                }
                _hlTargetOpacity = 1;
            }
            else
            {
                _hlTargetOpacity = 0;
            }

            UpdateCenterText();
        }

        _hoverCenter = center;
        _hoverGear = gear;
        Cursor = (idx >= 0 || center || gear) ? Cursors.Hand : Cursors.Arrow;
        StartRendering();
    }

    // =====================================================================
    //  Анимация: пружины считаем каждый кадр
    // =====================================================================
    private void StartRendering()
    {
        if (!_animationsEnabled)
        {
            StopRendering();
            _menuT = _open ? 1 : 0;
            _menuV = _hlVel = 0;
            _hlAngle = _hlTarget;
            _hlOpacity = _hlTargetOpacity;
            _gearHover = _hoverGear ? 1 : 0;
            foreach (var slot in _slots)
            {
                slot.T = _menuT;
                slot.V = 0;
                slot.Hover = slot.HoverTarget;
            }
            ApplyVisuals();
            if (!_open) CloseFinished?.Invoke();
            return;
        }

        if (_rendering) return;
        _rendering = true;
        _last = Now();
        CompositionTarget.Rendering += OnRendering;
    }

    private void StopRendering()
    {
        if (!_rendering) return;
        CompositionTarget.Rendering -= OnRendering;
        _rendering = false;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        double now = Now();
        double dt = Math.Min(now - _last, 1.0 / 30.0);
        _last = now;
        if (dt <= 0) return;

        bool busy = false;

        // общее появление меню
        double menuTarget = _open ? 1 : 0;
        _menuV += (SpringK * (menuTarget - _menuT) - SpringDamp * _menuV) * dt;
        _menuT += _menuV * dt;
        if (Math.Abs(menuTarget - _menuT) > 0.0015 || Math.Abs(_menuV) > 0.01) busy = true;

        // значки
        foreach (var s in _slots)
        {
            double target = (_open && now - _changedAt >= s.Delay) ? 1 : 0;
            s.V += (SpringK * (target - s.T) - SpringDamp * s.V) * dt;
            s.T += s.V * dt;
            s.Hover += (s.HoverTarget - s.Hover) * Math.Min(1, dt * 16);

            if (Math.Abs(target - s.T) > 0.0015 ||
                Math.Abs(s.V) > 0.01 ||
                Math.Abs(s.HoverTarget - s.Hover) > 0.002)
            {
                busy = true;
            }
            if (_open && target == 0) busy = true;   // ждём своей очереди
        }

        // скользящая подсветка
        _hlVel += (SlideK * (_hlTarget - _hlAngle) - SlideDamp * _hlVel) * dt;
        _hlAngle += _hlVel * dt;
        _hlOpacity += (_hlTargetOpacity - _hlOpacity) * Math.Min(1, dt * 18);
        if (Math.Abs(_hlTarget - _hlAngle) > 0.001 ||
            Math.Abs(_hlVel) > 0.01 ||
            Math.Abs(_hlTargetOpacity - _hlOpacity) > 0.01)
        {
            busy = true;
        }

        // шестерёнка
        double gearTarget = _hoverGear ? 1 : 0;
        _gearHover += (gearTarget - _gearHover) * Math.Min(1, dt * 16);
        if (Math.Abs(gearTarget - _gearHover) > 0.01) busy = true;

        ApplyVisuals();

        if (!busy)
        {
            StopRendering();
            if (!_open) CloseFinished?.Invoke();
        }
    }

    private void ApplyVisuals()
    {
        int n = _slots.Count;

        // появление: лёгкое увеличение и проявление
        double m = Math.Clamp(_menuT, 0, 1.2);
        double sc = 0.84 + 0.16 * m;
        double stretch = _glass && _animationsEnabled ? Math.Clamp(_menuV * 0.006, -0.045, 0.045) : 0;
        _menuScale.ScaleX = sc + stretch;
        _menuScale.ScaleY = sc - stretch;
        double alpha = Math.Clamp(_menuT * 1.8, 0, 1);
        _menu.Opacity = alpha;
        _settingsPill.Background = _gearHover > 0.3 ? _pillActive : _pillNormal;
        if (_glass)
            _sheenRotation.Angle = _animationsEnabled
                ? (1 - m) * -35 + Math.Sin(_hlAngle) * 24 * _hlOpacity : 0;

        // значки
        double span = n > 0 ? 2 * Math.PI / n : 0;
        for (int i = 0; i < n; i++)
        {
            var s = _slots[i];
            double a = i * span;
            double dist = IconR * (0.55 + 0.45 * Math.Max(0, s.T));
            double x = C + Math.Sin(a) * dist;
            double y = C - Math.Cos(a) * dist;

            double k = (0.5 + 0.5 * Math.Clamp(s.T, 0, 1)) * (1 + 0.14 * s.Hover);
            s.Scale.ScaleX = k;
            s.Scale.ScaleY = k;
            s.Holder.Opacity = Math.Clamp((s.T - 0.1) / 0.5, 0, 1);
            Canvas.SetLeft(s.Holder, x - IconBox / 2);
            Canvas.SetTop(s.Holder, y - IconBox / 2);
        }

        // подсветка
        if (n == 0 || _hlOpacity < 0.01)
        {
            _highlight.Visibility = Visibility.Collapsed;
        }
        else
        {
            _highlightRotation.Angle = _hlAngle * 180 / Math.PI;
            _highlight.Opacity = _hlOpacity;
            _highlight.Visibility = Visibility.Visible;
        }
    }

    // =====================================================================
    //  Геометрия
    // =====================================================================
    private static Point P(double r, double a) =>
        new(C + r * Math.Sin(a), C - r * Math.Cos(a));

    private static Geometry RingSector(double a1, double a2)
    {
        const double inner = InnerR + 4;
        const double outer = OuterR - 4;
        double sweep = a2 - a1;
        bool large = sweep > Math.PI;

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(P(outer, a1), true, true);
            ctx.ArcTo(P(outer, a2), new Size(outer, outer), 0, large, SweepDirection.Clockwise, true, false);
            ctx.LineTo(P(inner, a2), true, false);
            ctx.ArcTo(P(inner, a1), new Size(inner, inner), 0, large, SweepDirection.Counterclockwise, true, false);
        }
        geometry.Freeze();
        return geometry;
    }

    private static Geometry FullRing()
    {
        var ring = new CombinedGeometry(
            GeometryCombineMode.Exclude,
            new EllipseGeometry(new Point(C, C), OuterR, OuterR),
            new EllipseGeometry(new Point(C, C), InnerR, InnerR));
        ring.Freeze();
        return ring;
    }
}
