using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Kadraj.Video;

namespace Kadraj.Effects;

/// <summary>
/// Builds the final picture: fit/crop the phone image to the output size, background, mirror,
/// colour filter, then the frame and name tag on top. Runs on the decoder thread; settings arrive from the UI
/// as a whole new record, so a change never shows up half-applied.
/// </summary>
public sealed unsafe class Compositor
{
    readonly VideoFrame[] _buffers = [new(), new()];
    readonly Segmenter _segmenter;
    readonly BackgroundReplacer _background = new();
    int _back;

    volatile EffectSettings _settings;
    EffectSettings? _filterFor;
    ColorFilter _filter;
    EffectSettings? _overlayFor;
    Overlay? _overlay;

    public Compositor(EffectSettings settings, Segmenter segmenter)
    {
        _settings = settings;
        _segmenter = segmenter;
        _filter = ColorFilter.Create(settings);
    }

    public EffectSettings Settings
    {
        get => _settings;
        set => _settings = value;
    }

    /// <summary>Composes into the back buffer. Call <see cref="Flip"/> after handing it to someone who keeps it.</summary>
    public VideoFrame Compose(BitmapSource source, int width, int height)
    {
        var s = _settings;
        if (!ReferenceEquals(s, _filterFor))
        {
            _filter = ColorFilter.Create(s);
            _filterFor = s;
        }
        if (!ReferenceEquals(s, _overlayFor) || _overlay is { } o && (o.Width != width || o.Height != height))
        {
            _overlay = OverlayRenderer.Render(s, width, height);
            _overlayFor = s;
        }

        var frame = _buffers[_back];
        frame.Resize(width, height);
        Place(source, frame, s.Fill);

        // Background first, on the raw image the mask was computed from; filters then treat
        // person and new background alike, so they look like one picture.
        if (s.Background != "none")
        {
            long ticket = _segmenter.Submit(frame);
            // On a GPU the mask for this very frame is ready within ~15 ms: wait for it so the cut-out
            // lines up during movement. On a CPU keep the frame rate and use the newest mask instead.
            var mask = _segmenter.OnGpu ? _segmenter.WaitForMask(ticket, 30) : _segmenter.LatestMask;
            if (mask is not null) _background.Apply(frame, s, mask);
        }

        Process(frame, _filter, s.Mirror, _overlay);
        return frame;
    }

    public void Flip() => _back ^= 1;

    static void Place(BitmapSource source, VideoFrame frame, bool fill)
    {
        int w = frame.Width, h = frame.Height;
        double sx = (double)w / source.PixelWidth, sy = (double)h / source.PixelHeight;
        double scale = fill ? Math.Max(sx, sy) : Math.Min(sx, sy);
        BitmapSource scaled = Math.Abs(scale - 1) < 0.001 ? source : new TransformedBitmap(source, new ScaleTransform(scale, scale));

        int cw = Math.Min(w, scaled.PixelWidth), ch = Math.Min(h, scaled.PixelHeight);
        int cx = (scaled.PixelWidth - cw) / 2, cy = (scaled.PixelHeight - ch) / 2;
        int dx = (w - cw) / 2, dy = (h - ch) / 2;

        if (cw < w || ch < h)
            MemoryMarshal.Cast<byte, uint>(frame.Pixels.AsSpan()).Fill(0xFF000000); // opaque black bars

        int offset = dy * frame.Stride + dx * 4;
        fixed (byte* p = frame.Pixels)
            scaled.CopyPixels(new Int32Rect(cx, cy, cw, ch), (IntPtr)(p + offset), frame.Pixels.Length - offset, frame.Stride);
    }

    static void Process(VideoFrame frame, ColorFilter filter, bool mirror, Overlay? overlay)
    {
        if (!mirror && filter.IsIdentity && overlay is null) return;

        int w = frame.Width, h = frame.Height, stride = frame.Stride, sat = filter.Saturation;
        bool colour = !filter.IsIdentity, saturate = sat != 256;

        fixed (byte* basePtr = frame.Pixels)
        fixed (byte* lr = filter.R, lg = filter.G, lb = filter.B)
        fixed (byte* op = overlay?.Pixels)
        {
            nint pixels = (nint)basePtr, r = (nint)lr, g = (nint)lg, b = (nint)lb, ov = (nint)op;

            // Rows are independent: split them across cores.
            Parallel.For(0, Math.Min(Environment.ProcessorCount, 8), band =>
            {
                int bands = Math.Min(Environment.ProcessorCount, 8);
                int y0 = h * band / bands, y1 = h * (band + 1) / bands;
                byte* R = (byte*)r, G = (byte*)g, B = (byte*)b, O = (byte*)ov;

                for (int y = y0; y < y1; y++)
                {
                    byte* row = (byte*)pixels + (long)y * stride;
                    if (mirror) new Span<uint>(row, w).Reverse();

                    if (colour)
                    {
                        for (int i = 0; i < w * 4; i += 4)
                        {
                            int cb = B[row[i]], cg = G[row[i + 1]], cr = R[row[i + 2]];
                            if (saturate)
                            {
                                int l = (19 * cb + 183 * cg + 54 * cr) >> 8;
                                cb = Math.Clamp(l + (((cb - l) * sat) >> 8), 0, 255);
                                cg = Math.Clamp(l + (((cg - l) * sat) >> 8), 0, 255);
                                cr = Math.Clamp(l + (((cr - l) * sat) >> 8), 0, 255);
                            }
                            row[i] = (byte)cb;
                            row[i + 1] = (byte)cg;
                            row[i + 2] = (byte)cr;
                        }
                    }

                    if (overlay is null) continue;
                    byte* orow = O + (long)y * stride;
                    for (int run = overlay.RowStart[y]; run < overlay.RowStart[y + 1]; run++)
                    {
                        int end = (overlay.RunX[run] + overlay.RunLength[run]) * 4;
                        for (int i = overlay.RunX[run] * 4; i < end; i += 4)
                        {
                            int a = orow[i + 3];
                            if (a == 255)
                            {
                                row[i] = orow[i];
                                row[i + 1] = orow[i + 1];
                                row[i + 2] = orow[i + 2];
                            }
                            else
                            {
                                int inv = 255 - a; // premultiplied "over"
                                row[i] = (byte)(orow[i] + (row[i] * inv + 127) / 255);
                                row[i + 1] = (byte)(orow[i + 1] + (row[i + 1] * inv + 127) / 255);
                                row[i + 2] = (byte)(orow[i + 2] + (row[i + 2] * inv + 127) / 255);
                            }
                        }
                    }
                }
            });
        }
    }
}
