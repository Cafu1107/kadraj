using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using Kadraj.Camera;

namespace Kadraj;

public partial class App : Application
{
    Mutex? _singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Elevated helper mode, started by the "Install camera" button.
        if (e.Args is ["--install-camera"])
        {
            Shutdown(VirtualCamera.Install());
            return;
        }
        if (e.Args is ["--uninstall-camera"])
        {
            Shutdown(VirtualCamera.Uninstall());
            return;
        }

        // One Kadraj at a time: a second launch brings the running window forward instead of
        // fighting over the port and the camera feed.
        _singleInstance = new Mutex(true, @"Local\KadrajSingleInstance", out bool first);
        if (!first)
        {
            BringExistingToFront();
            Shutdown();
            return;
        }

        new MainWindow().Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    static void BringExistingToFront()
    {
        var current = Process.GetCurrentProcess();
        foreach (var other in Process.GetProcessesByName(current.ProcessName))
        {
            if (other.Id == current.Id || other.MainWindowHandle == IntPtr.Zero) continue;
            ShowWindow(other.MainWindowHandle, 9 /* SW_RESTORE */);
            SetForegroundWindow(other.MainWindowHandle);
            break;
        }
    }

    [DllImport("user32.dll")]
    static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    static extern bool ShowWindow(IntPtr hwnd, int command);
}
