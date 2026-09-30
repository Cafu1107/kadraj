using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Kadraj.Video;

namespace Kadraj.Effects;

/// <summary>
/// Puts something else behind the person using the segmenter's mask: a blurred copy of the
/// room, a built-in backdrop or the user's own picture. Runs on the pipeline (STA) thread.
/// </summary>
public sealed unsafe class BackgroundReplacer
{
    public static readonly (string Id, string En, string Tr)[] Modes =
    [
        ("none", "Off", "Yok"),
        ("blur", "Blur", "Bulanık"),
        ("studio", "Studio", "Stüdyo"),
        ("sunset", "Sunset", "Gün batımı"),
    ];

    (string Mode, string? Image, int Width, int Height) _backdropKey;
    byte[]? _backdrop;
    readonly BackgroundOnly _blurred = new();
    readonly BackgroundOnly _old = new();

    public void Apply(VideoFrame frame, EffectSettings s, PersonMask mask)
    {
        // Rough colour of the original background everywhere (also behind the person), used to
        // clean the old background out of soft edges.
        var old = _old.Compute(frame, mask, factor: 16, passes: 3);

        if (s.Background == "blur")
        {
            // The blur is built from background pixels only, so the person's colours don't bleed
            // into it as a halo. Cost stays flat at any strength: work on a small copy.
            int factor = (int)Math.Round(6 + s.BlurStrength * 14);
            var blurred = _blurred.Compute(frame, mask, factor, passes: 2);
            Blend(frame, mask, blurred.Pixels, blurred.Width, blurred.Height, old.Pixels, old.Width, old.Height);
        }
        else if (Backdrop(s, frame.Width, frame.Height) is { } backdrop)
        {
            Blend(frame, mask, backdrop, frame.Width, frame.Height, old.Pixels, old.Width, old.Height);
        }
    }

    /// <summary>
    /// A small, smooth picture of the background alone: averages only background pixels (weighted
    /// by 1 - mask) into a grid 1/factor the size of the frame, then spreads them with a normalised
    /// box blur, so cells covered by the person borrow colour from their surroundings.
    /// </summary>
    sealed class BackgroundOnly
    {
        float[] _sums = [], _tmp = [];
        byte[] _pixels = [];

        public (byte[] Pixels, int Width, int Height) Compute(VideoFrame frame, PersonMask mask, int factor, int passes)
        {
            int w = frame.Width, h = frame.Height;
            int gw = Math.Max(4, w / factor), gh = Math.Max(4, h / factor);
            int size = gw * gh * 4;
            if (_sums.Length != size)
            {
                _sums = new float[size];
                _tmp = new float[size];
                _pixels = new byte[size];
            }
            Array.Clear(_sums);

            int mw = mask.Width, mh = mask.Height;
            var alpha = mask.Alpha;
            int step = Math.Max(2, factor / 4); // sample sparsely: the result is blurred anyway
            fixed (byte* src = frame.Pixels)
            {
                for (int y = 0; y < h; y += step)
                {
                    int gy = y * gh / h, my = (y * mh / h) * mw;
                    for (int x = 0; x < w; x += step)
                    {
                        int weight = 255 - alpha[my + x * mw / w];
                        if (weight == 0) continue;
                        byte* p = src + (long)y * frame.Stride + x * 4;
                        int at = (gy * gw + x * gw / w) * 4;
                        _sums[at] += p[0] * weight;
                        _sums[at + 1] += p[1] * weight;
                        _sums[at + 2] += p[2] * weight;
                        _sums[at + 3] += weight;
                    }
                }
            }

            for (int pass = 0; pass < passes; pass++)
            {
                BlurSums(_sums, _tmp, gw, gh, horizontal: true);
                BlurSums(_tmp, _sums, gw, gh, horizontal: false);
            }

            for (int i = 0; i < gw * gh; i++)
            {
                float weight = _sums[i * 4 + 3];
                for (int c = 0; c < 3; c++)
                    _pixels[i * 4 + c] = weight > 0 ? (byte)Math.Clamp(_sums[i * 4 + c] / weight, 0, 255) : (byte)128;
                _pixels[i * 4 + 3] = 255;
            }
            return (_pixels, gw, gh);
        }

        static void BlurSums(float[] from, float[] to, int w, int h, bool horizontal)
        {
            const int radius = 2;
            int lines = horizontal ? h : w, length = horizontal ? w : h;
            for (int line = 0; line < lines; line++)
                for (int i = 0; i < length; i++)
                {
                    float b = 0, g = 0, r = 0, n = 0;
                    for (int k = Math.Max(0, i - radius); k <= Math.Min(length - 1, i + radius); k++)
                    {
                        int at = horizontal ? (line * w + k) * 4 : (k * w + line) * 4;
                        b += from[at]; g += from[at + 1]; r += from[at + 2]; n += from[at + 3];
                    }
                    int o = horizontal ? (line * w + i) * 4 : (i * w + line) * 4;
                    to[o] = b; to[o + 1] = g; to[o + 2] = r; to[o + 3] = n;
                }
        }
    }

    /// <summary>
    /// frame = person * mask + background * (1 - mask). Mask and backgrounds are sampled
    /// bilinearly, so they may be smaller than the frame. On the soft edge the old background's
    /// colour is subtracted first (colour decontamination), so hair doesn't keep a fringe of it.
    /// </summary>
    static void Blend(VideoFrame frame, PersonMask mask, byte[] background, int bw, int bh, byte[] old, int ow, int oh)
    {
        int w = frame.Width, h = frame.Height, stride = frame.Stride;
        int mw = mask.Width, mh = mask.Height;

        // Per-column sample positions (index + 8-bit fraction)
        var mx = Columns(w, mw);
        var bx = Columns(w, bw);
        var ox = Columns(w, ow);

        fixed (byte* fp = frame.Pixels)
        fixed (byte* mp = mask.Alpha)
        fixed (byte* bp = background)
        fixed (byte* op = old)
        {
            nint framePtr = (nint)fp, maskPtr = (nint)mp, bgPtr = (nint)bp, oldPtr = (nint)op;
            Parallel.For(0, h, y =>
            {
                byte* row = (byte*)framePtr + (long)y * stride;
                var (my0, my1, fmy) = Sample(y, h, mh);
                var (by0, by1, fby) = Sample(y, h, bh);
                var (oy0, oy1, foy) = Sample(y, h, oh);
                byte* m0 = (byte*)maskPtr + my0 * mw, m1 = (byte*)maskPtr + my1 * mw;
                byte* b0 = (byte*)bgPtr + (long)by0 * bw * 4, b1 = (byte*)bgPtr + (long)by1 * bw * 4;
                byte* o0 = (byte*)oldPtr + (long)oy0 * ow * 4, o1 = (byte*)oldPtr + (long)oy1 * ow * 4;

                for (int x = 0; x < w; x++)
                {
                    var (mxa, mxb, fmx) = mx[x];
                    int top = m0[mxa] * (256 - fmx) + m0[mxb] * fmx;
                    int bottom = m1[mxa] * (256 - fmx) + m1[mxb] * fmx;
                    int a = (top * (256 - fmy) + bottom * fmy) >> 16;
                    if (a >= 255) continue; // solid person: keep the pixel

                    var (bxa, bxb, fbx) = bx[x];
                    var (oxa, oxb, fox) = ox[x];
                    byte* p = row + x * 4;
                    for (int c = 0; c < 3; c++)
                    {
                        int bg = Bilinear(b0, b1, bxa, bxb, c, fbx, fby);
                        int fg = p[c];
                        if (a >= 128)
                        {
                            // observed = a·person + (1−a)·old  ⇒  person = (observed − (1−a)·old) / a.
                            // Only where a is large (small divisor), and capped: the old-background
                            // guess is rough, and amplified errors look worse than a faint fringe.
                            int oldBg = Bilinear(o0, o1, oxa, oxb, c, fox, foy);
                            int clean = (fg * 255 - oldBg * (255 - a)) / a;
                            fg = Math.Clamp(clean, Math.Max(0, fg - 40), Math.Min(255, fg + 40));
                        }
                        p[c] = (byte)((fg * a + bg * (255 - a) + 127) / 255);
                    }
                }
            });
        }

        static int Bilinear(byte* r0, byte* r1, int xa, int xb, int c, int fx, int fy)
        {
            int t = r0[xa * 4 + c] * (256 - fx) + r0[xb * 4 + c] * fx;
            int b = r1[xa * 4 + c] * (256 - fx) + r1[xb * 4 + c] * fx;
            return (t * (256 - fy) + b * fy) >> 16;
        }

        static (int, int, int)[] Columns(int length, int size)
        {
            var table = new (int, int, int)[length];
            for (int i = 0; i < length; i++) table[i] = Sample(i, length, size);
            return table;
        }

        // Maps output index i (of length) onto a source of `size` samples: two neighbours + weight of the second.
        static (int, int, int) Sample(int i, int length, int size)
        {
            double pos = Math.Max(0, (i + 0.5) * size / length - 0.5);
            int a = Math.Min((int)pos, size - 1);
            return (a, Math.Min(a + 1, size - 1), (int)((pos - a) * 256));
        }
    }

    byte[]? Backdrop(EffectSettings s, int width, int height)
    {
        var key = (s.Background, s.Background == "image" ? s.BackgroundImagePath : null, width, height);
        if (key == _backdropKey) return _backdrop;
        _backdropKey = key;
        _backdrop = RenderBackdrop(s, width, height);
        return _backdrop;
    }

    static byte[]? RenderBackdrop(EffectSettings s, int w, int h)
    {
        var full = new Rect(0, 0, w, h);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            switch (s.Background)
            {
                case "studio":
                    dc.DrawRectangle(new RadialGradientBrush(
                        [new GradientStop(Color.FromRgb(0x4A, 0x50, 0x5A), 0), new GradientStop(Color.FromRgb(0x1D, 0x1F, 0x24), 0.7), new GradientStop(Color.FromRgb(0x0E, 0x0F, 0x11), 1)])
                    { Center = new Point(0.5, 0.42), GradientOrigin = new Point(0.5, 0.42), RadiusX = 0.75, RadiusY = 0.9 }, null, full);
                    break;

                case "sunset":
                    dc.DrawRectangle(new LinearGradientBrush(
                        [new GradientStop(Color.FromRgb(0xFF, 0xB3, 0x6B), 0), new GradientStop(Color.FromRgb(0xFF, 0x6F, 0x7D), 0.5), new GradientStop(Color.FromRgb(0x6C, 0x5C, 0xE7), 1)],
                        new Point(0, 0), new Point(1, 1)), null, full);
                    break;

                case "image":
                    if (LoadImage(s.BackgroundImagePath) is not { } image) return null;
                    // Cover: scale to fill, crop the overflow evenly
                    double scale = Math.Max(w / image.Width, h / image.Height);
                    double iw = image.Width * scale, ih = image.Height * scale;
                    dc.DrawImage(image, new Rect((w - iw) / 2, (h - ih) / 2, iw, ih));
                    break;

                default:
                    return null;
            }
        }

        var target = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        var pixels = new byte[w * h * 4];
        target.CopyPixels(pixels, w * 4, 0);
        return pixels;
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
}
