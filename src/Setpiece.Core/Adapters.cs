using System.Diagnostics;
using System.Text;

namespace Setpiece;

internal static class Runtime
{
    public static string? VsCodeProfile;
    public static Dictionary<string, string> Viewers = new(StringComparer.OrdinalIgnoreCase);
    public static WindowCache? Cache;

    public static void Configure(SetpieceConfig cfg)
    {
        Cache = WindowCache.Load();
        VsCodeProfile = string.IsNullOrWhiteSpace(cfg.VscodeProfile)
            ? Environment.GetEnvironmentVariable("WORKBENCH_VSCODE_PROFILE")
            : cfg.VscodeProfile;
        Viewers = new Dictionary<string, string>(
            cfg.Viewers ?? new Dictionary<string, string>(),
            StringComparer.OrdinalIgnoreCase);
    }
}

internal interface IAppAdapter
{
    string Id { get; }
    WindowInfo? Locate(string file);
    WindowInfo? Launch(string file, string workDir, int timeoutMs);
    void Close(WindowInfo window);
}

internal static class AdapterFactory
{
    public static IAppAdapter Create(string id) => id.ToLowerInvariant() switch
    {
        "excel" => new ExcelAdapter(),
        "vscode" => new VsCodeAdapter(),
        "browser" or "pdf" or "image" => new WebAdapter(id.ToLowerInvariant()),
        _ => new ShellAdapter(),
    };
    public static bool ExcelRegistered() => Type.GetTypeFromProgID("Excel.Application") is not null;

    public static string? VsCodePath()
    {
        string[] candidates =
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\Microsoft VS Code\Code.exe"),
            @"C:\Program Files\Microsoft VS Code\Code.exe",
            @"C:\Program Files (x86)\Microsoft VS Code\Code.exe",
        };

        string? found = candidates.FirstOrDefault(File.Exists);
        if (found is not null) return found;

        // 从 PATH 里的 code.cmd 反推安装目录
        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
        {
            if (dir.Length == 0) continue;
            try
            {
                string cmd = Path.Combine(dir, "code.cmd");
                if (!File.Exists(cmd)) continue;

                string exe = Path.GetFullPath(Path.Combine(dir, "..", "Code.exe"));
                if (File.Exists(exe)) return exe;
            }
            catch
            {
                // 忽略非法 PATH 项
            }
        }

        return null;
    }

    // 标题匹配：只认完整文件名。去掉扩展名再匹配会误伤同前缀文件
    // （instructions.md 命中 instructions.jsonl）或同名文件夹（instructions）。
    public static bool TitleMatches(string title, string file)
    {
        string name = Path.GetFileName(file);
        return name.Length > 0 && title.Contains(name, StringComparison.OrdinalIgnoreCase);
    }
}

internal sealed class ExcelAdapter : IAppAdapter
{
    private static readonly List<object> Owned = new();
    private static readonly object OwnedGate = new();

    public string Id => "excel";

    public WindowInfo? Locate(string file)
    {
        var windows = WindowScanner.Scan()
            .Where(w => Path.GetFileName(w.Exe).Equals("EXCEL.EXE", StringComparison.OrdinalIgnoreCase))
            .ToList();
        return windows.FirstOrDefault(w => AdapterFactory.TitleMatches(w.Title, file));
    }

    public WindowInfo? Launch(string file, string workDir, int timeoutMs)
    {
        Type? type = Type.GetTypeFromProgID("Excel.Application")
                     ?? throw new InvalidOperationException("Excel.Application 未注册");
        dynamic app = Activator.CreateInstance(type)!;
        lock (OwnedGate) Owned.Add(app);
        app.Visible = true;
        app.DisplayAlerts = false;
        app.Workbooks.Open(file);

        IntPtr hwnd = IntPtr.Zero;
        for (int i = 0; i < 50 && hwnd == IntPtr.Zero; i++)
        {
            try
            {
                hwnd = new IntPtr(Convert.ToInt32(app.Windows[1].Hwnd));
            }
            catch
            {
                Thread.Sleep(200);
            }
        }
        if (hwnd == IntPtr.Zero) return null;

        string name = Path.GetFileName(file);
        var sw = Stopwatch.StartNew();
        string title = "";
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            var sb = new StringBuilder(512);
            Native.GetWindowText(hwnd, sb, sb.Capacity);
            title = sb.ToString();
            if (title.Contains(name, StringComparison.OrdinalIgnoreCase)) break;
            Thread.Sleep(200);
        }

        Native.GetWindowThreadProcessId(hwnd, out uint pid);
        return new WindowInfo
        {
            Handle = hwnd,
            Pid = (int)pid,
            Title = title,
            Class = "XLMAIN",
            Exe = "EXCEL.EXE",
        };
    }

    public void Close(WindowInfo window) => WindowScanner.Close(window.Handle);
}

internal sealed class VsCodeAdapter : IAppAdapter
{
    public string Id => "vscode";

    public WindowInfo? Locate(string file)
    {
        var windows = WindowScanner.Scan()
            .Where(w => Path.GetFileName(w.Exe).Equals("Code.exe", StringComparison.OrdinalIgnoreCase))
            .ToList();
        return windows.FirstOrDefault(w => AdapterFactory.TitleMatches(w.Title, file));
    }

    public WindowInfo? Launch(string file, string workDir, int timeoutMs)
    {
        string exe = AdapterFactory.VsCodePath()
                     ?? throw new InvalidOperationException("未找到 Code.exe");

        var before = WindowScanner.Handles();
        // 可选：用独立 profile 启动，绕开被安全软件锁定的默认数据目录
        string? profile = Runtime.VsCodeProfile
                          ?? Environment.GetEnvironmentVariable("WORKBENCH_VSCODE_PROFILE");
        string arguments = string.IsNullOrWhiteSpace(profile)
            ? $"--new-window \"{file}\""
            : $"--user-data-dir \"{profile}\" --new-window \"{file}\"";

        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = true,
            WorkingDirectory = workDir,
            Arguments = arguments,
        };
        Process.Start(psi);

        // 以「新出现的 Code 窗口」为准：新配置下标题可能还是欢迎页，
        // 不能只靠标题包含文件名来判断启动成功。
        string name = Path.GetFileName(file);
        WindowInfo? fresh = WindowScanner.WaitNew(before,
            w => Path.GetFileName(w.Exe).Equals("Code.exe", StringComparison.OrdinalIgnoreCase), timeoutMs);

        if (fresh is null) return Locate(file);

        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 3000)
        {
            WindowScanner.Refresh(fresh);
            if (AdapterFactory.TitleMatches(fresh.Title, file)) break;
            Thread.Sleep(200);
        }
        return fresh;
    }

    public void Close(WindowInfo window) => WindowScanner.Close(window.Handle);
}

internal sealed class ShellAdapter : IAppAdapter
{
    public string Id => "shell";

    public WindowInfo? Locate(string file)
    {
        var windows = WindowScanner.Scan()
            .Where(w => !string.Equals(w.Class, "#32770", StringComparison.OrdinalIgnoreCase))
            .ToList();
        return windows.FirstOrDefault(w => AdapterFactory.TitleMatches(w.Title, file));
    }

    public WindowInfo? Launch(string file, string workDir, int timeoutMs)
    {
        var before = WindowScanner.Handles();
        Process.Start(new ProcessStartInfo(file)
        {
            UseShellExecute = true,
            WorkingDirectory = workDir,
        });

        // Office 冷启动经常超过 25 秒（尤其是受保护的视图），这里放宽等待。
        int wait = IsOfficeFile(file) ? Math.Max(timeoutMs, 45000) : timeoutMs;

        return WindowScanner.WaitNew(before, w =>
                   !string.Equals(w.Class, "#32770", StringComparison.OrdinalIgnoreCase) &&
                   AdapterFactory.TitleMatches(w.Title, file), wait)
               ?? Locate(file);
    }

    private static bool IsOfficeFile(string file) =>
        Path.GetExtension(file).ToLowerInvariant() is
            ".doc" or ".docx" or ".docm" or ".ppt" or ".pptx" or ".pptm" or
            ".xls" or ".xlsx" or ".xlsm";

    public void Close(WindowInfo window) => WindowScanner.Close(window.Handle);
}

// 浏览器 / PDF / 图片共用：Chromium 用 --app= 开无标签独立窗口，
// Firefox 用 --new-window。
internal sealed class WebAdapter : IAppAdapter
{
    private readonly string _id;

    public WebAdapter(string id) => _id = id;
    public string Id => _id;

    public WindowInfo? Locate(string file)
    {
        string name = Path.GetFileName(file);
        return WindowScanner.Scan()
            .FirstOrDefault(w => w.Title.Contains(name, StringComparison.OrdinalIgnoreCase));
    }

    public WindowInfo? Launch(string file, string workDir, int timeoutMs)
    {
        (string exe, bool appMode) = Resolve();
        string url = new Uri(Path.GetFullPath(file)).AbsoluteUri;
        string arguments = appMode ? $"--app=\"{url}\"" : $"--new-window \"{url}\"";

        var before = WindowScanner.Handles();
        Process.Start(new ProcessStartInfo(exe)
        {
            UseShellExecute = true,
            WorkingDirectory = workDir,
            Arguments = arguments,
        });

        string exeName = Path.GetFileName(exe);
        WindowInfo? hit = WindowScanner.WaitNew(before,
            w => Path.GetFileName(w.Exe).Equals(exeName, StringComparison.OrdinalIgnoreCase), timeoutMs);
        if (hit is null) return Locate(file);

        string name = Path.GetFileName(file);
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 5000)
        {
            WindowScanner.Refresh(hit);
            if (AdapterFactory.TitleMatches(hit.Title, file)) break;
            Thread.Sleep(200);
        }
        return hit;
    }

    public void Close(WindowInfo window) => WindowScanner.Close(window.Handle);

    private (string Exe, bool AppMode) Resolve()
    {
        string? configured = Runtime.Viewers.GetValueOrDefault(_id);
        string exe = configured is not null && File.Exists(configured) ? configured : DefaultExe();
        string name = Path.GetFileName(exe).ToLowerInvariant();
        return (exe, name is "msedge.exe" or "chrome.exe");
    }

    private static string DefaultExe()
    {
        string[] candidates =
        {
            @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
            @"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
            @"C:\Program Files\Google\Chrome\Application\chrome.exe",
            @"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
            @"C:\Program Files\Mozilla Firefox\firefox.exe",
        };
        return candidates.FirstOrDefault(File.Exists)
               ?? throw new InvalidOperationException("未找到 Edge / Chrome / Firefox");
    }
}
