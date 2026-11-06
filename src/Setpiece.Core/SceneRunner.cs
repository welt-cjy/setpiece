namespace Setpiece;

internal sealed class RunRow
{
    public string File = "";
    public string Adapter = "";
    public string Source = "";
    public string Target = "";
    public string Actual = "";
    public string Note = "";
    public bool Ok;
}

internal static class SceneRunner
{
    public static int Apply(string root, SetpieceConfig cfg, string sceneName,
        string? monitorName, bool dryRun, int timeoutMs)
    {
        Scene? scene = cfg.Find(sceneName);
        if (scene is null) return Fail($"未找到场景: {sceneName}");

        MonitorInfo? monitor = ResolveMonitor(monitorName ?? scene.Monitor);
        if (monitor is null) return Fail($"未找到显示器: {monitorName ?? scene.Monitor}");

        List<double[]> rects = Layout.Rects(scene.Template, scene.Items.Count);
        var rows = new List<RunRow>();

        for (int i = 0; i < scene.Items.Count; i++)
        {
            SceneItem item = scene.Items[i];
            string path = ResolvePath(root, item.File);
            string adapterId = item.Adapter ?? ResolveAdapter(cfg, path);
            var row = new RunRow { File = item.File, Adapter = adapterId };
            rows.Add(row);

            if (!File.Exists(path) && !Directory.Exists(path))
            {
                row.Note = "文件不存在";
                continue;
            }

            try
            {
                IAppAdapter adapter = AdapterFactory.Create(adapterId);
                WindowInfo? win = Resolve(adapter, path);

                if (win is not null)
                {
                    row.Source = "复用";
                }
                else if (dryRun)
                {
                    var planned = Rect.FromNormalized(item.Rect ?? rects[i], monitor.WorkArea);
                    row.Source = "将启动";
                    row.Target = planned.ToString();
                    row.Ok = true;
                    continue;
                }
                else
                {
                    win = adapter.Launch(path, Path.GetDirectoryName(path)!, timeoutMs);
                    row.Source = win is null ? "失败" : "启动";
                    if (win is not null) Runtime.Cache?.Set(path, win);
                }

                if (win is null)
                {
                    row.Note = "窗口未出现（应用可能未就绪，或被其他实例占用）";
                    continue;
                }

                Rect target = Rect.FromNormalized(item.Rect ?? rects[i], monitor.WorkArea);
                row.Target = target.ToString();

                if (dryRun)
                {
                    row.Ok = true;
                    continue;
                }

                bool placed = WindowScanner.Place(win.Handle, target, out Rect actual);
                if (placed && string.Equals(item.State, "maximized", StringComparison.OrdinalIgnoreCase))
                {
                    WindowScanner.Maximize(win.Handle);
                    Thread.Sleep(250);
                    actual = WindowScanner.Frame(win.Handle);
                }

                row.Actual = actual.ToString();
                row.Ok = placed;
                if (!placed)
                    row.Note = WindowScanner.LastPlaceError.Length > 0
                        ? $"摆放未命中目标（{WindowScanner.LastPlaceError}）"
                        : "摆放未命中目标";
            }
            catch (Exception ex)
            {
                row.Note = ex.Message;
            }
        }

        Print(rows);
        return rows.All(r => r.Ok) ? 0 : 1;
    }

    public static int Capture(string root, SetpieceConfig cfg, string sceneName)
    {
        Scene? scene = cfg.Find(sceneName);
        if (scene is null) return Fail($"未找到场景: {sceneName}");

        var rows = new List<RunRow>();
        foreach (SceneItem item in scene.Items)
        {
            string path = ResolvePath(root, item.File);
            string adapterId = item.Adapter ?? ResolveAdapter(cfg, path);
            var row = new RunRow { File = item.File, Adapter = adapterId };
            rows.Add(row);

            try
            {
                IAppAdapter adapter = AdapterFactory.Create(adapterId);
                WindowInfo? win = Resolve(adapter, path);
                if (win is null)
                {
                    row.Note = "未找到已打开的窗口";
                    continue;
                }

                Rect frame = WindowScanner.Frame(win.Handle);
                MonitorInfo screen = WindowScanner.ScreenOf(win.Handle);
                item.Rect = frame.Normalized(screen.WorkArea);
                item.State = Native.IsZoomed(win.Handle) ? "maximized" : "normal";

                row.Source = screen.Primary ? "primary" : $"monitor#{screen.Index}";
                row.Target = string.Join(", ", item.Rect.Select(v => v.ToString("0.###")));
                row.Actual = frame.ToString();
                row.Ok = true;
            }
            catch (Exception ex)
            {
                row.Note = ex.Message;
            }
        }

        ConfigStore.Save(root, cfg);
        Print(rows);
        Console.WriteLine($"已写入 {ConfigStore.ConfigPath(root)}");
        return rows.All(r => r.Ok) ? 0 : 1;
    }

    public static int Close(string root, SetpieceConfig cfg, string sceneName)
    {
        Scene? scene = cfg.Find(sceneName);
        if (scene is null) return Fail($"未找到场景: {sceneName}");

        var rows = new List<RunRow>();
        foreach (SceneItem item in scene.Items)
        {
            string path = ResolvePath(root, item.File);
            string adapterId = item.Adapter ?? ResolveAdapter(cfg, path);
            var row = new RunRow { File = item.File, Adapter = adapterId };
            rows.Add(row);

            try
            {
                IAppAdapter adapter = AdapterFactory.Create(adapterId);
                WindowInfo? win = Resolve(adapter, path);
                if (win is null)
                {
                    row.Source = "未打开";
                    row.Ok = true;
                    continue;
                }

                adapter.Close(win);
                Runtime.Cache?.Remove(path);
                row.Source = "已发送关闭";
                row.Ok = true;
            }
            catch (Exception ex)
            {
                row.Note = ex.Message;
            }
        }

        Thread.Sleep(1000);
        foreach (RunRow row in rows.Where(r => r.Ok && r.Source == "已发送关闭").ToList())
        {
            SceneItem? item = scene.Items.FirstOrDefault(i => i.File == row.File);
            if (item is null) continue;
            string path = ResolvePath(root, item.File);
            try
            {
                if (AdapterFactory.Create(row.Adapter).Locate(path) is not null)
                {
                    row.Ok = false;
                    row.Note = "仍在运行（可能有未保存提示）";
                }
            }
            catch
            {
                // 忽略复查异常
            }
        }

        Print(rows);
        return rows.All(r => r.Ok) ? 0 : 1;
    }

    private static void Print(List<RunRow> rows)
    {
        foreach (RunRow r in rows)
        {
            Console.WriteLine($"[{(r.Ok ? "OK  " : "FAIL")}] {r.File}  ({r.Adapter}, {r.Source})");
            if (r.Target.Length > 0) Console.WriteLine($"        target = {r.Target}");
            if (r.Actual.Length > 0) Console.WriteLine($"        actual = {r.Actual}");
            if (r.Note.Length > 0) Console.WriteLine($"        {r.Note}");
        }

        Console.WriteLine();
        Console.WriteLine($"完成: {rows.Count(r => r.Ok)}/{rows.Count} 成功");
    }

    private static string ResolvePath(string root, string file) =>
        Path.IsPathRooted(file) ? Path.GetFullPath(file) : Path.GetFullPath(Path.Combine(root, file));

    // 先查运行时缓存（按文件路径），命中则直接用；否则回退到适配器的标题匹配。
    private static WindowInfo? Resolve(IAppAdapter adapter, string path) =>
        Runtime.Cache?.TryGet(path) ?? adapter.Locate(path);

    private static string ResolveAdapter(SetpieceConfig cfg, string path)
    {
        string ext = Path.GetExtension(path);
        return !string.IsNullOrEmpty(ext) && cfg.Extensions.TryGetValue(ext, out string? adapter)
            ? adapter
            : "shell";
    }

    private static MonitorInfo? ResolveMonitor(string name)
    {
        var screens = Native.Screens();
        if (string.IsNullOrWhiteSpace(name) || name.Equals("primary", StringComparison.OrdinalIgnoreCase))
            return screens.FirstOrDefault(s => s.Primary) ?? screens.FirstOrDefault();
        if (int.TryParse(name, out int index))
            return screens.FirstOrDefault(s => s.Index == index);
        return screens.FirstOrDefault(s => s.ToString().Contains(name, StringComparison.OrdinalIgnoreCase));
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 2;
    }
}
