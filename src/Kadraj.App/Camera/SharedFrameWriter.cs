using System.Runtime.InteropServices;
using System.Security.Principal;
using Kadraj.Video;
using Microsoft.Win32.SafeHandles;

namespace Kadraj.Camera;

/// <summary>
/// Hands frames to the virtual camera (running in the Frame Server service) through shared memory.
/// The app creates the section in its own session and grants access only to the current user,
/// SYSTEM and LocalService (Frame Server's account): other users and sandboxed processes can
/// neither watch the camera feed nor inject frames. Layout mirrors src/Kadraj.Camera/Shared.h.
/// </summary>
public sealed unsafe partial class SharedFrameWriter : IDisposable
{
    const string Name = @"Local\KadrajCameraFrames";
    const uint Magic = 0x4A52444B;
    const uint Version = 2;
    const int MaxWidth = 1920, MaxHeight = 1080;
    const int HeaderSize = 4096;
    const long Size = HeaderSize + (long)MaxWidth * MaxHeight * 4;

    // Header offsets
    const int MagicOffset = 0, VersionOffset = 4;
    const int RequestedWidth = 8, RequestedHeight = 12, ConsumerTick = 16;
    const int ProducerTick = 24, Sequence = 32, Width = 36, Height = 40;

    readonly object _gate = new(); // UI thread polls status while the pipeline thread writes
    readonly SafeFileHandle _mapping;
    byte* _base;

    /// <summary>
    /// Creates the section. If that fails, the writer stays inert (the virtual camera shows its
    /// "waiting" card) rather than taking the whole app down; <see cref="IsAvailable"/> tells.
    /// </summary>
    public SharedFrameWriter()
    {
        var user = WindowsIdentity.GetCurrent().User!.Value;
        // Protected DACL: full access for SYSTEM, LocalService and this user only.
        var sddl = $"D:P(A;;GA;;;SY)(A;;GA;;;LS)(A;;GA;;;{user})";
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl, 1, out var descriptor, IntPtr.Zero))
        {
            _mapping = new SafeFileHandle(IntPtr.Zero, ownsHandle: false);
            return;
        }
        try
        {
            var attributes = new SECURITY_ATTRIBUTES { nLength = sizeof(SECURITY_ATTRIBUTES), lpSecurityDescriptor = descriptor };
            _mapping = CreateFileMappingW(new IntPtr(-1), ref attributes, PAGE_READWRITE,
                (uint)(Size >> 32), (uint)Size, Name);
        }
        finally
        {
            LocalFree(descriptor);
        }
        if (_mapping.IsInvalid) return;

        _base = (byte*)MapViewOfFile(_mapping, FILE_MAP_WRITE | FILE_MAP_READ, 0, 0, (nuint)Size);
        if (_base == null) return;
        *(uint*)(_base + VersionOffset) = Version;
        *(uint*)(_base + MagicOffset) = Magic;
    }

    public bool IsAvailable => _base != null;

    /// <summary>The size the app using the camera asked for, or null while nobody is watching.</summary>
    public (int Width, int Height)? RequestedSize
    {
        get
        {
            lock (_gate) return ReadRequestedSize();
        }
    }

    (int Width, int Height)? ReadRequestedSize()
    {
        if (_base == null) return null;
        int w = Volatile.Read(ref *(int*)(_base + RequestedWidth));
        int h = Volatile.Read(ref *(int*)(_base + RequestedHeight));
        long tick = Volatile.Read(ref *(long*)(_base + ConsumerTick));
        bool fresh = Environment.TickCount64 - tick < 2000;
        return fresh && w > 0 && h > 0 && w <= MaxWidth && h <= MaxHeight ? (w, h) : null;
    }

    /// <summary>Publishes a frame composed at <see cref="RequestedSize"/>.</summary>
    public void Write(VideoFrame frame)
    {
        lock (_gate)
        {
            // Nobody watching, or the frame was composed for a size the consumer just changed away from.
            if (_base == null || ReadRequestedSize() != (frame.Width, frame.Height)) return;

            ref int sequence = ref *(int*)(_base + Sequence);
            Interlocked.Increment(ref sequence); // odd: writing
            frame.Pixels.AsSpan().CopyTo(new Span<byte>(_base + HeaderSize, frame.Pixels.Length));
            *(int*)(_base + Width) = frame.Width;
            *(int*)(_base + Height) = frame.Height;
            Interlocked.Increment(ref sequence); // even: done
            Volatile.Write(ref *(long*)(_base + ProducerTick), Environment.TickCount64);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_base != null) UnmapViewOfFile(_base);
            _base = null;
            _mapping.Dispose();
        }
    }

    // Win32 ---------------------------------------------------------------------

    const uint PAGE_READWRITE = 0x04;
    const uint FILE_MAP_WRITE = 0x02, FILE_MAP_READ = 0x04;

    [StructLayout(LayoutKind.Sequential)]
    struct SECURITY_ATTRIBUTES
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        public int bInheritHandle;
    }

    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ConvertStringSecurityDescriptorToSecurityDescriptorW(
        string sddl, uint revision, out IntPtr descriptor, IntPtr size);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr LocalFree(IntPtr memory);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFileMappingW(IntPtr file, ref SECURITY_ATTRIBUTES attributes,
        uint protect, uint sizeHigh, uint sizeLow, string name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial void* MapViewOfFile(SafeFileHandle mapping, uint access, uint offsetHigh, uint offsetLow, nuint bytes);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnmapViewOfFile(void* address);
}
