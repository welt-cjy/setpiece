using System.Diagnostics;

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

internal sealed class ApplyProgress
{
    public int Completed { get; init; }
    public int Total { get; init; }
    public string File { get; init; } = "";
    public string Phase { get; init; } = "";
    public string? Note { get; init; }
    public bool? Ok { get; init; }
}

internal static class SceneRunner
{
    public static int Apply(string root, SetpieceConfig cfg, string sceneName,
        string? monitorName, bool dryRun, int timeoutMs, Action<ApplyProgress>? progress = null)
    {
        Scene? scene = cfg.Find(sceneName);
        if (scene is null) return Fail($"未找到场景: {sceneName}");

        var screens = Native.Screens();
        if (screens.Count == 0) return Fail("未找到显示器");

        List<Slot> slots = Layout.Plan(scene, screens, monitorName);
        if (slots.Count != scene.Items.Count) return Fail("无法计算布局");

        int total = scene.Items.Count;
        var rows = new RunRow[total];
        var paths = new string[total];
        var adapterIds = new string[total];
        var attemptable = new bool[total];

        for (int i = 0; i < total; i++)
        {
            SceneItem item = scene.Items[i];
            string path = ResolvePath(root, item.File);
            paths[i] = path;
            adapterIds[i] = item.Adapter ?? ResolveAdapter(cfg, path);
            attemptable[i] = item.AutoOpen != false;
            rows[i] = new RunRow
            {
                File = item.File,
                Adapter = adapterIds[i],
                Monitor = slots[i].Monitor.DeviceKey,
                Note = slots[i].Note ?? "",
            };
        }

        int attemptTotal = attemptable.Count(a => a);
        var placedWindows = new List<(IntPtr Hwnd, Rect Target, bool Maximized, RunRow Row)>();
        int completed = 0;
        bool anyLaunched = false;

        progress?.Invoke(new ApplyProgress { Completed = 0, Total = attemptTotal, Phase = "准备" });

        void Report(int index, string phase)
        {
            RunRow r = rows[index];
            progress?.Invoke(new ApplyProgress
            {
                Completed = Volatile.Read(ref completed),
                Total = attemptTotal,
                File = r.File,
                Phase = phase,
                Note = r.Note.Length > 0 ? r.Note : null,
                Ok = null,
            });
        }

        void Finish(int index)
        {
            RunRow r = rows[index];

            // 未启用的文件不进入进度条，只在事件流里标记为跳过。
            if (!attemptable[index])
            {
                progress?.Invoke(new ApplyProgress
                {
                    Completed = Volatile.Read(ref completed),
                    Total = attemptTotal,
                    File = r.File,
                    Phase = "跳过",
                    Note = r.Note.Length > 0 ? r.Note : null,
                    Ok = null,
                });
                return;
            }

            int done = Interlocked.Increment(ref completed);
            progress?.Invoke(new ApplyProgress
            {
                Completed = done,
                Total = attemptTotal,
                File = r.File,
                Phase = r.Ok ? "完成" : "失败",
                Note = r.Note.Length > 0 ? r.Note : null,
                Ok = r.Ok,
            });
        }

        void Process(int i)
        {
            SceneItem item = scene.Items[i];
            Slot slot = slots[i];
            RunRow row = rows[i];
            string path = paths[i];
            string adapterId = adapterIds[i];

            if (!File.Exists(path) && !Directory.Exists(path))
            {
                row.Note = "文件不存在";
                return;
            }

            if (item.AutoOpen == false)
            {
                row.Source = "未启用";
                row.Note = "未保存位置，已跳过；手动打开后保存位置可重新启用";
                row.Ok = true;
                return;
            }

            try
            {
                IAppAdapter adapter = AdapterFactory.Create(adapterId);
                WindowInfo? win = Resolve(adapter, path);

                if (win is not null)
                {
                    row.Source = "复用";
                    Report(i, "摆放中");
                }
                else if (dryRun)
                {
                    var planned = Rect.FromNormalized(item.Rect ?? slot.Rect, slot.Monitor.WorkArea);
                    row.Source = "将启动";
                    row.Target = planned.ToString();
                    row.Ok = true;
                    return;
                }
                else
                {
                    Report(i, "等待窗口");
                    win = adapter.Launch(path, Path.GetDirectoryName(path)!, timeoutMs);
                    row.Source = win is null ? "失败" : "启动";
                    if (win is not null)
                    {
                        Runtime.Cache?.Set(path, win);
                        Volatile.Write(ref anyLaunched, true);
                        // VS Code / Office 可能在窗口出现后继续恢复自己的窗口几何；等它稳定再摆。
                        WaitStable(win.Handle, 2500);
                    }
                }

                if (win is null)
                {
                    row.Note = "窗口未出现（应用启动较慢、被其他实例占用，或该类型没有默认打开方式）";
                    return;
                }

                Rect target = Rect.FromNormalized(item.Rect ?? slot.Rect, slot.Monitor.WorkArea);
                row.Target = target.ToString();
                row.Window = win.Title;

                if (dryRun)
                {
                    row.Ok = true;
                    return;
                }

                bool maximized = string.Equals(item.State, "maximized", StringComparison.OrdinalIgnoreCase);
                bool placed = WindowScanner.Place(win.Handle, target, out Rect actual);
                if (placed && maximized)
                {
                    WindowScanner.Maximize(win.Handle);
                    Thread.Sleep(250);
                    actual = WindowScanner.Frame(win.Handle);
                }

                row.Actual = actual.ToString();
                row.Ok = placed;
                if (placed)
                {
                    lock (placedWindows)
                        placedWindows.Add((win.Handle, target, maximized, row));
                }
                else
                {
                    string placeNote = WindowScanner.LastPlaceError.Length > 0
                        ? $"摆放未命中目标（{WindowScanner.LastPlaceError}）"
                        : "摆放未命中目标";
                    row.Note = NoteJoin(row.Note, placeNote);
                }
            }
            catch (Exception ex)
            {
                row.Note = ex.Message;
            }
        }

        void RunOne(int i)
        {
            try
            {
                Process(i);
            }
            catch (Exception ex)
            {
                rows[i].Note = ex.Message;
                rows[i].Ok = false;
            }
            finally
            {
                Finish(i);
            }
        }

        if (dryRun || total <= 1)
        {
            for (int i = 0; i < total; i++) RunOne(i);
        }
        else
        {
            // 打开文件主要是等待（Office/浏览器冷启动），并行可以显著缩短总时间。
            // 工作线程用 STA，保证 Excel 的 COM 调用和 ShellExecute 都能正常工作。
            int workers = Math.Min(4, total);
            int next = -1;
            var threads = new List<Thread>();

            for (int w = 0; w < workers; w++)
            {
                var thread = new Thread(() =>
                {
                    while (true)
                    {
                        int i = Interlocked.Increment(ref next);
                        if (i >= total) return;
                        RunOne(i);
                    }
                })
                {
                    IsBackground = true,
                };
                thread.SetApartmentState(ApartmentState.STA);
                thread.Start();
                threads.Add(thread);
            }

            foreach (Thread thread in threads) thread.Join();
        }

        // 有些应用（VS Code、Office、浏览器）在窗口出现后还会继续初始化，可能在第一次
        // 摆放之后好几秒才自己调整位置/大小。这里持续复查最多 10 秒，连续两轮稳定才结束。
        if (!dryRun && placedWindows.Count > 0)
        {
            progress?.Invoke(new ApplyProgress
            {
                Completed = attemptTotal,
                Total = attemptTotal,
                Phase = "校正",
            });

            int corrected = 0;
            int stableRounds = 0;
            var watch = Stopwatch.StartNew();

            // 新启动的应用（尤其是 VS Code/Electron）可能启动十几秒后才回写自己的窗口尺寸，
            // 所以冷启动后至少观察一段时间；发现窗口被改动就重新摆回去。
            int minWatchMs = Volatile.Read(ref anyLaunched) ? 8000 : 1200;

            while (watch.ElapsedMilliseconds < 15000 &&
                   (watch.ElapsedMilliseconds < minWatchMs || stableRounds < 2))
            {
                Thread.Sleep(400);
                bool allGood = true;

                foreach (var p in placedWindows)
                {
                    if (!Native.IsWindow(p.Hwnd)) continue;

                    if (p.Maximized)
                    {
                        if (!Native.IsZoomed(p.Hwnd))
                        {
                            WindowScanner.Maximize(p.Hwnd);
                            allGood = false;
                            corrected++;
                        }
                        p.Row.Actual = WindowScanner.Frame(p.Hwnd).ToString();
                        continue;
                    }

                    Rect now = WindowScanner.Frame(p.Hwnd);
                    if (MaxDelta(now, p.Target) <= 3) continue;

                    allGood = false;
                    if (WindowScanner.Place(p.Hwnd, p.Target, out Rect fixedFrame))
                    {
                        p.Row.Actual = fixedFrame.ToString();
                        p.Row.Note = NoteJoin(p.Row.Note, "窗口自行调整过，已二次校正");
                        corrected++;
                    }
                    else
                    {
                        p.Row.Actual = now.ToString();
                        p.Row.Note = NoteJoin(p.Row.Note,
                            $"窗口自行调整过，二次校正后仍有 {MaxDelta(now, p.Target)}px 偏差");
                    }
                }

                stableRounds = allGood ? stableRounds + 1 : 0;
            }

            if (corrected > 0) Console.WriteLine($"二次校正 {corrected} 次窗口位置");
        }

        Print(rows);
        progress?.Invoke(new ApplyProgress
        {
            Completed = attemptTotal,
            Total = attemptTotal,
            Phase = "结束",
        });
        return rows.All(r => r.Ok) ? 0 : 1;
    }

    private static int MaxDelta(Rect a, Rect b) => Math.Max(
        Math.Max(Math.Abs(a.Left - b.Left), Math.Abs(a.Top - b.Top)),
        Math.Max(Math.Abs(a.Right - b.Right), Math.Abs(a.Bottom - b.Bottom)));

    // 等窗口自己把初始几何/DPI 调整完：尺寸连续 400ms 不变即认为稳定。
    private static void WaitStable(IntPtr hwnd, int maxMs)
    {
        var watch = Stopwatch.StartNew();
        Rect last = WindowScanner.Frame(hwnd);
        long stableSince = watch.ElapsedMilliseconds;

        while (watch.ElapsedMilliseconds < maxMs)
        {
            Thread.Sleep(120);
            if (!Native.IsWindow(hwnd)) return;

            Rect now = WindowScanner.Frame(hwnd);
            if (MaxDelta(now, last) > 0)
            {
                last = now;
                stableSince = watch.ElapsedMilliseconds;
            }
            else if (watch.ElapsedMilliseconds - stableSince >= 400)
            {
                return;
            }
        }
    }

    private static string NoteJoin(string note, string extra) =>
        note.Length > 0 ? note + "；" + extra : extra;

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

    private static void Print(IEnumerable<RunRow> rows)
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
        Console.WriteLine($"完成: {rows.Count(r => r.Ok)}/{rows.Count()} 成功");
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
