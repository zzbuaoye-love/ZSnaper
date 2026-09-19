using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using ZSnaper.Models;

namespace ZSnaper.Helpers;

/// <summary>
/// 使用 System.Drawing 渲染内嵌的 Lucide SVG。保持官方 SVG 几何数据不变，
/// 同时避免 UI 层依赖第三方 SVG 程序集。
/// </summary>
public static class LucideRenderer
{
    private const int OversampleFactor = 4;
    private const string ResourcePrefix = "ZSnaper.Assets.Lucide.";

    private static readonly object CacheLock = new();
    private static readonly Dictionary<LucideIcon, string> ResourceNames = new()
    {
        [LucideIcon.Camera] = "camera.svg",
        [LucideIcon.FileText] = "file-text.svg",
        [LucideIcon.Keyboard] = "keyboard.svg",
        [LucideIcon.Sliders] = "sliders-horizontal.svg",
        [LucideIcon.Info] = "info.svg",
        [LucideIcon.Sun] = "sun.svg",
        [LucideIcon.Moon] = "moon.svg",
        [LucideIcon.Copy] = "copy.svg",
        [LucideIcon.Folder] = "folder.svg",
        [LucideIcon.Sparkles] = "sparkles.svg",
        [LucideIcon.Palette] = "palette.svg",
        [LucideIcon.Zap] = "zap.svg",
        [LucideIcon.ShieldCheck] = "shield-check.svg",
        [LucideIcon.Minus] = "minus.svg",
        [LucideIcon.Check] = "check.svg",
        [LucideIcon.RotateCcw] = "rotate-ccw.svg",
        [LucideIcon.MousePointer2] = "mouse-pointer-2.svg",
        [LucideIcon.PenLine] = "pen-line.svg",
        [LucideIcon.ArrowUpRight] = "arrow-up-right.svg",
        [LucideIcon.Type] = "type.svg",
        [LucideIcon.Grid3X3] = "grid-3x3.svg",
        [LucideIcon.Undo2] = "undo-2.svg",
        [LucideIcon.GalleryVerticalEnd] = "gallery-vertical-end.svg",
        [LucideIcon.ChevronsDown] = "chevrons-down.svg",
        [LucideIcon.Download] = "download.svg",
        [LucideIcon.Pin] = "pin.svg",
        [LucideIcon.Monitor] = "monitor.svg",
        [LucideIcon.Power] = "power.svg",
        [LucideIcon.Search] = "search.svg",
        [LucideIcon.X] = "x.svg",
        [LucideIcon.ChevronDown] = "chevron-down.svg",
        [LucideIcon.ChevronUp] = "chevron-up.svg"
    };

    private static readonly Dictionary<LucideIcon, string> SvgMarkupCache = new();
    private static readonly Dictionary<IconCacheKey, Bitmap> BitmapCache = new();

    public static void Draw(
        Graphics graphics,
        LucideIcon icon,
        float x,
        float y,
        float size,
        Color color,
        float strokeWidth = 2f)
    {
        if (size <= 0f)
        {
            return;
        }

        int pixelSize = Math.Max(1, (int)Math.Ceiling(size));
        int strokeWidthKey = (int)Math.Round(strokeWidth * 1000f);
        var cacheKey = new IconCacheKey(icon, color.ToArgb(), pixelSize, strokeWidthKey);

        Bitmap bitmap;
        lock (CacheLock)
        {
            if (!BitmapCache.TryGetValue(cacheKey, out bitmap!))
            {
                bitmap = Render(icon, pixelSize, color, strokeWidth);
                BitmapCache.Add(cacheKey, bitmap);
            }
        }

        GraphicsState state = graphics.Save();
        try
        {
            graphics.CompositingQuality = CompositingQuality.HighQuality;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.DrawImage(bitmap, x, y, size, size);
        }
        finally
        {
            graphics.Restore(state);
        }
    }

    private static Bitmap Render(LucideIcon icon, int targetSize, Color color, float strokeWidth)
    {
        int renderSize = targetSize * OversampleFactor;
        using var highResolution = new Bitmap(renderSize, renderSize, PixelFormat.Format32bppPArgb);
        using (Graphics graphics = Graphics.FromImage(highResolution))
        using (var pen = new Pen(color, strokeWidth)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round
        })
        using (var fillBrush = new SolidBrush(color))
        {
            graphics.Clear(Color.Transparent);
            graphics.CompositingMode = CompositingMode.SourceCopy;
            graphics.CompositingQuality = CompositingQuality.HighQuality;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.ScaleTransform(renderSize / 24f, renderSize / 24f);

            XDocument svg = XDocument.Parse(GetMarkup(icon));
            foreach (XElement element in svg.Descendants())
            {
                DrawElement(graphics, element, pen, fillBrush);
            }
        }

        var result = new Bitmap(targetSize, targetSize, PixelFormat.Format32bppPArgb);
        using Graphics output = Graphics.FromImage(result);
        output.Clear(Color.Transparent);
        output.CompositingMode = CompositingMode.SourceCopy;
        output.CompositingQuality = CompositingQuality.HighQuality;
        output.InterpolationMode = InterpolationMode.HighQualityBicubic;
        output.PixelOffsetMode = PixelOffsetMode.HighQuality;
        output.SmoothingMode = SmoothingMode.HighQuality;
        output.DrawImage(
            highResolution,
            new Rectangle(0, 0, targetSize, targetSize),
            0,
            0,
            highResolution.Width,
            highResolution.Height,
            GraphicsUnit.Pixel);

        return result;
    }

    private static void DrawElement(Graphics graphics, XElement element, Pen pen, Brush fillBrush)
    {
        string name = element.Name.LocalName;
        using GraphicsPath? path = name switch
        {
            "path" => SvgPathParser.Parse((string?)element.Attribute("d") ?? string.Empty),
            "circle" => CreateEllipsePath(
                ReadNumber(element, "cx"),
                ReadNumber(element, "cy"),
                ReadNumber(element, "r"),
                ReadNumber(element, "r")),
            "ellipse" => CreateEllipsePath(
                ReadNumber(element, "cx"),
                ReadNumber(element, "cy"),
                ReadNumber(element, "rx"),
                ReadNumber(element, "ry")),
            "rect" => CreateRectPath(element),
            "line" => CreateLinePath(element),
            "polyline" => CreatePointsPath((string?)element.Attribute("points"), close: false),
            "polygon" => CreatePointsPath((string?)element.Attribute("points"), close: true),
            _ => null
        };

        if (path is null || path.PointCount == 0)
        {
            return;
        }

        string? fill = (string?)element.Attribute("fill");
        if (!string.IsNullOrWhiteSpace(fill) && !fill.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            graphics.FillPath(fillBrush, path);
        }

        graphics.DrawPath(pen, path);
    }

    private static GraphicsPath CreateEllipsePath(float cx, float cy, float rx, float ry)
    {
        var path = new GraphicsPath();
        path.AddEllipse(cx - rx, cy - ry, rx * 2f, ry * 2f);
        return path;
    }

    private static GraphicsPath CreateRectPath(XElement element)
    {
        float x = ReadNumber(element, "x");
        float y = ReadNumber(element, "y");
        float width = ReadNumber(element, "width");
        float height = ReadNumber(element, "height");
        float rx = ReadNumber(element, "rx");
        float ry = ReadNumber(element, "ry");
        if (rx <= 0f && ry > 0f) rx = ry;
        if (ry <= 0f && rx > 0f) ry = rx;
        rx = Math.Min(rx, width / 2f);
        ry = Math.Min(ry, height / 2f);

        var path = new GraphicsPath();
        if (rx <= 0f || ry <= 0f)
        {
            path.AddRectangle(new RectangleF(x, y, width, height));
            return path;
        }

        path.AddArc(x, y, rx * 2f, ry * 2f, 180f, 90f);
        path.AddArc(x + width - rx * 2f, y, rx * 2f, ry * 2f, 270f, 90f);
        path.AddArc(x + width - rx * 2f, y + height - ry * 2f, rx * 2f, ry * 2f, 0f, 90f);
        path.AddArc(x, y + height - ry * 2f, rx * 2f, ry * 2f, 90f, 90f);
        path.CloseFigure();
        return path;
    }

    private static GraphicsPath CreateLinePath(XElement element)
    {
        var path = new GraphicsPath();
        path.AddLine(
            ReadNumber(element, "x1"),
            ReadNumber(element, "y1"),
            ReadNumber(element, "x2"),
            ReadNumber(element, "y2"));
        return path;
    }

    private static GraphicsPath CreatePointsPath(string? points, bool close)
    {
        var path = new GraphicsPath();
        if (string.IsNullOrWhiteSpace(points))
        {
            return path;
        }

        float[] values = Regex.Matches(points, SvgPathParser.NumberPattern)
            .Select(match => float.Parse(match.Value, CultureInfo.InvariantCulture))
            .ToArray();
        if (values.Length < 4)
        {
            return path;
        }

        path.StartFigure();
        for (int i = 2; i + 1 < values.Length; i += 2)
        {
            path.AddLine(values[i - 2], values[i - 1], values[i], values[i + 1]);
        }
        if (close) path.CloseFigure();
        return path;
    }

    private static float ReadNumber(XElement element, string attributeName)
    {
        string? value = (string?)element.Attribute(attributeName);
        return float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float number)
            ? number
            : 0f;
    }

    private static string GetMarkup(LucideIcon icon)
    {
        if (SvgMarkupCache.TryGetValue(icon, out string? markup))
        {
            return markup;
        }

        string fileName = ResourceNames.TryGetValue(icon, out string? mappedName)
            ? mappedName
            : throw new ArgumentOutOfRangeException(nameof(icon), icon, "Unknown Lucide icon.");
        string resourceName = ResourcePrefix + fileName;

        Assembly assembly = typeof(LucideRenderer).Assembly;
        using Stream stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded Lucide SVG not found: {resourceName}");
        using var reader = new StreamReader(stream);
        markup = reader.ReadToEnd();
        SvgMarkupCache.Add(icon, markup);
        return markup;
    }

    private readonly record struct IconCacheKey(
        LucideIcon Icon,
        int Argb,
        int PixelSize,
        int StrokeWidthKey);

    internal static class SvgPathParser
    {
        internal const string NumberPattern = @"[-+]?(?:\d*\.\d+|\d+\.?\d*)(?:[eE][-+]?\d+)?";
        private static readonly Regex TokenRegex = new($@"[A-Za-z]|{NumberPattern}", RegexOptions.Compiled);

        public static GraphicsPath Parse(string data)
        {
            var path = new GraphicsPath();
            if (string.IsNullOrWhiteSpace(data))
            {
                return path;
            }

            string[] tokens = TokenRegex.Matches(data).Select(match => match.Value).ToArray();
            int index = 0;
            char command = '\0';
            char previousCommand = '\0';
            PointF current = PointF.Empty;
            PointF figureStart = PointF.Empty;
            PointF lastCubicControl = PointF.Empty;
            PointF lastQuadraticControl = PointF.Empty;

            while (index < tokens.Length)
            {
                if (IsCommand(tokens[index]))
                {
                    command = tokens[index++][0];
                }
                else if (command == '\0')
                {
                    break;
                }

                bool relative = char.IsLower(command);
                char normalized = char.ToUpperInvariant(command);

                switch (normalized)
                {
                    case 'M':
                    {
                        PointF target = ReadPoint(tokens, ref index, relative, current);
                        current = target;
                        figureStart = target;
                        path.StartFigure();
                        previousCommand = command;
                        command = relative ? 'l' : 'L';
                        break;
                    }
                    case 'L':
                    {
                        PointF target = ReadPoint(tokens, ref index, relative, current);
                        path.AddLine(current, target);
                        current = target;
                        previousCommand = command;
                        break;
                    }
                    case 'H':
                    {
                        float x = Read(tokens, ref index) + (relative ? current.X : 0f);
                        var target = new PointF(x, current.Y);
                        path.AddLine(current, target);
                        current = target;
                        previousCommand = command;
                        break;
                    }
                    case 'V':
                    {
                        float y = Read(tokens, ref index) + (relative ? current.Y : 0f);
                        var target = new PointF(current.X, y);
                        path.AddLine(current, target);
                        current = target;
                        previousCommand = command;
                        break;
                    }
                    case 'C':
                    {
                        PointF control1 = ReadPoint(tokens, ref index, relative, current);
                        PointF control2 = ReadPoint(tokens, ref index, relative, current);
                        PointF target = ReadPoint(tokens, ref index, relative, current);
                        path.AddBezier(current, control1, control2, target);
                        current = target;
                        lastCubicControl = control2;
                        previousCommand = command;
                        break;
                    }
                    case 'S':
                    {
                        PointF control1 = IsPrevious(previousCommand, 'C', 'S')
                            ? Reflect(lastCubicControl, current)
                            : current;
                        PointF control2 = ReadPoint(tokens, ref index, relative, current);
                        PointF target = ReadPoint(tokens, ref index, relative, current);
                        path.AddBezier(current, control1, control2, target);
                        current = target;
                        lastCubicControl = control2;
                        previousCommand = command;
                        break;
                    }
                    case 'Q':
                    {
                        PointF control = ReadPoint(tokens, ref index, relative, current);
                        PointF target = ReadPoint(tokens, ref index, relative, current);
                        AddQuadratic(path, current, control, target);
                        current = target;
                        lastQuadraticControl = control;
                        previousCommand = command;
                        break;
                    }
                    case 'T':
                    {
                        PointF control = IsPrevious(previousCommand, 'Q', 'T')
                            ? Reflect(lastQuadraticControl, current)
                            : current;
                        PointF target = ReadPoint(tokens, ref index, relative, current);
                        AddQuadratic(path, current, control, target);
                        current = target;
                        lastQuadraticControl = control;
                        previousCommand = command;
                        break;
                    }
                    case 'A':
                    {
                        float rx = Read(tokens, ref index);
                        float ry = Read(tokens, ref index);
                        float rotation = Read(tokens, ref index);
                        bool largeArc = ReadArcFlag(tokens, ref index);
                        bool sweep = ReadArcFlag(tokens, ref index);
                        PointF target = ReadPoint(tokens, ref index, relative, current);
                        AddArc(path, current, target, rx, ry, rotation, largeArc, sweep);
                        current = target;
                        previousCommand = command;
                        break;
                    }
                    case 'Z':
                        path.CloseFigure();
                        current = figureStart;
                        previousCommand = command;
                        command = '\0';
                        break;
                    default:
                        index++;
                        command = '\0';
                        break;
                }
            }

            return path;
        }

        private static void AddQuadratic(GraphicsPath path, PointF start, PointF control, PointF end)
        {
            var control1 = new PointF(
                start.X + (control.X - start.X) * 2f / 3f,
                start.Y + (control.Y - start.Y) * 2f / 3f);
            var control2 = new PointF(
                end.X + (control.X - end.X) * 2f / 3f,
                end.Y + (control.Y - end.Y) * 2f / 3f);
            path.AddBezier(start, control1, control2, end);
        }

        private static void AddArc(
            GraphicsPath path,
            PointF start,
            PointF end,
            float rx,
            float ry,
            float rotationDegrees,
            bool largeArc,
            bool sweep)
        {
            rx = Math.Abs(rx);
            ry = Math.Abs(ry);
            if (rx < float.Epsilon || ry < float.Epsilon || DistanceSquared(start, end) < float.Epsilon)
            {
                if (DistanceSquared(start, end) >= float.Epsilon) path.AddLine(start, end);
                return;
            }

            double phi = rotationDegrees * Math.PI / 180d;
            double cosPhi = Math.Cos(phi);
            double sinPhi = Math.Sin(phi);
            double dx = (start.X - end.X) / 2d;
            double dy = (start.Y - end.Y) / 2d;
            double x1Prime = cosPhi * dx + sinPhi * dy;
            double y1Prime = -sinPhi * dx + cosPhi * dy;

            double rxSquared = rx * rx;
            double rySquared = ry * ry;
            double scale = x1Prime * x1Prime / rxSquared + y1Prime * y1Prime / rySquared;
            if (scale > 1d)
            {
                double factor = Math.Sqrt(scale);
                rx = (float)(rx * factor);
                ry = (float)(ry * factor);
                rxSquared = rx * rx;
                rySquared = ry * ry;
            }

            double numerator = Math.Max(0d,
                rxSquared * rySquared - rxSquared * y1Prime * y1Prime - rySquared * x1Prime * x1Prime);
            double denominator = rxSquared * y1Prime * y1Prime + rySquared * x1Prime * x1Prime;
            double coefficient = denominator <= double.Epsilon ? 0d : Math.Sqrt(numerator / denominator);
            if (largeArc == sweep) coefficient = -coefficient;

            double cxPrime = coefficient * (rx * y1Prime / ry);
            double cyPrime = coefficient * (-ry * x1Prime / rx);
            double centerX = cosPhi * cxPrime - sinPhi * cyPrime + (start.X + end.X) / 2d;
            double centerY = sinPhi * cxPrime + cosPhi * cyPrime + (start.Y + end.Y) / 2d;

            double startAngle = Math.Atan2((y1Prime - cyPrime) / ry, (x1Prime - cxPrime) / rx);
            double endVectorX = (-x1Prime - cxPrime) / rx;
            double endVectorY = (-y1Prime - cyPrime) / ry;
            double deltaAngle = VectorAngle(
                (x1Prime - cxPrime) / rx,
                (y1Prime - cyPrime) / ry,
                endVectorX,
                endVectorY);
            if (!sweep && deltaAngle > 0d) deltaAngle -= Math.PI * 2d;
            if (sweep && deltaAngle < 0d) deltaAngle += Math.PI * 2d;

            int segments = Math.Max(1, (int)Math.Ceiling(Math.Abs(deltaAngle) / (Math.PI / 2d)));
            double segmentAngle = deltaAngle / segments;
            PointF segmentStart = start;
            for (int segment = 0; segment < segments; segment++)
            {
                double angle1 = startAngle + segment * segmentAngle;
                double angle2 = angle1 + segmentAngle;
                double alpha = 4d / 3d * Math.Tan((angle2 - angle1) / 4d);
                PointF segmentEnd = ArcPoint(centerX, centerY, rx, ry, cosPhi, sinPhi, angle2);
                PointF derivative1 = ArcDerivative(rx, ry, cosPhi, sinPhi, angle1);
                PointF derivative2 = ArcDerivative(rx, ry, cosPhi, sinPhi, angle2);
                var control1 = new PointF(
                    segmentStart.X + (float)(alpha * derivative1.X),
                    segmentStart.Y + (float)(alpha * derivative1.Y));
                var control2 = new PointF(
                    segmentEnd.X - (float)(alpha * derivative2.X),
                    segmentEnd.Y - (float)(alpha * derivative2.Y));
                path.AddBezier(segmentStart, control1, control2, segmentEnd);
                segmentStart = segmentEnd;
            }
        }

        private static PointF ArcPoint(
            double centerX,
            double centerY,
            double rx,
            double ry,
            double cosPhi,
            double sinPhi,
            double angle) => new(
                (float)(centerX + rx * cosPhi * Math.Cos(angle) - ry * sinPhi * Math.Sin(angle)),
                (float)(centerY + rx * sinPhi * Math.Cos(angle) + ry * cosPhi * Math.Sin(angle)));

        private static PointF ArcDerivative(
            double rx,
            double ry,
            double cosPhi,
            double sinPhi,
            double angle) => new(
                (float)(-rx * cosPhi * Math.Sin(angle) - ry * sinPhi * Math.Cos(angle)),
                (float)(-rx * sinPhi * Math.Sin(angle) + ry * cosPhi * Math.Cos(angle)));

        private static double VectorAngle(double ux, double uy, double vx, double vy) =>
            Math.Atan2(ux * vy - uy * vx, ux * vx + uy * vy);

        private static float DistanceSquared(PointF first, PointF second)
        {
            float dx = first.X - second.X;
            float dy = first.Y - second.Y;
            return dx * dx + dy * dy;
        }

        private static bool IsPrevious(char command, char first, char second)
        {
            char normalized = char.ToUpperInvariant(command);
            return normalized == first || normalized == second;
        }

        private static PointF Reflect(PointF control, PointF around) =>
            new(around.X * 2f - control.X, around.Y * 2f - control.Y);

        private static PointF ReadPoint(string[] tokens, ref int index, bool relative, PointF current)
        {
            float x = Read(tokens, ref index);
            float y = Read(tokens, ref index);
            return relative ? new PointF(current.X + x, current.Y + y) : new PointF(x, y);
        }

        private static float Read(string[] tokens, ref int index)
        {
            if (index >= tokens.Length || IsCommand(tokens[index]))
            {
                throw new FormatException("Invalid SVG path data.");
            }
            return float.Parse(tokens[index++], CultureInfo.InvariantCulture);
        }

        private static bool ReadArcFlag(string[] tokens, ref int index)
        {
            if (index >= tokens.Length || IsCommand(tokens[index]))
            {
                throw new FormatException("Invalid SVG arc flag.");
            }

            string token = tokens[index];
            if (token[0] is not ('0' or '1'))
            {
                throw new FormatException("SVG arc flags must be 0 or 1.");
            }

            bool value = token[0] == '1';
            if (token.Length == 1)
            {
                index++;
            }
            else
            {
                // SVG 允许两个 arc flag 紧邻书写，例如官方 zap.svg 中的 "00"。
                tokens[index] = token[1..];
            }
            return value;
        }

        private static bool IsCommand(string token) => token.Length == 1 && char.IsLetter(token[0]);
    }
}
