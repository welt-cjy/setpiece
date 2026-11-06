namespace Setpiece;

internal sealed class RunRow
{
    public string File = "";
    public string Adapter = "";
    public string Monitor = "";
    public string Window = "";
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

        var screens = Native.Screens();
        if (screens.Count == 0) return Fail("未找到显示器");

        List<Slot> slots = Layout.Plan(scene, screens, monitorName);
        if (slots.Count != scene.Items.Count) return Fail("无法计算布局");

        var rows = new List<RunRow>();

        for (int i = 0; i < scene.Items.Count; i++)
        {
            SceneItem item = scene.Items[i];
            Slot slot = slots[i];
            string path = ResolvePath(root, item.File);
            string adapterId = item.Adapter ?? ResolveAdapter(cfg, path);
            var row = new RunRow
            {
                File = item.File,
                Adapter = adapterId,
                Monitor = slot.Monitor.DeviceKey,
                Note = slot.Note ?? "",
            };
            rows.Add(row);

            if (!File.Exists(path) && !Directory.Exists(path))
            {
                row.Note = "文件不存在";
                continue;
            }

            if (item.AutoOpen == false)
            {
                row.Source = "未启用";
                row.Note = "未保存位置，已跳过；手动打开后保存位置可重新启用";
                row.Ok = true;
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
                    var planned = Rect.FromNormalized(item.Rect ?? slot.Rect, slot.Monitor.WorkArea);
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
                    row.Note = "窗口未出现（应用启动较慢、被其他实例占用，或该类型没有默认打开方式）";
                    continue;
                }

                Rect target = Rect.FromNormalized(item.Rect ?? slot.Rect, slot.Monitor.WorkArea);
                row.Target = target.ToString();
                row.Window = win.Title;

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
                {
                    string placeNote = WindowScanner.LastPlaceError.Length > 0
                        ? $"摆放未命中目标（{WindowScanner.LastPlaceError}）"
                        : "摆放未命中目标";
                    row.Note = row.Note.Length > 0 ? row.Note + "；" + placeNote : placeNote;
                }
            }
            catch (Exception ex)
            {
                row.Note = ex.Message;
            }
        }

        Print(rows);
        return rows.All(r => r.Ok) ? 0 : 1;
    }

    public static int Capture(string root, SetpieceConfig cfg, string sceneName, out int disabled)
    {
        disabled = 0;

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
                    // 没打开的条目不进入自动打开列表，但保留在场景里作为候选。
                    item.AutoOpen = false;
                    disabled++;
                    row.Note = "未打开，已停用自动打开";
                    row.Ok = true;
                    continue;
                }

                Rect frame = WindowScanner.Frame(win.Handle);
                MonitorInfo screen = WindowScanner.ScreenOf(win.Handle);
                item.Rect = frame.Normalized(screen.WorkArea);
                item.Monitor = screen.DeviceKey;
                item.State = Native.IsZoomed(win.Handle) ? "maximized" : "normal";
                item.AutoOpen = null;

                row.Monitor = screen.DeviceKey;
                row.Window = win.Title;
                row.Source = screen.Primary ? "primary" : screen.DeviceKey;
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
        if (disabled > 0)
            Console.WriteLine($"已停用 {disabled} 个未打开的文件（保留在场景中，手动打开后保存位置即可恢复）");
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
            string where = r.Monitor.Length > 0 ? $"  @{r.Monitor}" : "";
            Console.WriteLine($"[{(r.Ok ? "OK  " : "FAIL")}] {r.File}  ({r.Adapter}, {r.Source}){where}");
            if (r.Target.Length > 0) Console.WriteLine($"        target = {r.Target}");
            if (r.Window.Length > 0) Console.WriteLine($"        window = \"{r.Window}\"");
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
        return !string.IsNullOrEmpty(ext) && cfg.Extensions is not null &&
               cfg.Extensions.TryGetValue(ext, out string? adapter)
            ? adapter
            : "shell";
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 2;
    }
}
