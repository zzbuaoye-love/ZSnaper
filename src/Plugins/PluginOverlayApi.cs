using System.Drawing.Drawing2D;
using SkiaSharp;
using ZSnaper.Services;

namespace ZSnaper.Plugins;

public sealed record PluginArrowToolInfo(string PluginId, PluginArrowToolDefinition Definition, PluginArrowRenderer Renderer);

internal sealed class PluginOverlayApi(string pluginId, Action changed) : IZSnaperOverlayApi, IDisposable
{
    private readonly object _sync = new();
    private readonly Dictionary<string, PluginArrowToolInfo> _tools = new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<PluginArrowToolInfo> Tools { get { lock (_sync) return _tools.Values.ToArray(); } }

    public PluginArrowRegistration RegisterArrow(PluginArrowToolDefinition tool, PluginArrowRenderer renderer)
    {
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentNullException.ThrowIfNull(renderer);
        if (string.IsNullOrWhiteSpace(tool.Id) || tool.Id.Length > 64 ||
            tool.Id.Any(c => !char.IsLetterOrDigit(c) && c is not '_' and not '-') ||
            string.IsNullOrWhiteSpace(tool.Label) || tool.Label.Length > 40)
            throw new ArgumentException("箭头工具 ID 或名称无效", nameof(tool));
        lock (_sync)
            if (!_tools.TryAdd(tool.Id, new(pluginId, tool, renderer)))
                throw new InvalidOperationException("箭头工具 ID 已存在");
        changed();
        return new(tool.Id);
    }

    public bool UnregisterArrow(string id)
    {
        bool removed;
        lock (_sync) removed = _tools.Remove(id);
        if (removed) changed();
        return removed;
    }

    public void Dispose() { lock (_sync) _tools.Clear(); }
}

/// <summary>Owns a geometry snapshot, never plugin-owned mutable point collections.</summary>
internal sealed class PluginArrowDrawing
{
    private readonly (PointF[] Points, bool Closed, bool Filled)[] _paths;
    public RectangleF Bounds { get; }

    private PluginArrowDrawing((PointF[] Points, bool Closed, bool Filled)[] paths)
    {
        _paths = paths;
        PointF[] points = paths.SelectMany(p => p.Points).ToArray();
        Bounds = RectangleF.FromLTRB(points.Min(p => p.X), points.Min(p => p.Y),
            points.Max(p => p.X), points.Max(p => p.Y));
    }

    public static PluginArrowDrawing? Build(PluginArrowToolInfo tool, PointF start, PointF end, float width)
    {
        try
        {
            var result = tool.Renderer(new(new(start.X, start.Y), new(end.X, end.Y), width));
            if (result is null || result.Count is < 1 or > 64) throw new InvalidDataException("Invalid arrow paths");
            var paths = result.Select(path =>
            {
                if (path.Points is null || path.Points.Count is < 2 or > 4096)
                    throw new InvalidDataException("Invalid arrow point count");
                var points = path.Points.Select(p => new PointF(p.X, p.Y)).ToArray();
                if (points.Any(p => !float.IsFinite(p.X) || !float.IsFinite(p.Y) || Math.Abs(p.X) > 1_000_000 || Math.Abs(p.Y) > 1_000_000))
                    throw new InvalidDataException("Invalid arrow coordinates");
                if (path.Filled && !path.Closed) throw new InvalidDataException("Filled arrow paths must be closed");
                return (points, path.Closed, path.Filled);
            }).ToArray();
            return new(paths);
        }
        catch (Exception exception)
        {
            AppDiagnostics.LogException($"Plugin.Arrow.{tool.PluginId}.{tool.Definition.Id}", exception);
            return null;
        }
    }

    private static GraphicsPath CreatePath(PointF[] points, bool closed)
    {
        var path = new GraphicsPath();
        path.AddLines(points);
        if (closed) path.CloseFigure();
        return path;
    }

    public void Draw(Graphics graphics, Color color, float width)
    {
        using var pen = new Pen(color, width) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };
        using var brush = new SolidBrush(color);
        foreach (var item in _paths)
        {
            using GraphicsPath path = CreatePath(item.Points, item.Closed);
            if (item.Filled) graphics.FillPath(brush, path);
            else graphics.DrawPath(pen, path);
        }
    }

    public bool HitTest(Point point, float width)
    {
        using var pen = new Pen(Color.Black, Math.Max(16f, width + 8f));
        foreach (var item in _paths)
        {
            using GraphicsPath path = CreatePath(item.Points, item.Closed);
            if ((item.Filled && path.IsVisible(point)) || path.IsOutlineVisible(point, pen)) return true;
        }
        return false;
    }

    public void Draw(SKCanvas canvas, Color color, float width)
    {
        using var paint = new SKPaint
        {
            Color = new SKColor(color.R, color.G, color.B, color.A), IsAntialias = true,
            StrokeWidth = width, StrokeJoin = SKStrokeJoin.Round, StrokeCap = SKStrokeCap.Round
        };
        foreach (var item in _paths)
        {
            using var path = new SKPath();
            path.MoveTo(item.Points[0].X, item.Points[0].Y);
            foreach (PointF point in item.Points.Skip(1)) path.LineTo(point.X, point.Y);
            if (item.Closed) path.Close();
            paint.Style = item.Filled ? SKPaintStyle.Fill : SKPaintStyle.Stroke;
            canvas.DrawPath(path, paint);
        }
    }
}
