using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;

namespace Setpiece.App;

public partial class App : System.Windows.Application
{
    private static Mutex? _instanceMutex;

    public static string? StartupProject { get; private set; }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int max);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    protected override void OnStartup(StartupEventArgs e)
    {
        // 通过 dotnet 宿主启动时（setpiece-ui.cmd / 开发调试），进程清单里的
        // PerMonitorV2 不会生效，混合 DPI 下窗口坐标会被系统缩放。这里显式设置。
        Native.SetProcessDpiAwarenessContext(new IntPtr(-4));

        StartupProject = e.Args.FirstOrDefault(a => !a.StartsWith("-") && Directory.Exists(a));

        _instanceMutex = new Mutex(true, @"Local\Setpiece.SingleInstance", out bool created);
        if (!created)
        {
            // 已经有一个实例在跑：把它的窗口叫到前台，自己退出。
            // 窗口标题可能是 "Setpiece" 或 "Setpiece — 项目名"，按前缀找。
            IntPtr existing = FindMainWindow();
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

    private static IntPtr FindMainWindow()
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows((hWnd, _) =>
        {
            var title = new StringBuilder(256);
            GetWindowText(hWnd, title, title.Capacity);
            if (!title.ToString().StartsWith("Setpiece", StringComparison.Ordinal)) return true;

            found = hWnd;
            return false;
        }, IntPtr.Zero);
        return found;
    }
}
