using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;

namespace Setpiece.App;

public partial class App : System.Windows.Application
{
    private static Mutex? _instanceMutex;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? className, string? windowName);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    protected override void OnStartup(StartupEventArgs e)
    {
        _instanceMutex = new Mutex(true, @"Local\Setpiece.SingleInstance", out bool created);
        if (!created)
        {
            // 已经有一个实例在跑：把它的窗口叫到前台，自己退出。
            IntPtr existing = FindWindow(null, "Setpiece");
            if (existing != IntPtr.Zero)
            {
                ShowWindow(existing, 9 /* SW_RESTORE */);
                SetForegroundWindow(existing);
            }
            Shutdown();
            return;
        }

        base.OnStartup(e);
    }
}
