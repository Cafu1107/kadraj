using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Kadraj.Camera;
using Kadraj.Effects;

namespace Kadraj.Video;

/// <summary>A BGRA frame (top-down, stride = width * 4).</summary>
public sealed class VideoFrame
{
    public int Width { get; private set; }
    public int Height { get; private set; }
    public int Stride => Width * 4;
    public byte[] Pixels { get; private set; } = [];

    public void Resize(int width, int height)
    {
        if (width == Width && height == Height) return;
        Width = width;
        Height = height;
        Pixels = new byte[width * height * 4];
    }
}

/// <summary>
/// Decodes incoming JPEGs on a dedicated thread, composes effects at the output size, and sends
/// the result to the virtual camera and (when the UI is free) the preview. Always works on the
/// newest frame: anything that arrives while busy replaces the waiting one, so a slow step drops
/// frames instead of adding delay.
/// </summary>
public sealed class FramePump : IDisposable
{
    /// <summary>Output size while no app is using the camera (the preview still needs one).</summary>
    const int DefaultWidth = 1280, DefaultHeight = 720;
    const int MaxInputSide = 4096; // phones send 1080p at most

    readonly Dispatcher _ui;
    readonly Compositor _compositor;
    readonly SharedFrameWriter _output;
    readonly Action<VideoFrame> _show;
    readonly AutoResetEvent _frameArrived = new(false);
    readonly ManualResetEventSlim _uiIdle = new(true);
    byte[]? _pending;
    volatile bool _disposed;
    int _received;
    int _shown;

    /// <param name="show">Runs on the UI thread with the composed frame; copy it before returning.</param>
    public FramePump(Dispatcher ui, Compositor compositor, SharedFrameWriter output, Action<VideoFrame> show)
    {
        _ui = ui;
        _compositor = compositor;
        _output = output;
        _show = show;
        // STA: WIC decoding and WPF overlay rendering happen on this thread.
        var thread = new Thread(Run) { IsBackground = true, Name = "Kadraj frame pipeline" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    public void Submit(byte[] jpeg)
    {
        Interlocked.Increment(ref _received);
        Volatile.Write(ref _pending, jpeg);
        _frameArrived.Set();
    }

    /// <summary>Frames received and shown since the last call.</summary>
    public (int Received, int Shown) TakeCounts() =>
        (Interlocked.Exchange(ref _received, 0), Interlocked.Exchange(ref _shown, 0));

    void Run()
    {
        while (true)
        {
            _frameArrived.WaitOne();
            if (_disposed) return;

            var jpeg = Interlocked.Exchange(ref _pending, null);
            if (jpeg is null || Decode(jpeg) is not { } source) continue;

            var (width, height) = _output.RequestedSize ?? (DefaultWidth, DefaultHeight);
            var frame = _compositor.Compose(source, width, height);
            _output.Write(frame);

            if (!_uiIdle.IsSet) continue; // the UI is still drawing the previous preview frame
            _compositor.Flip();           // the UI keeps this buffer; compose the next one elsewhere
            _uiIdle.Reset();
            _ui.BeginInvoke(() =>
            {
                try
                {
                    _show(frame);
                    Interlocked.Increment(ref _shown);
                }
                finally
                {
                    _uiIdle.Set();
                }
            });
        }
    }

    static BitmapSource? Decode(byte[] jpeg)
    {
        try
        {
            // JPEG only (no format sniffing), and the size is checked from the header before any
            // pixels are decoded: a tiny file claiming a huge image can't exhaust memory.
            using var stream = new MemoryStream(jpeg, writable: false);
            var decoder = new JpegBitmapDecoder(stream,
                BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile,
                BitmapCacheOption.None);

            BitmapSource source = decoder.Frames[0];
            if (source.PixelWidth > MaxInputSide || source.PixelHeight > MaxInputSide) return null;
            if (source.Format != PixelFormats.Bgra32)
                source = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            // Decode now, while the stream is still open.
            return new CachedBitmap(source, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        }
        catch (Exception e) when (e is NotSupportedException or FileFormatException or ArgumentException or IOException)
        {
            return null; // truncated or corrupt frame; the next one will do
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _uiIdle.Set();
        _frameArrived.Set();
    }
}
