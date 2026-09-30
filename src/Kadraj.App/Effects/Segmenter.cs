using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using Kadraj.Video;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Kadraj.Effects;

public enum SegmenterState { Off, Downloading, Loading, Ready, Failed }

/// <summary>A person mask: 255 = person, 0 = background.</summary>
public sealed record PersonMask(byte[] Alpha, int Width, int Height);

/// <summary>
/// Separates the person from the background with Robust Video Matting (RVM, GPL-3.0,
/// downloaded once on first use). RVM is recurrent: it carries state from frame to frame, which
/// keeps arms, hands and worn items such as headsets stable instead of flickering.
/// Inference runs on its own thread; the pipeline submits a frame and briefly waits for its mask.
/// </summary>
public sealed class Segmenter : IDisposable
{
    const string ModelUrl = "https://github.com/PeterL1n/RobustVideoMatting/releases/download/v1.0.0/rvm_mobilenetv3_fp32.onnx";
    const string ModelSha256 = "88d4531297118f595bf2fd60f6f566aec2e559393802d1f436c380f0cbbd2828";
    static readonly string ModelPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kadraj", "models", "rvm_mobilenetv3.onnx");

    readonly object _gate = new();
    readonly AutoResetEvent _frameReady = new(false);
    Task? _starting;
    InferenceSession? _session;

    // Input size and downsample ratio never change within a session: DirectML is fast for the first
    // shape it sees and falls onto a ~3x slower path when shapes change. GPU: 720p input, RVM works
    // at 640x360 inside (~15 ms, keeps dark hair and headsets). CPU: 320x180 inside (~40 ms).
    int _width, _height;
    float _ratio;

    byte[]? _pending;              // RGB at _width x _height, filled by the pipeline thread
    long _pendingSequence;
    byte[]? _spare;
    long _submitted;

    PersonMask? _mask;
    long _maskSequence;
    readonly object _maskGate = new();

    volatile bool _resetRequested;
    volatile bool _disposed;

    public SegmenterState State { get; private set; }
    public int DownloadPercent { get; private set; }
    public bool OnGpu { get; private set; }
    public double InferenceMs { get; private set; }
    public string? Error { get; private set; }

    /// <summary>Raised from background threads whenever <see cref="State"/> or progress changes.</summary>
    public event Action? StatusChanged;

    public PersonMask? LatestMask => Volatile.Read(ref _mask);

    /// <summary>Downloads the model if needed and starts the worker. Safe to call repeatedly.</summary>
    public Task EnsureStartedAsync()
    {
        lock (_gate)
        {
            if (_starting is null || State == SegmenterState.Failed) _starting = Task.Run(StartAsync);
            return _starting;
        }
    }

    async Task StartAsync()
    {
        try
        {
            // v0.4 used MODNet; it isn't needed any more.
            var retired = Path.Combine(Path.GetDirectoryName(ModelPath)!, "modnet.onnx");
            if (File.Exists(retired)) File.Delete(retired);

            if (!File.Exists(ModelPath) || !await HasExpectedHashAsync(ModelPath))
                await DownloadAsync();

            SetState(SegmenterState.Loading);
            (_session, OnGpu) = CreateSession();
            (_width, _height, _ratio) = OnGpu ? (1280, 720, 0.5f) : (512, 288, 0.625f);

            var worker = new Thread(Run) { IsBackground = true, Name = "Kadraj segmenter" };
            worker.Start();
            SetState(SegmenterState.Ready);
        }
        catch (Exception e) when (e is IOException or HttpRequestException or OnnxRuntimeException or InvalidDataException or TaskCanceledException or UnauthorizedAccessException)
        {
            Error = e.Message;
            SetState(SegmenterState.Failed);
        }
    }

    static (InferenceSession, bool) CreateSession()
    {
        // ONNX Runtime's Windows build sends usage telemetry by default; Kadraj sends none.
        OrtEnv.Instance().DisableTelemetryEvents();

        // Troubleshooting switch: KADRAJ_SEGMENTER=cpu skips the GPU (e.g. with a misbehaving driver).
        if (string.Equals(Environment.GetEnvironmentVariable("KADRAJ_SEGMENTER"), "cpu", StringComparison.OrdinalIgnoreCase))
            return (new InferenceSession(ModelPath, new SessionOptions()), false);

        try
        {
            var gpu = new SessionOptions { EnableMemoryPattern = false, ExecutionMode = ExecutionMode.ORT_SEQUENTIAL };
            gpu.AppendExecutionProvider_DML(0);
            return (new InferenceSession(ModelPath, gpu), true);
        }
        catch (OnnxRuntimeException)
        {
            // No DirectX 12 capable GPU (or driver trouble): the CPU is slower but works everywhere.
            return (new InferenceSession(ModelPath, new SessionOptions()), false);
        }
    }

    async Task DownloadAsync()
    {
        SetState(SegmenterState.Downloading);
        Directory.CreateDirectory(Path.GetDirectoryName(ModelPath)!);
        var partial = ModelPath + ".part";

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        using (var response = await http.GetAsync(ModelUrl, HttpCompletionOption.ResponseHeadersRead))
        {
            response.EnsureSuccessStatusCode();
            long total = response.Content.Headers.ContentLength ?? 0;
            await using var source = await response.Content.ReadAsStreamAsync();
            await using var target = File.Create(partial);
            var buffer = new byte[81920];
            long done = 0;
            int read, lastPercent = -1;
            while ((read = await source.ReadAsync(buffer)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read));
                done += read;
                int percent = total > 0 ? (int)(done * 100 / total) : 0;
                if (percent != lastPercent)
                {
                    DownloadPercent = lastPercent = percent;
                    StatusChanged?.Invoke();
                }
            }
        }

        if (!await HasExpectedHashAsync(partial))
        {
            File.Delete(partial);
            throw new InvalidDataException("Downloaded model failed the integrity check.");
        }
        File.Move(partial, ModelPath, overwrite: true);
    }

    static async Task<bool> HasExpectedHashAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream);
        return Convert.ToHexString(hash).Equals(ModelSha256, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Called on the pipeline thread: samples the frame to model size and wakes the worker.
    /// Returns a ticket for <see cref="WaitForMask"/>, or 0 when the model isn't ready.
    /// </summary>
    public unsafe long Submit(VideoFrame frame)
    {
        if (State != SegmenterState.Ready) return 0;

        int w = _width, h = _height;
        var rgb = Interlocked.Exchange(ref _spare, null);
        if (rgb is null || rgb.Length != w * h * 3) rgb = new byte[w * h * 3];

        // A pixel-identical frame repeated for seconds (a frozen phone camera) makes RVM slowly fade
        // a still person into the background; a real sensor's noise never lets that happen. Add a
        // trace of noise to the model's copy only, so a frozen feed behaves like a live one.
        uint seed = (uint)Environment.TickCount | 1;
        fixed (byte* src = frame.Pixels)
        fixed (byte* dst = rgb)
        {
            for (int y = 0; y < h; y++)
            {
                byte* row = src + (long)(y * frame.Height / h) * frame.Stride;
                byte* o = dst + y * w * 3;
                for (int x = 0; x < w; x++)
                {
                    byte* p = row + (x * frame.Width / w) * 4;
                    seed ^= seed << 13; seed ^= seed >> 17; seed ^= seed << 5; // xorshift
                    int noise = (int)(seed & 3) - 2; // -2..1
                    o[0] = (byte)Math.Clamp(p[2] + noise, 0, 255);
                    o[1] = (byte)Math.Clamp(p[1] + noise, 0, 255);
                    o[2] = (byte)Math.Clamp(p[0] + noise, 0, 255);
                    o += 3;
                }
            }
        }

        long sequence;
        lock (_maskGate)
        {
            sequence = ++_submitted;
            _pendingSequence = sequence;
            var replaced = _pending;
            _pending = rgb;
            if (replaced is not null) _spare = replaced; // worker was busy: reuse the older buffer
        }
        _frameReady.Set();
        return sequence;
    }

    /// <summary>
    /// Waits until the mask for <paramref name="ticket"/> (or a newer one) is ready, so the mask
    /// matches the frame it's applied to. Falls back to the newest mask after the timeout.
    /// </summary>
    public PersonMask? WaitForMask(long ticket, int timeoutMs)
    {
        if (ticket == 0) return LatestMask;
        var deadline = Environment.TickCount64 + timeoutMs;
        lock (_maskGate)
        {
            while (_maskSequence < ticket && !_disposed)
            {
                long left = deadline - Environment.TickCount64;
                if (left <= 0 || !Monitor.Wait(_maskGate, (int)left)) break;
            }
            return _mask;
        }
    }

    void Run()
    {
        var watch = new Stopwatch();
        var ratio = new DenseTensor<float>(new[] { _ratio }, [1]);
        DenseTensor<float>[] state = EmptyState();

        while (!_disposed)
        {
            _frameReady.WaitOne();
            if (_disposed) return;

            byte[]? rgb;
            long sequence;
            lock (_maskGate)
            {
                rgb = _pending;
                sequence = _pendingSequence;
                _pending = null;
            }
            if (rgb is null || _session is null) continue;

            if (_resetRequested)
            {
                _resetRequested = false;
                state = EmptyState();
            }

            int w = _width, h = _height, plane = w * h;
            var src = new DenseTensor<float>([1, 3, h, w]);
            var span = src.Buffer.Span;
            for (int i = 0, j = 0; i < plane; i++, j += 3)
            {
                span[i] = rgb[j] / 255f;
                span[plane + i] = rgb[j + 1] / 255f;
                span[2 * plane + i] = rgb[j + 2] / 255f;
            }
            lock (_maskGate) _spare ??= rgb;

            watch.Restart();
            byte[] alpha = new byte[plane];
            try
            {
                // Ask only for what we use: skipping "fgr" saves copying 11 MB back from the GPU per frame.
                using var results = _session.Run(
                [
                    NamedOnnxValue.CreateFromTensor("src", src),
                    NamedOnnxValue.CreateFromTensor("r1i", state[0]),
                    NamedOnnxValue.CreateFromTensor("r2i", state[1]),
                    NamedOnnxValue.CreateFromTensor("r3i", state[2]),
                    NamedOnnxValue.CreateFromTensor("r4i", state[3]),
                    NamedOnnxValue.CreateFromTensor("downsample_ratio", ratio),
                ], OutputNames);

                // Read through the dense buffers: enumerating a Tensor<T> element by element is slow.
                var pha = Dense(results, "pha").Buffer.Span;
                if (pha.Length != plane) continue;
                for (int k = 0; k < plane; k++) alpha[k] = (byte)(Math.Clamp(pha[k], 0f, 1f) * 255f + 0.5f);

                // Recurrent state for the next frame
                state = OutputNames.Skip(1)
                    .Select(name => Dense(results, name))
                    .Select(t => new DenseTensor<float>(t.Buffer.ToArray(), t.Dimensions.ToArray()))
                    .ToArray();
            }
            catch (OnnxRuntimeException e)
            {
                Error = e.Message;
                SetState(SegmenterState.Failed);
                return;
            }
            InferenceMs = watch.Elapsed.TotalMilliseconds;

            lock (_maskGate)
            {
                _mask = new PersonMask(alpha, w, h);
                _maskSequence = sequence;
                Monitor.PulseAll(_maskGate);
            }
        }

        static DenseTensor<float>[] EmptyState() =>
            Enumerable.Range(0, 4).Select(_ => new DenseTensor<float>(new float[1], [1, 1, 1, 1])).ToArray();

        static DenseTensor<float> Dense(IEnumerable<DisposableNamedOnnxValue> results, string name)
        {
            var tensor = results.First(r => r.Name == name).AsTensor<float>();
            return tensor as DenseTensor<float> ?? new DenseTensor<float>(tensor.ToArray(), tensor.Dimensions.ToArray());
        }
    }

    static readonly string[] OutputNames = ["pha", "r1o", "r2o", "r3o", "r4o"];

    /// <summary>Start over (new phone, new scene): drop the recurrent state and the last mask.</summary>
    public void ResetMask()
    {
        _resetRequested = true;
        lock (_maskGate) _mask = null;
    }

    void SetState(SegmenterState state)
    {
        State = state;
        StatusChanged?.Invoke();
    }

    public void Dispose()
    {
        _disposed = true;
        _frameReady.Set();
        lock (_maskGate) Monitor.PulseAll(_maskGate);
    }
}
