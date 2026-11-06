using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Setpiece;

internal readonly struct Rect
{
    public readonly int Left, Top, Right, Bottom;

    public Rect(int left, int top, int right, int bottom)
    {
        Left = left;
        Top = top;
        Right = right;
        Bottom = bottom;
    }

    public int Width => Right - Left;
    public int Height => Bottom - Top;

    public override string ToString() => $"({Left},{Top})-({Right},{Bottom}) {Width}x{Height}";

    public static Rect FromNormalized(double[] n, Rect work) => new(
        work.Left + (int)Math.Round(n[0] * work.Width),
        work.Top + (int)Math.Round(n[1] * work.Height),
        work.Left + (int)Math.Round(n[2] * work.Width),
        work.Top + (int)Math.Round(n[3] * work.Height));

    public double[] Normalized(Rect work) => new[]
    {
        Math.Round((double)(Left - work.Left) / work.Width, 4),
        Math.Round((double)(Top - work.Top) / work.Height, 4),
        Math.Round((double)(Right - work.Left) / work.Width, 4),
        Math.Round((double)(Bottom - work.Top) / work.Height, 4),
    };
}

internal sealed class MonitorInfo
{
    public IntPtr Handle;
    public Rect Bounds;
    public Rect WorkArea;
    public bool Primary;
    public int Index;
    public override string ToString() =>
        $"#{Index} {(Primary ? "primary" : "secondary")} bounds={Bounds} work={WorkArea}";
}

internal sealed class WindowInfo
{
    public IntPtr Handle;
    public int Pid;
    public string Title = "";
    public string Class = "";
    public string Exe = "";
    public override string ToString() =>
        $"0x{Handle.ToInt64():X} pid={Pid} {Path.GetFileName(Exe)} \"{Title}\"";
}

internal static class Native
{
    public const int SW_SHOWNORMAL = 1;
    public const int SW_MAXIMIZE = 3;
    public const int SW_RESTORE = 9;
    public const uint WM_CLOSE = 0x0010;
    public const uint GW_OWNER = 4;
    public const int GWL_EXSTYLE = -20;
    public const long WS_EX_TOOLWINDOW = 0x00000080L;
    public const long WS_EX_APPWINDOW = 0x00040000L;
    public const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    public const int DWMWA_CLOAKED = 14;
    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    public const uint MONITOR_DEFAULTTONEAREST = 2;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct WINDOWPLACEMENT
    {
        public int length;
        public int flags;
        public int showCmd;
        public POINT ptMinPosition;
        public POINT ptMaxPosition;
        public RECT rcNormalPosition;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT lprcMonitor, IntPtr dwData);

    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr hWnd, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hWnd, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] public static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int cmd);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool SetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT wp);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool GetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT wp);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hWnd, int attr, out RECT r, int size);
    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")] public static extern int DwmGetWindowAttributeInt(IntPtr hWnd, int attr, out int value, int size);
    [DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO mi);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc cb, IntPtr data);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] public static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder s, ref int size);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool CloseHandle(IntPtr h);
    [DllImport("advapi32.dll", SetLastError = true)] public static extern bool OpenProcessToken(IntPtr h, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)] public static extern bool GetTokenInformation(IntPtr token, int cls, IntPtr info, int len, out int ret);
    [DllImport("kernel32.dll")] public static extern IntPtr GetCurrentProcess();

    public static List<MonitorInfo> Screens()
    {
        var list = new List<MonitorInfo>();

        bool Callback(IntPtr hMon, IntPtr hdc, ref RECT r, IntPtr data)
        {
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(hMon, ref mi))
            {
                list.Add(new MonitorInfo
                {
                    Handle = hMon,
                    Bounds = new Rect(mi.rcMonitor.Left, mi.rcMonitor.Top, mi.rcMonitor.Right, mi.rcMonitor.Bottom),
                    WorkArea = new Rect(mi.rcWork.Left, mi.rcWork.Top, mi.rcWork.Right, mi.rcWork.Bottom),
                    Primary = (mi.dwFlags & 1) != 0,
                    Index = list.Count,
                });
            }
            return true;
        }

        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, Callback, IntPtr.Zero);
        return list;
    }
}

internal static class WindowScanner
{
    private static readonly Dictionary<int, string> ExeCache = new();

    public static List<WindowInfo> Scan()
    {
        var list = new List<WindowInfo>();
        Native.EnumWindows((h, _) =>
        {
            if (!Native.IsWindowVisible(h)) return true;
            if (Native.GetWindowTextLength(h) == 0) return true;
            if (IsCloaked(h)) return true;

            long ex = Native.GetWindowLongPtr(h, Native.GWL_EXSTYLE).ToInt64();
            if ((ex & Native.WS_EX_TOOLWINDOW) != 0) return true;
            if (Native.GetWindow(h, Native.GW_OWNER) != IntPtr.Zero && (ex & Native.WS_EX_APPWINDOW) == 0) return true;

            var title = new StringBuilder(512);
            Native.GetWindowText(h, title, title.Capacity);
            var cls = new StringBuilder(256);
            Native.GetClassName(h, cls, cls.Capacity);
            Native.GetWindowThreadProcessId(h, out uint pid);

            list.Add(new WindowInfo
            {
                Handle = h,
                Pid = (int)pid,
                Title = title.ToString(),
                Class = cls.ToString(),
                Exe = ExePath((int)pid),
            });
            return true;
        }, IntPtr.Zero);
        return list;
    }

    public static HashSet<IntPtr> Handles() => Scan().Select(w => w.Handle).ToHashSet();

    public static string ExePathOf(int pid) => ExePath(pid);

    public static WindowInfo Refresh(WindowInfo window)
    {
        var sb = new StringBuilder(512);
        Native.GetWindowText(window.Handle, sb, sb.Capacity);
        window.Title = sb.ToString();
        return window;
    }

    public static WindowInfo? WaitNew(HashSet<IntPtr> before, Func<WindowInfo, bool> pred, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            var hit = Scan().FirstOrDefault(w => !before.Contains(w.Handle) && pred(w));
            if (hit is not null) return hit;
            Thread.Sleep(250);
        }
        return null;
    }

    public static Rect Frame(IntPtr hwnd)
    {
        if (Native.DwmGetWindowAttribute(hwnd, Native.DWMWA_EXTENDED_FRAME_BOUNDS,
                out Native.RECT r, Marshal.SizeOf<Native.RECT>()) == 0
            && r.Right > r.Left && r.Bottom > r.Top)
            return new Rect(r.Left, r.Top, r.Right, r.Bottom);

        Native.GetWindowRect(hwnd, out r);
        return new Rect(r.Left, r.Top, r.Right, r.Bottom);
    }

    public static string LastPlaceError = "";

    // 不依赖对边框内缩的预判：每次量出实际可见边框与目标的误差，
    // 把窗口矩形反向补偿后重设，通常 1~2 次收敛。
    public static bool Place(IntPtr hwnd, Rect targetFrame, out Rect actual)
    {
        EnsureNormal(hwnd);
        LastPlaceError = "";

        for (int attempt = 0; attempt < 4; attempt++)
        {
            Native.GetWindowRect(hwnd, out Native.RECT w);
            actual = Frame(hwnd);
            if (Approx(actual, targetFrame)) return true;

            int dl = actual.Left - targetFrame.Left;
            int dt = actual.Top - targetFrame.Top;
            int dr = actual.Right - targetFrame.Right;
            int db = actual.Bottom - targetFrame.Bottom;

            int left = w.Left - dl;
            int top = w.Top - dt;
            int right = w.Right - dr;
            int bottom = w.Bottom - db;

            bool ok = Native.SetWindowPos(hwnd, IntPtr.Zero, left, top, right - left, bottom - top,
                Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
            LastPlaceError = $"SetWindowPos ok={ok} err={Marshal.GetLastWin32Error()}";
            Thread.Sleep(160);
        }

        actual = Frame(hwnd);
        return Approx(actual, targetFrame);
    }

    // Electron / Office 的还原是异步的，必须等到真正退出最小化/最大化再采样。
    private static void EnsureNormal(IntPtr hwnd)
    {
        if (!Native.IsIconic(hwnd) && !Native.IsZoomed(hwnd)) return;

        Native.ShowWindow(hwnd, Native.SW_RESTORE);
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 1500 && (Native.IsIconic(hwnd) || Native.IsZoomed(hwnd)))
            Thread.Sleep(100);
        Thread.Sleep(200);
    }

    public static void Maximize(IntPtr hwnd) => Native.ShowWindow(hwnd, Native.SW_MAXIMIZE);

    public static void Close(IntPtr hwnd) => Native.PostMessage(hwnd, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);

    public static MonitorInfo ScreenOf(IntPtr hwnd)
    {
        IntPtr h = Native.MonitorFromWindow(hwnd, Native.MONITOR_DEFAULTTONEAREST);
        var screens = Native.Screens();
        return screens.FirstOrDefault(m => m.Handle == h) ?? screens[0];
    }

    public static string IntegrityLevel()
    {
        if (!Native.OpenProcessToken(Native.GetCurrentProcess(), 0x0008, out IntPtr token)) return "?";
        try
        {
            IntPtr buf = Marshal.AllocHGlobal(256);
            try
            {
                if (!Native.GetTokenInformation(token, 25, buf, 256, out _)) return "?";
                IntPtr sid = Marshal.ReadIntPtr(buf);
                byte count = Marshal.ReadByte(sid, 1);
                int rid = Marshal.ReadInt32(sid, 8 + 4 * (count - 1));
                return rid switch
                {
                    0x0000 => "Untrusted",
                    0x1000 => "Low",
                    0x2000 => "Medium",
                    0x3000 => "High",
                    0x4000 => "System",
                    _ => $"0x{rid:X}",
                };
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }
        finally
        {
            Native.CloseHandle(token);
        }
    }

    private static bool IsCloaked(IntPtr h) =>
        Native.DwmGetWindowAttributeInt(h, Native.DWMWA_CLOAKED, out int v, sizeof(int)) == 0 && v != 0;

    private static string ExePath(int pid)
    {
        if (ExeCache.TryGetValue(pid, out string? cached)) return cached;
        string path = "";
        IntPtr h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (h != IntPtr.Zero)
        {
            try
            {
                var sb = new StringBuilder(1024);
                int size = sb.Capacity;
                if (Native.QueryFullProcessImageName(h, 0, sb, ref size)) path = sb.ToString(0, size);
            }
            finally
            {
                Native.CloseHandle(h);
            }
        }
        ExeCache[pid] = path;
        return path;
    }

    private static bool Approx(Rect a, Rect b) =>
        Math.Abs(a.Left - b.Left) <= 2 && Math.Abs(a.Top - b.Top) <= 2 &&
        Math.Abs(a.Right - b.Right) <= 2 && Math.Abs(a.Bottom - b.Bottom) <= 2;
}
