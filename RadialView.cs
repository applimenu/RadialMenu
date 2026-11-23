using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace RadialMenu1;

/// <summary>
/// Круговое меню для главного окна и живого предпросмотра в настройках.
/// </summary>
public sealed partial class RadialView : Canvas
{
    // ---------- размеры ----------
    public const double ViewSize = 400;
    private const double C = ViewSize / 2;      // центр
    private const double OuterR = 140;          // общий внешний радиус колеса
    internal const double GlassDiameter = OuterR * 2;
    private const double InnerR = 63;
    private const double CursorR = 173;
    private const double EscapeMargin = 35;
    private static readonly Entry SettingsEntry = new("Настройки кругового меню", "", "\uE713");
    private static readonly Lazy<ImageSource> SettingsIcon = new(() =>
    {
        var uri = new Uri("pack://application:,,,/RadialMenu1;component/app.ico");
        using var stream = Application.GetResourceStream(uri).Stream;
        var decoder = new IconBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var icon = decoder.Frames.MaxBy(frame => frame.PixelWidth)!;
        icon.Freeze();
        return icon;
    });

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);
    private const double IconR = (OuterR + InnerR) / 2;
    private const double IconBox = 45;
    private const double IconHoverScale = 0.14;

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
    private readonly Mode _emptyMode = new("Без приложений");

    public bool IsOpen => _open;

    public double SetMenuScale(int percent)
    {
        _displayScale = percent / 100.0;
        _statusRing.SetScale(_displayScale);
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
    private readonly Grid _centerHost = new() { ClipToBounds = true };
    private readonly TextBlock _centerText = new() { IsHitTestVisible = false };
    private readonly Canvas _dots = new();
    private Brush? _tileNormal;
    private Brush? _tileBorder;
    private readonly Image _dragIcon = new()
    {
        Width = 30, Height = 30, Opacity = 0.85, IsHitTestVisible = false, Visibility = Visibility.Collapsed
    };
    private readonly RadialEffects _effects = new() { Width = ViewSize, Height = ViewSize };

    private sealed class Slot
    {
        public Entry Entry = null!;
        public Grid Holder = null!;
        public Border? Tile;
        public Image Icon = null!;
        public ScaleTransform Scale = null!;
        public readonly TranslateTransform Offset = new();
        public StyledSlot? Style;
        public double T;            // 0 = в центре, 1 = на месте
        public double IconT;
        public double Delay;
        public double Phase, FromPhase;
        public double Hover;
        public double HoverTarget;
    }

    private readonly List<Slot> _slots = new();

    // ---------- состояние ----------
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _last;
    private double _transitionStart = double.NaN;
    private readonly Canvas _outgoing = new() { IsHitTestVisible = false };
    private double _fadeStart;
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
        _centerHost.Height = 44;
        Canvas.SetTop(_centerHost, C - _centerHost.Height / 2);
        _centerText.TextAlignment = TextAlignment.Center;
        _centerText.TextWrapping = TextWrapping.Wrap;
        _centerText.TextTrimming = TextTrimming.CharacterEllipsis;
        _centerText.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        _centerText.HorizontalAlignment = HorizontalAlignment.Center;
        _centerText.VerticalAlignment = VerticalAlignment.Center;
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
        InitializeStyledLayers();
        _menu.Children.Add(_outgoing);
        _menu.Children.Add(_iconLayer);
        _menu.Children.Add(_centerHost);
        _menu.Children.Add(_dots);
        _menu.Children.Add(_statusRing);

        Children.Add(_menu);
        Children.Add(_effects);
        Children.Add(_dragIcon);

        Loaded += (s, e) =>
        {
            ConfigStore.Changed -= OnConfigChanged;
            ConfigStore.Changed += OnConfigChanged;
            ChameleonMonitor.PaletteChanged += OnChameleonChanged;
            if (IsPreview) Open();
            else SynchronizeStatusRing();
        };
        Unloaded += (s, e) =>
        {
            ConfigStore.Changed -= OnConfigChanged;
            ChameleonMonitor.PaletteChanged -= OnChameleonChanged;
            StopWatchingStatus();
            StopRendering();
        };
        IsVisibleChanged += (_, _) =>
        {
            SynchronizeStatusRing();
            if (IsPreview && _open) StartRendering();
        };

        Rebuild(false);
    }

    private void OnConfigChanged() => Rebuild(IsPreview && _style != ConfigStore.Current.MenuStyle);
    private void OnChameleonChanged()
    {
        if (_open && ConfigStore.Current.Theme == MenuTheme.Chameleon) RefreshAppearance();
    }

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
            if (cfg.Modes.Count == 0) return _emptyMode;
            int i = cfg.AvailableModeIndex(cfg.ActiveMode, 1);
            if (i < 0) i = Math.Clamp(cfg.ActiveMode, 0, cfg.Modes.Count - 1);
            return cfg.Modes[i];
        }
    }

    // =====================================================================
    //  Открыть / закрыть / переключить режим
    // =====================================================================
    public void Open()
    {
        if (_submenu != null || _style != ConfigStore.Current.MenuStyle)
        {
            _submenu = null;
            Rebuild(false);
        }
        RefreshAppearance();
        MoveCursorGlow(new Point(C, C));
        if (!_open && _rendering && (_menuT > 0 || _slots.Any(s => s.Phase > 0)))
        {
            // меню ещё закрывалось: просто разворачиваем обратно
            _open = true;
            BeginSlotTransition();
            SynchronizeStatusRing();
            StartRendering();
            return;
        }

        _open = true;
        _effects.Reset();
        foreach (var s in _slots)
        {
            s.Phase = s.T = s.IconT = 0;
            s.Hover = 0;
            s.HoverTarget = 0;
        }
        BeginSlotTransition();
        _menuT = 0;
        _menuV = 0;
        _hover = -1;
        _hoverCenter = false;
        SetStatusHover(null);
        _hlOpacity = 0;
        _hlTargetOpacity = 0;
        _hlVel = 0;
        UpdateCenterText();
        SynchronizeStatusRing();
        ApplyVisuals();
        StartRendering();
    }

    public void BeginClose()
    {
        if (!_open) return;

        _open = false;
        BeginSlotTransition();
        if (IsMouseCaptured) ReleaseMouseCapture();
        foreach (var s in _slots) s.HoverTarget = 0;
        _hover = -1;
        _hoverCenter = false;
        SetStatusHover(null);
        _hlTargetOpacity = 0;
        UpdateCenterText();
        SynchronizeStatusRing();
        StartRendering();
    }

    public void SwitchMode(int direction)
    {
        var cfg = ConfigStore.Current;
        int n = cfg.Modes.Count;
        if (!_open || IsPreview) return;
        int current = cfg.AvailableModeIndex(cfg.ActiveMode, 1);
        if (current < 0) return;
        direction = Math.Sign(direction);
        int next = cfg.AvailableModeIndex((current + direction + n) % n, direction);
        if (next == current)
        {
            TryGoBack();
            return;
        }

        _submenu = null;
        cfg.ActiveMode = next;
        ConfigStore.Save();
        Rebuild(true);
    }

    public void SetMode(int index)
    {
        var cfg = ConfigStore.Current;
        if (!_open || IsPreview || !cfg.IsModeAvailable(index)) return;
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
        _outgoing.Children.Clear();
        if (animate && _open && IsVisible && config.AnimationsEnabled)
        {
            // Retain the already rendered tiles for a short crossfade on a mode/style change.
            foreach (var layer in new[] { _shapeLayer, _iconLayer })
            {
                var visuals = layer.Children.Cast<UIElement>().ToArray();
                layer.Children.Clear();
                foreach (var visual in visuals) _outgoing.Children.Add(visual);
            }
            _outgoing.Opacity = 1;
            _fadeStart = double.NaN;
        }
        if (!config.SubmenusEnabled) _submenu = null;
        _style = config.MenuStyle;
        bool classic = _style == MenuStyle.Classic;
        var mode = CurrentMode;
        var palette = MenuPalette.From(config);
        ApplyLayoutChrome();
        _iconLayer.Children.Clear();
        _shapeLayer.Children.Clear();
        _slots.Clear();
        ApplyAppearance(palette);

        const double iconSize = 30;
        double initialProgress = _open && !animate ? 1 : 0;
        var entries = mode.VisibleItems(config.SubmenusEnabled).ToList();
        if (entries.Count > 0) entries.Insert(0, SettingsEntry);
        foreach (var entry in entries)
        {
            var image = new Image
            {
                Source = SlotIcon(entry, palette.Text),
                Width = iconSize,
                Height = iconSize,
                Stretch = Stretch.Uniform
            };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);

            var scale = new ScaleTransform(1, 1);
            var holder = new Grid
            {
                Width = IconBox,
                Height = IconBox,
                Opacity = 0,
                RenderTransformOrigin = new Point(0.5, 0.5)
            };
            Border? tile = null;
            if (classic)
            {
                tile = new Border { CornerRadius = new CornerRadius(14), Background = _tileNormal,
                    BorderBrush = _tileBorder, BorderThickness = new Thickness(1) };
                holder.Children.Add(tile);
            }
            holder.Children.Add(image);
            _iconLayer.Children.Add(holder);

            var slot = new Slot
            {
                Entry = entry,
                Holder = holder,
                Tile = tile,
                Icon = image,
                Scale = scale,
                Phase = initialProgress,
                T = initialProgress,
                IconT = initialProgress
            };
            var transforms = new TransformGroup();
            transforms.Children.Add(scale);
            transforms.Children.Add(slot.Offset);
            holder.RenderTransform = transforms;
            SetLeft(holder, C - IconBox / 2);
            SetTop(holder, C - IconBox / 2);
            _slots.Add(slot);
        }

        _hover = -1;
        _hoverCenter = false;
        SetStatusHover(null);
        _hlAngle = 0;
        _hlVel = 0;
        _hlTarget = 0;
        _hlOpacity = 0;
        _hlTargetOpacity = 0;
        BeginSlotTransition();

        BuildDividers(classic ? _slots.Count : 0);
        if (!classic) BuildStyledSlots();
        _effects.Configure(_style, palette.Accent, _slots.Count);
        UpdateEmptyState(palette);
        BuildDots(palette);
        UpdateCenterText();
        SynchronizeStatusRing();
        ApplyVisuals();
        if (_open) StartRendering();
    }

    internal void RefreshAppearance()
    {
        bool previousMotion = _animationsEnabled;
        var palette = MenuPalette.From(ConfigStore.Current);
        ApplyAppearance(palette);
        BuildDots(palette);
        SynchronizeStatusRing();
        if (IsPreview && _slots.Count == 0) UpdateEmptyState(palette);
        if ((_open && previousMotion != _animationsEnabled) || (_rendering && !_animationsEnabled)) StartRendering();
        else ApplyVisuals();
    }

    internal void SetGlassBackdrop(ImageSource? image)
    {
        _glassBackdrop.Fill = image == null ? null : new ImageBrush(image) { Stretch = Stretch.Fill };
        _styledGlassImage.ImageSource = image;
    }

    private void ApplyAppearance(MenuPalette palette)
    {
        var config = ConfigStore.Current;
        _statusRing.ApplyPalette(palette);
        double opacity = 1 - config.TransparencyPercent / 100.0;
        _animationsEnabled = config.AnimationsEnabled;
        _effects.Enabled = _animationsEnabled && config.SelectionEffectsEnabled;
        _effects.Configure(_style, palette.Accent, _slots.Count);
        _glass = config.Theme == MenuTheme.Glass;
        _glassBackdrop.Opacity = _glass ? opacity : 0;
        if (IsPreview && _glass && opacity > 0 && _styledGlassImage.ImageSource == null)
            SetGlassBackdrop(GlassBackdrop.Preview.Value);

        if (_glass)
        {
            var sheen = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0), EndPoint = new Point(1, 1), RelativeTransform = _sheenRotation,
                Opacity = opacity
            };
            sheen.GradientStops.Add(new GradientStop(Color.FromArgb(69, 255, 255, 255), 0));
            sheen.GradientStops.Add(new GradientStop(Color.FromArgb(16, 231, 243, 255), 0.20));
            sheen.GradientStops.Add(new GradientStop(Color.FromArgb(0, 203, 242, 255), 0.43));
            sheen.GradientStops.Add(new GradientStop(Color.FromArgb(0, 203, 242, 255), 0.78));
            sheen.GradientStops.Add(new GradientStop(Color.FromArgb(46, 129, 219, 255), 1));
            _glassSheen.Fill = sheen;
            _glassSheen.Visibility = _style == MenuStyle.Classic ? Visibility.Visible : Visibility.Collapsed;
        }
        else
        {
            _glassSheen.Visibility = Visibility.Collapsed;
            _glassSheen.Fill = null;
        }

        _disc.Fill = palette.WheelSurface(opacity, glass: _glass);
        _disc.Stroke = palette.WheelEdge();
        _centerDisc.Fill = palette.WheelSurface(opacity, center: true, glass: _glass);
        _centerDisc.Stroke = palette.WheelEdge(center: true);
        _dividers.Stroke = palette.WheelEdge();
        Color accent = palette.Accent;
        _cursorGlowBrush.GradientStops.Clear();
        _cursorGlowBrush.GradientStops.Add(new GradientStop(WithAlpha(accent, (byte)(32 * opacity)), 0));
        _cursorGlowBrush.GradientStops.Add(new GradientStop(WithAlpha(accent, (byte)(15 * opacity)), 0.40));
        _cursorGlowBrush.GradientStops.Add(new GradientStop(Color.FromArgb(0, accent.R, accent.G, accent.B), 1));
        _highlight.Fill = palette.WheelHover(opacity);
        _highlight.Stroke = new SolidColorBrush(WithAlpha(accent, 175));
        _highlight.StrokeThickness = 1.2;
        _highlight.StrokeLineJoin = PenLineJoin.Round;
        bool textChanged = _centerText.Foreground is not SolidColorBrush text || text.Color != palette.Text;
        _centerText.Foreground = new SolidColorBrush(palette.Text);
        _tileNormal = palette.WheelSurface(.70 * opacity, center: true, glass: _glass);
        _tileBorder = palette.WheelEdge();
        foreach (var slot in _slots)
        {
            if (slot.Tile is { } tile)
            {
                tile.Background = _tileNormal;
                tile.BorderBrush = _tileBorder;
            }
            if (textChanged) slot.Icon.Source = SlotIcon(slot.Entry, palette.Text);
        }
        if (_style != MenuStyle.Classic) ApplyStyledAppearance(palette, opacity);
        if (!_animationsEnabled) _cursorGlow.Visibility = Visibility.Collapsed;
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
        var available = cfg.Modes.Where(mode => mode.VisibleItems(cfg.SubmenusEnabled).Any()).ToList();
        int active = available.IndexOf(CurrentMode);
        int count = Math.Min(available.Count, _style == MenuStyle.Honeycomb ? 3 : 7);
        if (count <= 1 || active < 0) return;
        active -= Math.Clamp(active - count / 2, 0, available.Count - count);

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
        var entry = _hover >= 0 && _hover < _slots.Count ? _slots[_hover].Entry : null;
        bool settings = ReferenceEquals(entry, SettingsEntry);
        double size = _style != MenuStyle.Honeycomb ? 13 : _centerHost.Width < 65 ? 9.5 : 12;
        _centerText.FontSize = settings ? Math.Min(size, 10.5) : size;
        _centerText.LineHeight = _centerText.FontSize * 1.23;
        string caption = _statusHover is { } metric ? _statusRing.Caption(metric) :
            settings ? "Настройки\nкругового\nменю" : entry?.Name ?? CurrentMode.Name;
        if (_centerText.Text != caption) _centerText.Text = caption;
        bool neutral = entry == null && !_statusHover.HasValue;
        _centerText.MaxHeight = _centerText.LineHeight * (neutral ? 2 : 3);
        _dots.Visibility = neutral ? Visibility.Visible : Visibility.Hidden;
    }

    private static ImageSource SlotIcon(Entry entry, Color color) =>
        ReferenceEquals(entry, SettingsEntry) ? SettingsIcon.Value : IconService.Get(entry, color);

    // =====================================================================
    //  Мышь
    // =====================================================================
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (!IsPreview || !_open || _slots.Count < 3) return;

        int index = PreviewSectorAt(e.GetPosition(this));
        if (index <= 0 || !CaptureMouse()) return;

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
            double pointerRadius = PointerRadius;
            if (offset.LengthSquared > pointerRadius * pointerRadius)
            {
                offset.Normalize();
                // Оставляем запас для округления координат до целых пикселей.
                point = center + offset * (pointerRadius - 1 / _displayScale);
                var screenPoint = PointToScreen(point);
                SetCursorPos((int)Math.Round(screenPoint.X), (int)Math.Round(screenPoint.Y));
            }
        }

        UpdatePointer(point);
        MoveCursorGlow(point);
    }

    private bool ShouldEscape(Vector offset)
    {
        double escapeRadius = PointerRadius + Math.Max(EscapeMargin, 14 / _displayScale);
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
        SetStatusHover(null);
        SetPointer(-1, false);
        _cursorGlow.Visibility = Visibility.Collapsed;
    }

    private void MoveCursorGlow(Point point)
    {
        if (_style != MenuStyle.Classic || !_animationsEnabled) return;
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
        var point = e.GetPosition(this);
        if (!_previewDragging && ClickStatusAt(point))
        {
            if (_previewDragMode != null) CancelPreviewDrag();
            e.Handled = true;
            return;
        }

        if (IsPreview)
        {
            if (!_previewDragging && PreviewSectorAt(point) is >= 0 and var index)
            {
                _effects.Burst(SlotCenter(index), Now());
                StartRendering();
            }
            if (_previewDragMode != null) FinishPreviewDrag(point);
            e.Handled = true;
            return;
        }

        UpdatePointer(point);
        if (InvokeSector()) return;
        if (_hoverCenter && TryGoBack()) return;
        if (_hoverCenter) SwitchMode(1);
        else CloseRequested?.Invoke();
    }

    internal void SelectAtRelease(Point point)
    {
        if (!_open || IsPreview) return;
        if (!ShouldEscape(point - new Point(C, C)))
        {
            if (!ClickStatusAt(point))
            {
                UpdatePointer(point);
                if (InvokeSector()) return;
            }
        }
        CloseRequested?.Invoke();
    }

    private bool InvokeSector()
    {
        if (_hover < 0 || _hover >= _slots.Count) return false;
        var entry = _slots[_hover].Entry;
        _effects.Burst(SlotCenter(_hover), Now());
        if (ReferenceEquals(entry, SettingsEntry))
        {
            SettingsRequested?.Invoke();
        }
        else if (entry.Submenu is { } submenu)
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
        SetStatusHover(null);
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
        _dragIcon.Visibility = target > 0 ? Visibility.Visible : Visibility.Collapsed;
        Canvas.SetLeft(_dragIcon, point.X - _dragIcon.Width / 2);
        Canvas.SetTop(_dragIcon, point.Y - _dragIcon.Height / 2);
        SetPointer(target > 0 ? target : -1, false, target > 0 ? point : null);
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
        SetPointer(-1, false);
    }

    private void FinishPreviewDrag(Point point)
    {
        if (_previewDragMode is not { } mode) return;
        int from = _previewDragIndex;
        int target = _previewDragging ? PreviewSectorAt(point) : -1;
        CancelPreviewDrag();
        if (target > 0 && target != from)
        {
            int sourceIndex = mode.Items.IndexOf(_slots[from].Entry);
            int targetIndex = mode.Items.IndexOf(_slots[target].Entry);
            if (sourceIndex >= 0 && targetIndex >= 0)
                EntryMoveRequested?.Invoke(mode, sourceIndex, targetIndex);
        }
    }

    private int PreviewSectorAt(Point point)
    {
        if (_style != MenuStyle.Classic) return StyledSlotAt(point);
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

    private Point SlotCenter(int index) => _slots[index].Style?.Center ??
        P(IconR, 2 * Math.PI * index / _slots.Count);

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (_open && _statusRing.At(e.GetPosition(this)) == StatusArc.Timer)
        {
            StatusRingMonitor.Instance.ChangeDuration(e.Delta);
            UpdateStatusRing();
            e.Handled = true;
            return;
        }
        SwitchMode(e.Delta > 0 ? -1 : 1);
    }

    private void UpdatePointer(Point p)
    {
        if (!_open || HighlightStatusAt(p)) return;
        if (_style != MenuStyle.Classic)
        {
            bool centerHit = IsStyledCenter(p);
            SetPointer(centerHit ? -1 : StyledSlotAt(p), centerHit, p);
            return;
        }

        double dx = p.X - C;
        double dy = p.Y - C;
        double dist = Math.Sqrt(dx * dx + dy * dy);

        int idx = -1;
        bool center = false;

        if (dist <= InnerR)
        {
            center = true;
        }
        else if (!IsPreview || dist <= OuterR)
        {
            idx = SectorAt(dx, dy);
        }

        SetPointer(idx, center);
    }

    private void SetPointer(int idx, bool center, Point? point = null)
    {
        if (!_open) return;
        bool classic = _style == MenuStyle.Classic;
        bool proximityChanged = !classic && UpdateStyledHoverTargets(ref idx, center ? null : point);
        if (!proximityChanged && idx == _hover && center == _hoverCenter) return;

        if (idx != _hover)
        {
            if (classic && _hover >= 0 && _hover < _slots.Count) _slots[_hover].HoverTarget = 0;
            _hover = idx;

            if (idx >= 0 && classic)
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

        }

        _hoverCenter = center;
        UpdateCenterText();
        Cursor = (idx >= 0 || center) ? Cursors.Hand : Cursors.Arrow;
        StartRendering();
    }

    // =====================================================================
    //  Открытие, подсветка и телеметрия используют общий кадровый цикл.
    // =====================================================================
    private void StartRendering()
    {
        if (!_animationsEnabled || IsPreview && !IsVisible)
        {
            StopRendering();
            _menuT = _open ? 1 : 0;
            _menuV = _hlVel = 0;
            _hlAngle = _hlTarget;
            _hlOpacity = _hlTargetOpacity;
            foreach (var slot in _slots)
            {
                slot.Phase = slot.FromPhase = slot.T = slot.IconT = _menuT;
                slot.Hover = slot.HoverTarget;
            }
            _outgoing.Children.Clear();
            _statusRing.Advance(0, animate: false);
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
        double dt = now - _last;
        _last = now;
        if (dt <= 0) return;

        if (double.IsNaN(_transitionStart)) _transitionStart = now;
        double elapsed = now - _transitionStart;
        bool burst = _effects.Tick(now);
        bool busy = SpringTo(ref _menuT, ref _menuV, _open ? 1 : 0, _open ? 29 : 24, dt);
        if (_outgoing.Children.Count > 0)
        {
            if (double.IsNaN(_fadeStart)) _fadeStart = now;
            double remaining = Math.Max(0, 1 - (now - _fadeStart) / .14);
            _outgoing.Opacity = remaining * remaining;
            if (remaining == 0) _outgoing.Children.Clear();
            else busy = true;
        }
        if (_open) busy |= _statusRing.Advance(dt, animate: true);
        double blend = 1 - Math.Exp(-dt * 28);
        foreach (var s in _slots)
        {
            s.Hover += (s.HoverTarget - s.Hover) * blend;
            if (Math.Abs(s.HoverTarget - s.Hover) > 0.002) busy = true;
            else s.Hover = s.HoverTarget;
            busy |= UpdateSlotMotion(s, elapsed);
        }

        busy |= SpringTo(ref _hlAngle, ref _hlVel, _hlTarget, 34, dt);
        _hlOpacity += (_hlTargetOpacity - _hlOpacity) * blend;
        if (Math.Abs(_hlTargetOpacity - _hlOpacity) > .006) busy = true;
        else _hlOpacity = _hlTargetOpacity;

        ApplyVisuals();

        if (!busy && !burst)
        {
            StopRendering();
            if (!_open) CloseFinished?.Invoke();
        }
    }

    private static bool SpringTo(ref double value, ref double velocity, double target, double smoothness, double dt)
    {
        double offset = value - target;
        double slope = velocity + offset * smoothness;
        double decay = Math.Exp(-smoothness * dt);
        offset = (offset + slope * dt) * decay;
        velocity = (velocity - smoothness * slope * dt) * decay;
        value = target + offset;
        if (Math.Abs(offset) > .005 || Math.Abs(velocity) > .12) return true;
        value = target;
        velocity = 0;
        return false;
    }

    private void ApplyVisuals()
    {
        int n = _slots.Count;
        double progress = Math.Clamp(_menuT, 0, 1);
        double scale = .91 + .09 * progress;
        _menuScale.ScaleX = _menuScale.ScaleY = scale;
        _menu.Opacity = Math.Min(1, progress * 1.12);

        if (_style != MenuStyle.Classic)
        {
            foreach (var slot in _slots) ApplyStyledVisuals(slot);
            _highlight.Visibility = Visibility.Collapsed;
            return;
        }

        if (_glass)
            _sheenRotation.Angle = _animationsEnabled
                ? (1 - progress) * -19 + Math.Sin(_hlAngle) * 8 * _hlOpacity : 0;

        // значки
        double span = n > 0 ? 2 * Math.PI / n : 0;
        for (int i = 0; i < n; i++)
        {
            var s = _slots[i];
            double a = i * span;
            double dist = IconR * (.65 + .35 * s.T);
            double x = C + Math.Sin(a) * dist;
            double y = C - Math.Cos(a) * dist;

            double k = (.64 + .36 * s.T) * (1 + (_animationsEnabled ? IconHoverScale * s.Hover : 0));
            s.Scale.ScaleX = k;
            s.Scale.ScaleY = k;
            s.Holder.Opacity = s.IconT;
            s.Offset.X = x - C;
            s.Offset.Y = y - C;
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
