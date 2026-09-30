using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32;

namespace Kadraj.Camera;

public enum CameraSetup
{
    Unsupported,   // needs Windows 11
    Missing,       // KadrajCamera.dll not next to the app (dev build without the C++ project)
    NotInstalled,
    NeedsUpdate,
    Installed,
}

/// <summary>
/// The "Kadraj" entry in the system camera list. Installing copies KadrajCamera.dll to
/// Program Files and registers it (admin, once) — the Frame Server service loads it from there.
/// While the app runs, the camera is added for the current user and removed on exit.
/// </summary>
public sealed partial class VirtualCamera : IDisposable
{
    const string Clsid = "{69570d99-37dc-4406-9a8c-cb2d1438ea3a}";
    const string DllName = "KadrajCamera.dll";

    IntPtr _handle;

    static string BundledDll => Path.Combine(AppContext.BaseDirectory, DllName);
    static string InstallFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Kadraj");
    static string InstalledDll => Path.Combine(InstallFolder, DllName);

    [LibraryImport(DllName)]
    private static partial int KadrajCamera_IsSupported(out int supported);

    [LibraryImport(DllName, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int KadrajCamera_Create(string friendlyName, out IntPtr camera);

    [LibraryImport(DllName)]
    private static partial int KadrajCamera_Remove(IntPtr camera);

    public static CameraSetup CheckSetup()
    {
        if (!File.Exists(BundledDll)) return CameraSetup.Missing;
        if (KadrajCamera_IsSupported(out var supported) < 0 || supported == 0) return CameraSetup.Unsupported;

        using var key = Registry.LocalMachine.OpenSubKey($@"SOFTWARE\Classes\CLSID\{Clsid}\InprocServer32");
        if (key?.GetValue(null) is not string registered || !File.Exists(registered)) return CameraSetup.NotInstalled;
        return SameContent(registered, BundledDll) ? CameraSetup.Installed : CameraSetup.NeedsUpdate;
    }

    /// <summary>Adds the camera to the system list. Returns an HRESULT (0 = success).</summary>
    public int Start()
    {
        if (_handle != IntPtr.Zero) return 0;
        return KadrajCamera_Create("Kadraj", out _handle);
    }

    public void Dispose()
    {
        if (_handle == IntPtr.Zero) return;
        KadrajCamera_Remove(_handle);
        _handle = IntPtr.Zero;
    }

    /// <summary>Runs the install step elevated (UAC prompt). False if it failed or the user declined.</summary>
    public static Task<bool> InstallElevatedAsync() => Task.Run(() =>
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!, "--install-camera") { UseShellExecute = true, Verb = "runas" };
        try
        {
            using var process = Process.Start(start)!;
            process.WaitForExit();
            return process.ExitCode == 0;
        }
        catch (Win32Exception)
        {
            return false; // UAC declined
        }
    });

    /// <summary>The elevated half of <see cref="InstallElevatedAsync"/>. Returns a process exit code.</summary>
    public static int Install()
    {
        Directory.CreateDirectory(InstallFolder);
        foreach (var stale in Directory.GetFiles(InstallFolder, "*.old"))
            TryDelete(stale);

        if (File.Exists(InstalledDll))
        {
            // Frame Server may still have the old copy loaded: a loaded DLL can be renamed, not overwritten.
            var aside = $"{InstalledDll}.{Guid.NewGuid():N}.old";
            File.Move(InstalledDll, aside);
            TryDelete(aside);
        }
        File.Copy(BundledDll, InstalledDll);
        int code = RunSystemTool("regsvr32.exe", $"/s \"{InstalledDll}\"");

        // Frame Server keeps an already loaded copy until it restarts; it starts again on demand,
        // so stopping it makes the next camera use load the new version.
        RunSystemTool("sc.exe", "stop FrameServer");
        return code;
    }

    public static int Uninstall()
    {
        if (!File.Exists(InstalledDll)) return 0;
        int code = RunSystemTool("regsvr32.exe", $"/u /s \"{InstalledDll}\"");
        TryDelete(InstalledDll);
        return code;
    }

    /// <summary>
    /// Runs a tool from System32 by full path. This code runs elevated: a bare "regsvr32.exe" would
    /// be looked up in the app's folder first, where anyone able to write there could plant their own.
    /// </summary>
    static int RunSystemTool(string exe, string arguments)
    {
        var path = Path.Combine(Environment.SystemDirectory, exe);
        using var process = Process.Start(new ProcessStartInfo(path, arguments) { UseShellExecute = false, CreateNoWindow = true })!;
        process.WaitForExit();
        return process.ExitCode;
    }

    static bool SameContent(string a, string b)
    {
        try
        {
            return SHA256.HashData(File.ReadAllBytes(a)).AsSpan().SequenceEqual(SHA256.HashData(File.ReadAllBytes(b)));
        }
        catch (IOException)
        {
            return false;
        }
    }

    static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
