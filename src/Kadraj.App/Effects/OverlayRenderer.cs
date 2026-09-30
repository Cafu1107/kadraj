using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace Kadraj.Effects;

/// <summary>
/// The frame and name tag, rendered once per settings change into a premultiplied BGRA layer.
/// Runs lists the non-transparent spans of each row so blending skips empty pixels.
/// </summary>
public sealed class Overlay
{
    public int Width { get; }
    public int Height { get; }
    public byte[] Pixels { get; }
    public int[] RowStart { get; }  // row y uses runs RowStart[y] .. RowStart[y + 1] - 1
    public int[] RunX { get; }
    public int[] RunLength { get; }

    Overlay(int width, int height, byte[] pixels)
    {
        Width = width;
        Height = height;
        Pixels = pixels;
        RowStart = new int[height + 1];

        var xs = new List<int>();
        var lengths = new List<int>();
        for (int y = 0; y < height; y++)
        {
            RowStart[y] = xs.Count;
            int row = y * width * 4;
            int x = 0;
            while (x < width)
            {
                if (pixels[row + x * 4 + 3] == 0) { x++; continue; }
                int start = x;
                while (x < width && pixels[row + x * 4 + 3] != 0) x++;
                xs.Add(start);
                lengths.Add(x - start);
            }
        }
        RowStart[height] = xs.Count;
        RunX = [.. xs];
        RunLength = [.. lengths];
    }

    public static Overlay FromPixels(byte[] pixels, int width, int height) => new(width, height, pixels);
}

public static class OverlayRenderer
{
    public static readonly (string Id, string En, string Tr)[] Frames =
    [
        ("none", "None", "Yok"),
        ("minimal", "Minimal", "Zarif"),
        ("polaroid", "Polaroid", "Polaroid"),
        ("film", "Film", "Film"),
        ("neon", "Neon", "Neon"),
        ("viewfinder", "Viewfinder", "Vizör"),
    ];

    public static readonly string[] Accents = ["#FF5A36", "#FFFFFF", "#3B82F6", "#34D399", "#EC4899", "#FACC15"];

    /// <summary>Must run on an STA thread. Returns null when there is nothing to draw.</summary>
    public static Overlay? Render(EffectSettings s, int width, int height)
    {
        bool hasFrame = s.Frame != "none" && !(s.Frame == "custom" && s.CustomFramePath is null);
        bool hasTag = s.ShowNameTag && (s.Name + s.Title).Trim().Length > 0;
        if (!hasFrame && !hasTag) return null;

        double u = height / 100.0; // 1% of the frame height: keeps proportions at any resolution
        var accent = ParseColor(s.Accent);
        var root = new ContainerVisual();
        if (hasFrame) root.Children.Add(DrawFrame(s, width, height, u, accent));
        if (hasTag) root.Children.Add(DrawNameTag(s, width, height, u, accent));

        var target = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        target.Render(root);
        var pixels = new byte[width * height * 4];
        target.CopyPixels(pixels, width * 4, 0);
        return Overlay.FromPixels(pixels, width, height);
    }

    static Visual DrawFrame(EffectSettings s, int w, int h, double u, Color accent)
    {
        var visual = new DrawingVisual();
        using var dc = visual.RenderOpen();
        var full = new Rect(0, 0, w, h);

        switch (s.Frame)
        {
            case "minimal":
                dc.DrawRoundedRectangle(null, new Pen(new SolidColorBrush(accent), Math.Max(2, 0.45 * u)),
                    Deflate(full, 3 * u), 2.5 * u, 2.5 * u);
                break;

            case "polaroid":
            {
                var inner = new Rect(4 * u, 4 * u, w - 8 * u, h - 19 * u);
                var paper = new SolidColorBrush(Color.FromRgb(0xF4, 0xF1, 0xEA));
                dc.DrawGeometry(paper, null, new CombinedGeometry(GeometryCombineMode.Exclude,
                    new RectangleGeometry(full), new RectangleGeometry(inner)));
                dc.DrawRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb(50, 0, 0, 0)), Math.Max(1, 0.2 * u)), inner);

                // Handwritten caption, unless the name tag already shows the name
                if (!s.ShowNameTag && Text(s.Name, "Segoe Print", 5.5 * u, FontWeights.Normal, Color.FromRgb(0x3A, 0x38, 0x34)) is { } caption)
                    dc.DrawText(caption, new Point((w - caption.Width) / 2, inner.Bottom + (15 * u - caption.Height) / 2));
                break;
            }

            case "film":
            {
                double bar = 9 * u;
                var black = new SolidColorBrush(Color.FromRgb(0x0B, 0x0B, 0x0C));
                dc.DrawRectangle(black, null, new Rect(0, 0, w, bar));
                dc.DrawRectangle(black, null, new Rect(0, h - bar, w, bar));

                var hole = new SolidColorBrush(Color.FromArgb(235, 0xEE, 0xEA, 0xE0));
                double holeW = 3.2 * u, holeH = 4.2 * u, pitch = 7.5 * u;
                for (double x = pitch / 2 - holeW / 2; x < w; x += pitch)
                {
                    dc.DrawRoundedRectangle(hole, null, new Rect(x, (bar - holeH) / 2, holeW, holeH), 0.6 * u, 0.6 * u);
                    dc.DrawRoundedRectangle(hole, null, new Rect(x, h - bar + (bar - holeH) / 2, holeW, holeH), 0.6 * u, 0.6 * u);
                }
                break;
            }

            case "neon":
            {
                var tube = Deflate(full, 3.5 * u);
                dc.DrawRoundedRectangle(null, new Pen(new SolidColorBrush(accent), 0.9 * u), tube, 5 * u, 5 * u);
                dc.DrawRoundedRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb(200, 255, 255, 255)), 0.25 * u), tube, 5 * u, 5 * u);
                visual.Effect = new DropShadowEffect { Color = accent, BlurRadius = 4 * u, ShadowDepth = 0, Opacity = 1 };
                break;
            }

            case "viewfinder":
            {
                var pen = new Pen(Brushes.White, Math.Max(2, 0.5 * u)) { StartLineCap = PenLineCap.Square, EndLineCap = PenLineCap.Square };
                double m = 5 * u, l = 9 * u;
                foreach (var (x, y, dx, dy) in new[] { (m, m, 1, 1), (w - m, m, -1, 1), (m, h - m, 1, -1), (w - m, h - m, -1, -1) })
                {
                    dc.DrawLine(pen, new Point(x, y), new Point(x + dx * l, y));
                    dc.DrawLine(pen, new Point(x, y), new Point(x, y + dy * l));
                }

                var thin = new Pen(new SolidColorBrush(Color.FromArgb(180, 255, 255, 255)), Math.Max(1, 0.2 * u));
                dc.DrawLine(thin, new Point(w / 2.0 - 2.5 * u, h / 2.0), new Point(w / 2.0 + 2.5 * u, h / 2.0));
                dc.DrawLine(thin, new Point(w / 2.0, h / 2.0 - 2.5 * u), new Point(w / 2.0, h / 2.0 + 2.5 * u));

                double dot = 1.3 * u;
                dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0xFF, 0x3B, 0x30)), null, new Point(m + 3 * u, m + 4 * u), dot, dot);
                if (Text("REC", "Segoe UI", 3 * u, FontWeights.SemiBold, Colors.White) is { } rec)
                    dc.DrawText(rec, new Point(m + 5.2 * u, m + 4 * u - rec.Height / 2));
                if (Text($"{h}p · 30 fps", "Segoe UI", 2.4 * u, FontWeights.Normal, Color.FromArgb(220, 255, 255, 255)) is { } info)
                    dc.DrawText(info, new Point(w - m - 2.5 * u - info.Width, h - m - 3 * u - info.Height));
                break;
            }

            case "custom":
                if (LoadImage(s.CustomFramePath) is { } image) dc.DrawImage(image, full);
                break;
        }
        return visual;
    }

    static Visual DrawNameTag(EffectSettings s, int w, int h, double u, Color accent)
    {
        var visual = new DrawingVisual();
        using var dc = visual.RenderOpen();

        var name = Text(s.Name, "Segoe UI", 4.4 * u, FontWeights.SemiBold, Colors.White);
        var title = Text(s.Title, "Segoe UI", 2.8 * u, FontWeights.Normal, Color.FromRgb(0xC9, 0xCC, 0xD2));

        double padX = 3 * u, padY = 2 * u, bar = 0.8 * u, gap = name is not null && title is not null ? 0.4 * u : 0;
        double contentW = Math.Max(name?.Width ?? 0, title?.Width ?? 0);
        double contentH = (name?.Height ?? 0) + gap + (title?.Height ?? 0);

        // Sit above whatever the frame puts at the bottom edge
        double bottomEdge = s.Frame switch { "film" => 9 * u, "polaroid" => 15 * u, "viewfinder" => 4 * u, "minimal" or "neon" => 3 * u, _ => 0 };
        double left = s.Frame is "polaroid" or "viewfinder" ? 9 * u : s.Frame is "minimal" or "neon" ? 8 * u : 5 * u;
        var box = new Rect(left, h - bottomEdge - 5 * u - (contentH + 2 * padY), bar + 2 * padX + contentW, contentH + 2 * padY);

        var shape = new RectangleGeometry(box, 1.2 * u, 1.2 * u);
        dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(215, 15, 16, 18)), null, shape);
        dc.PushClip(shape);
        dc.DrawRectangle(new SolidColorBrush(accent), null, new Rect(box.X, box.Y, bar, box.Height));
        dc.Pop();

        double x = box.X + bar + padX, y = box.Y + padY;
        if (name is not null)
        {
            dc.DrawText(name, new Point(x, y));
            y += name.Height + gap;
        }
        if (title is not null) dc.DrawText(title, new Point(x, y));
        return visual;
    }

    static FormattedText? Text(string text, string font, double size, FontWeight weight, Color color)
    {
        text = text.Trim();
        if (text.Length == 0) return null;
        return new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily(font), FontStyles.Normal, weight, FontStretches.Normal),
            Math.Max(1, size), new SolidColorBrush(color), 1.0);
    }

    static BitmapImage? LoadImage(string? path)
    {
        if (path is null || !File.Exists(path)) return null;
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            image.UriSource = new Uri(path);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception e) when (e is IOException or NotSupportedException or FileFormatException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    static Color ParseColor(string value)
    {
        try { return (Color)ColorConverter.ConvertFromString(value); }
        catch (FormatException) { return Color.FromRgb(0xFF, 0x5A, 0x36); }
    }

    static Rect Deflate(Rect r, double by) => new(r.X + by, r.Y + by, Math.Max(0, r.Width - 2 * by), Math.Max(0, r.Height - 2 * by));
}
