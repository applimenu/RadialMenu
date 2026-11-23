using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using ShapePath = System.Windows.Shapes.Path;

namespace RadialMenu1;

public sealed partial class RadialView
{
    private const double OpenDuration = .29;
    private const double CloseDuration = .15;
    private const double HexRadius = 29.5;
    private const double HoneycombOuter = OuterR * 1.10;
    private const double RingOuter = OuterR;
    private const double RingInner = 54;
    private const double RingHubRadius = 50;
    private const double HoverReach = 28;
    private const double TileHoverScale = 0.035;

    private static readonly Point MenuCenter = new(C, C);
    private static readonly Geometry RingHub = new EllipseGeometry(MenuCenter, RingHubRadius, RingHubRadius);
    private static readonly Geometry CircleHub = new EllipseGeometry(MenuCenter, 54, 54);
    private static readonly Vector[] HexVertices = Enumerable.Range(0, 6)
        .Select(i => new Vector(Math.Sin(i * Math.PI / 3 + Math.PI / 6), -Math.Cos(i * Math.PI / 3 + Math.PI / 6))).ToArray();
    private static readonly Vector[] HexCenters =
    [
        new(0, -54), new(27 * Math.Sqrt(3), -27), new(27 * Math.Sqrt(3), 27),
        new(0, 54), new(-27 * Math.Sqrt(3), 27), new(-27 * Math.Sqrt(3), -27),
        new(54 * Math.Sqrt(3), -54), new(-54 * Math.Sqrt(3), 54), new(0, -108), new(0, 108),
        new(54 * Math.Sqrt(3), 54)
    ];
    private static readonly int[] FiveHexOrder = [0, 1, 2, 3, 5];

    private sealed class StyledSlot(ShapePath shape, ShapePath light, Point center, ScaleTransform scale, TranslateTransform move)
    {
        public ShapePath Shape { get; } = shape;
        public Point Center { get; } = center;
        public ScaleTransform Scale { get; } = scale;
        public TranslateTransform Move { get; } = move;
        public Point[] Outline { get; } = FlattenOutline(shape.Data);
        public ShapePath Light { get; } = light;
    }

    private readonly Canvas _shapeLayer = new() { Width = ViewSize, Height = ViewSize };
    private readonly ShapePath _styledHub = new() { StrokeThickness = 1 };
    private readonly TextBlock _emptyPreview = new()
    {
        Width = ViewSize, TextAlignment = TextAlignment.Center, Text = "Добавьте приложения в этот режим",
        FontSize = 15, IsHitTestVisible = false, Visibility = Visibility.Collapsed
    };
    private readonly KeySpline _openingEase = new(0.2, 0.7, 0.3, 1);
    private readonly KeySpline _iconEase = new(0, 0, 0.58, 1);
    private readonly ImageDrawing _styledGlassImage = new(null, new Rect(C - OuterR, C - OuterR, GlassDiameter, GlassDiameter));
    private readonly GeometryDrawing _styledGlassTint = new(null, null, new RectangleGeometry(new Rect(0, 0, ViewSize, ViewSize)));
    private readonly DrawingBrush _styledGlass = new();
    private Brush? _styledFill, _styledStroke, _styledActive, _styledActiveStroke;
    private MenuStyle _style;

    private void InitializeStyledLayers()
    {
        var bounds = new Rect(0, 0, ViewSize, ViewSize);
        var glass = new DrawingGroup();
        glass.Children.Add(_styledGlassImage);
        glass.Children.Add(_styledGlassTint);
        _styledGlass.Drawing = glass;
        _styledGlass.ViewboxUnits = _styledGlass.ViewportUnits = BrushMappingMode.Absolute;
        _styledGlass.Viewbox = _styledGlass.Viewport = bounds;
        _styledGlass.Stretch = Stretch.None;

        _menu.Children.Add(_styledHub);
        _menu.Children.Add(_shapeLayer);
        SetTop(_emptyPreview, C - 10);
        Children.Add(_emptyPreview);
    }

    private void ApplyLayoutChrome()
    {
        bool classic = _style == MenuStyle.Classic;
        var legacy = classic ? Visibility.Visible : Visibility.Collapsed;
        _disc.Visibility = _glassBackdrop.Visibility = _centerDisc.Visibility = _dividers.Visibility = legacy;
        _cursorGlow.Visibility = Visibility.Collapsed;

        _centerHost.Width = _style == MenuStyle.Ring ? 88 : 100;
        SetLeft(_centerHost, C - _centerHost.Width / 2);
        SetTop(_dots, C + 28);
        _styledHub.Data = _style switch
        {
            MenuStyle.Ring => RingHub,
            MenuStyle.Circles => CircleHub,
            _ => null
        };
        _styledHub.StrokeThickness = 1.25;
        _styledHub.StrokeLineJoin = PenLineJoin.Round;
    }

    private void UpdateEmptyState(MenuPalette palette)
    {
        bool empty = IsPreview && _slots.Count == 0;
        _emptyPreview.Foreground = new SolidColorBrush(palette.Muted);
        _emptyPreview.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        _menu.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ApplyStyledAppearance(MenuPalette palette, double opacity)
    {
        _styledGlassTint.Brush = palette.WheelSurface(1, glass: true);
        _styledGlass.Opacity = opacity;
        _styledFill = _glass ? _styledGlass : palette.WheelSurface(opacity);
        _styledStroke = palette.WheelEdge();
        _styledActive = palette.WheelHover(opacity);
        _styledActiveStroke = new SolidColorBrush(WithAlpha(palette.Accent, 205));
        _styledActiveStroke.Freeze();
        _styledHub.Stroke = palette.WheelEdge(center: true);
        _styledHub.Fill = palette.WheelSurface(opacity, center: true, glass: _glass);

        foreach (var slot in _slots)
            if (slot.Style is { } tile) ApplyStyledSlotPalette(tile);
    }

    private void ApplyStyledSlotPalette(StyledSlot tile)
    {
        tile.Shape.Fill = _styledFill;
        tile.Shape.Stroke = _styledStroke;
        tile.Light.Fill = _styledActive;
        tile.Light.Stroke = _styledActiveStroke;
    }

    private void BuildStyledSlots()
    {
        int count = _slots.Count;
        double hexScale = 1;
        if (_style == MenuStyle.Honeycomb)
        {
            if (count > 0 && count <= HexCenters.Length)
            {
                double reach = Enumerable.Range(0, count).Max(i =>
                {
                    Vector cell = HoneycombCenter(i, count, 0) - MenuCenter;
                    return HexVertices.Max(vertex => (cell + vertex * HexRadius).Length);
                });
                hexScale = HoneycombOuter / reach;
            }
            double hubRadius = HexRadius * hexScale;
            _styledHub.Data = Hexagon(MenuCenter, hubRadius);
            _centerHost.Width = Math.Min(84, hubRadius * 2 - 5);
            SetLeft(_centerHost, C - _centerHost.Width / 2);
            SetTop(_dots, hubRadius < 36 ? C + 20 : C + 27);
        }
        double spacing = Math.Sin(Math.PI / Math.Max(2, count));
        double circleRadius = Math.Clamp((OuterR * spacing - 5) / (1 + spacing), 1, 34);
        for (int i = 0; i < count; i++)
        {
            double angle = 2 * Math.PI * i / count;
            Point center;
            Geometry geometry;
            switch (_style)
            {
                case MenuStyle.Ring:
                    center = P(97, angle);
                    geometry = StyledRingSector(angle, count);
                    break;
                case MenuStyle.Honeycomb:
                    center = MenuCenter + (HoneycombCenter(i, count, angle) - MenuCenter) * hexScale;
                    geometry = Hexagon(center, count <= HexCenters.Length
                        ? HexRadius * hexScale : Math.Min(HexRadius, TileRadius(100, count)));
                    break;
                default:
                    center = P(OuterR - circleRadius, angle);
                    geometry = new EllipseGeometry(center, circleRadius, circleRadius);
                    break;
            }

            var scale = new ScaleTransform(1, 1, center.X, center.Y);
            var move = new TranslateTransform();
            var transform = new TransformGroup();
            transform.Children.Add(scale);
            transform.Children.Add(move);
            var path = new ShapePath { Data = geometry, StrokeThickness = 1, StrokeLineJoin = PenLineJoin.Round, RenderTransform = transform };
            var light = new ShapePath { Data = geometry, StrokeThickness = 1.4, StrokeLineJoin = PenLineJoin.Round, RenderTransform = transform };
            var surface = new Canvas();
            surface.Children.Add(path);
            surface.Children.Add(light);
            var slot = _slots[i];
            slot.Style = new StyledSlot(path, light, center, scale, move);
            _shapeLayer.Children.Add(surface);
            ApplyStyledSlotPalette(slot.Style);
        }
    }

    private static double TileRadius(double orbit, int count) =>
        Math.Max(1, orbit * Math.Sin(Math.PI / Math.Max(2, count)) - 5);

    private static Point HoneycombCenter(int index, int count, double angle)
    {
        if (count > HexCenters.Length) return P(100, angle);
        int cell = count switch
        {
            5 => FiveHexOrder[index],
            < 6 => (int)Math.Round(index * 6.0 / count, MidpointRounding.AwayFromZero),
            _ => index
        };
        return MenuCenter + HexCenters[cell];
    }

    private static Geometry Hexagon(Point center, double radius)
    {
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(center + HexVertices[0] * radius, true, true);
            for (int i = 1; i < HexVertices.Length; i++) ctx.LineTo(center + HexVertices[i] * radius, true, false);
        }
        geometry.Freeze();
        return geometry;
    }

    private static Geometry StyledRingSector(double angle, int count)
    {
        double half = Math.PI / count;
        double gap = count == 1 ? 3 : Math.Min(3, RingInner * Math.Sin(half) * 0.5);
        double outer = half - Math.Asin(gap / RingOuter);
        double inner = half - Math.Asin(gap / RingInner);
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(P(RingOuter, angle - outer), true, true);
            ctx.ArcTo(P(RingOuter, angle + outer), new Size(RingOuter, RingOuter), 0,
                outer > Math.PI / 2, SweepDirection.Clockwise, true, false);
            ctx.LineTo(P(RingInner, angle + inner), true, false);
            ctx.ArcTo(P(RingInner, angle - inner), new Size(RingInner, RingInner), 0,
                inner > Math.PI / 2, SweepDirection.Counterclockwise, true, false);
        }
        geometry.Freeze();
        return geometry;
    }

    private int StyledSlotAt(Point point)
    {
        for (int i = 0; i < _slots.Count; i++)
            if (_slots[i].Style!.Shape.Data.FillContains(point)) return i;

        for (int i = 0; i < _slots.Count; i++)
        {
            var slot = _slots[i];
            var tile = slot.Style!;
            if (slot.T >= 0.99 && slot.Hover > 0.01 &&
                tile.Shape.RenderTransform.Inverse is { } inverse &&
                tile.Shape.Data.FillContains(inverse.Transform(point)))
                return i;
        }
        return -1;
    }

    private static Point[] FlattenOutline(Geometry geometry)
    {
        var figure = geometry.GetFlattenedPathGeometry().Figures[0];
        var points = new List<Point> { figure.StartPoint };
        foreach (var segment in figure.Segments)
        {
            if (segment is PolyLineSegment polyline) points.AddRange(polyline.Points);
            else if (segment is LineSegment line) points.Add(line.Point);
        }
        points.Add(figure.StartPoint);
        return points.ToArray();
    }

    private static double DistanceToOutline(StyledSlot tile, Point point)
    {
        var bounds = tile.Shape.Data.Bounds;
        bounds.Inflate(HoverReach, HoverReach);
        if (!bounds.Contains(point)) return HoverReach;

        double nearestSquared = HoverReach * HoverReach;
        var outline = tile.Outline;
        for (int i = 1; i < outline.Length; i++)
        {
            Point start = outline[i - 1];
            Vector side = outline[i] - start;
            double lengthSquared = side.LengthSquared;
            if (lengthSquared == 0) continue;
            double t = Math.Clamp(Vector.Multiply(point - start, side) / lengthSquared, 0, 1);
            nearestSquared = Math.Min(nearestSquared, (point - (start + side * t)).LengthSquared);
        }
        return Math.Sqrt(nearestSquared);
    }

    private bool UpdateStyledHoverTargets(ref int selected, Point? point)
    {
        int nearest = selected;
        double strength = selected >= 0 ? 1 : 0;
        if (selected < 0 && point is { } pointer)
        {
            for (int i = 0; i < _slots.Count; i++)
            {
                double approach = 1 - DistanceToOutline(_slots[i].Style!, pointer) / HoverReach;
                if (approach <= strength) continue;
                nearest = i;
                strength = approach;
            }
        }

        selected = nearest;
        bool changed = false;
        for (int i = 0; i < _slots.Count; i++)
        {
            var slot = _slots[i];
            double target = i == nearest ? strength : 0;
            changed |= Math.Abs(slot.HoverTarget - target) > 0.001;
            slot.HoverTarget = target;
        }
        return changed;
    }

    private bool IsStyledCenter(Point point) => _styledHub.Data is { } center
        ? center.FillContains(point)
        : (point - MenuCenter).LengthSquared <= HexRadius * HexRadius;

    private void BeginSlotTransition()
    {
        _transitionStart = double.NaN;
        double spacing = _style == MenuStyle.Honeycomb ? .067 : .052;
        spacing = Math.Min(spacing, .48 / Math.Max(1, _slots.Count - 1));
        for (int i = 0; i < _slots.Count; i++)
        {
            var slot = _slots[i];
            slot.FromPhase = slot.Phase;
            slot.Delay = _open
                ? (slot.Phase == 0 ? i * spacing : 0)
                : (slot.Phase == 1 ? (_slots.Count - 1 - i) * .008 : 0);
        }
    }

    private bool UpdateSlotMotion(Slot slot, double elapsed)
    {
        double target = _open ? 1 : 0;
        if (slot.Phase == target) return false;
        double time = elapsed - slot.Delay;
        if (time <= 0) return true;
        slot.Phase = _open ? Math.Min(1, slot.FromPhase + time / OpenDuration)
                           : Math.Max(0, slot.FromPhase - time / CloseDuration);
        slot.T = _openingEase.GetSplineProgress(slot.Phase);
        slot.IconT = _iconEase.GetSplineProgress(Math.Clamp((slot.Phase - .16) / .84, 0, 1));
        return slot.Phase != target;
    }

    private void ApplyStyledVisuals(Slot slot)
    {
        var tile = slot.Style!;
        double travel = slot.T * (.82 + .18 * Math.Clamp(_menuT, 0, 1));
        var position = MenuCenter + (tile.Center - MenuCenter) * travel;
        double scale = .48 + .52 * travel;
        double hover = slot.Hover;
        double lift = _animationsEnabled ? hover : 0;
        tile.Scale.ScaleX = tile.Scale.ScaleY = scale * (1 + TileHoverScale * lift);
        tile.Move.X = position.X - tile.Center.X;
        tile.Move.Y = position.Y - tile.Center.Y;
        tile.Shape.Opacity = slot.T;
        tile.Light.Opacity = slot.T * hover;
        slot.Scale.ScaleX = slot.Scale.ScaleY = scale * (1 + IconHoverScale * lift);
        slot.Holder.Opacity = slot.T * slot.IconT;
        slot.Offset.X = position.X - C;
        slot.Offset.Y = position.Y - C;
    }
}
