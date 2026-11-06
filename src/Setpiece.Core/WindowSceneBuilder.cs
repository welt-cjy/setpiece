namespace Setpiece;

internal sealed class CapturedWindow
{
    public string RelativePath = "";
    public string Title = "";
    public string Adapter = "";
    public string Monitor = "";
    public double[] Rect = Array.Empty<double>();
    public string State = "normal";
}

internal sealed class CaptureResult
{
    public List<CapturedWindow> Windows = new();
    public int Unmatched;
}

internal static class WindowSceneBuilder
{
    // 把当前打开的窗口反推成一个场景：按标题里的文件名匹配项目文件，
    // 同时记录显示器、归一化位置和最大化状态。
    public static CaptureResult Capture(SetpieceConfig cfg, string root)
    {
        var result = new CaptureResult();
        Dictionary<string, List<string>> index = BuildIndex(root);
        if (index.Count == 0) return result;

        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (WindowInfo window in WindowScanner.Scan())
        {
            if (string.IsNullOrWhiteSpace(window.Title)) continue;

            string? match = Match(window.Title, index);
            if (match is null || !used.Add(match))
            {
                if (match is null) result.Unmatched++;
                continue;
            }

            Rect frame = WindowScanner.Frame(window.Handle);
            MonitorInfo screen = WindowScanner.ScreenOf(window.Handle);

            result.Windows.Add(new CapturedWindow
            {
                RelativePath = match.Replace('\\', '/'),
                Title = window.Title,
                Adapter = AdapterFor(cfg, match),
                Monitor = screen.DeviceKey,
                Rect = frame.Normalized(screen.WorkArea),
                State = Native.IsZoomed(window.Handle) ? "maximized" : "normal",
            });
        }

        result.Windows = result.Windows
            .OrderBy(w => w.Monitor)
            .ThenBy(w => w.Rect.Length == 4 ? w.Rect[0] : 0)
            .ToList();

        return result;
    }

    private static Dictionary<string, List<string>> BuildIndex(string root)
    {
        var index = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (string relative in ProjectScanner.AllFiles(root))
        {
            string name = Path.GetFileName(relative);
            if (!index.TryGetValue(name, out List<string>? list))
            {
                list = new List<string>();
                index[name] = list;
            }
            list.Add(relative);
        }

        return index;
    }

    private static string? Match(string title, Dictionary<string, List<string>> index)
    {
        string? best = null;
        int bestLength = 0;

        foreach ((string name, List<string> paths) in index)
        {
            // 太短的名字容易误匹配（如 a.py 出现在别的标题里）
            if (name.Length < 4 || name.Length <= bestLength) continue;
            if (!title.Contains(name, StringComparison.OrdinalIgnoreCase)) continue;

            best = paths[0];
            bestLength = name.Length;
        }

        return best;
    }

    private static string AdapterFor(SetpieceConfig cfg, string relativePath)
    {
        string ext = Path.GetExtension(relativePath);
        return !string.IsNullOrEmpty(ext) && cfg.Extensions is not null &&
               cfg.Extensions.TryGetValue(ext, out string? adapter)
            ? adapter
            : "";
    }
}
