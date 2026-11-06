namespace Setpiece;

internal sealed class SetpieceConfig
{
    public int Version { get; set; } = 1;

    // 可选：VSCode 的用户数据目录。机器上默认路径被安全软件拦截时，
    // 指向同一份数据的别名路径即可，登录态和插件不受影响。
    public string? VscodeProfile { get; set; }

    // 可选：覆盖 browser / pdf / image 使用的查看器可执行文件
    public Dictionary<string, string>? Viewers { get; set; }

    // 可选：扩展名 → 适配器。留空时全部走系统默认打开方式。
    public Dictionary<string, string>? Extensions { get; set; }

    public List<Scene> Scenes { get; set; } = new();

    public Scene? Find(string name) =>
        Scenes.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
}

internal sealed class Scene
{
    public string Name { get; set; } = "";
    public string Template { get; set; } = "auto";
    public string Monitor { get; set; } = "primary";
    public List<SceneItem> Items { get; set; } = new();
}

internal sealed class SceneItem
{
    public string File { get; set; } = "";
    public string? Adapter { get; set; }

    // 可选：本文件固定放在哪个显示器（DISPLAY1 / 序号）。为空时继承场景设置。
    public string? Monitor { get; set; }

    // null/true = 打开场景时自动启动；false = 仅作为候选保留。
    // 保存当前位置时，打开过的文件会置回自动打开，没打开过的会被停用。
    public bool? AutoOpen { get; set; }

    public double[]? Rect { get; set; }
    public string? State { get; set; }
}

internal sealed class Slot
{
    public MonitorInfo Monitor = null!;
    public double[] Rect = Array.Empty<double>();
    public string? Note;
}

internal static class Layout
{
    // 为场景里的每个文件算出「放在哪个显示器 + 归一化矩形」。
    // cross-monitor：按显示器从左到右轮转分配，同一屏内的多个窗口再按 auto 切分；
    // 其它模板：文件落到各自指定的显示器（默认场景设置），组内按模板切分。
    public static List<Slot> Plan(Scene scene, IReadOnlyList<MonitorInfo> screens, string? forceMonitor)
    {
        var slots = new List<Slot>();
        if (scene.Items.Count == 0) return slots;

        List<MonitorInfo> ordered = screens
            .OrderBy(s => s.Bounds.Left)
            .ThenBy(s => s.Bounds.Top)
            .ToList();
        if (ordered.Count == 0) return slots;

        bool cross = string.Equals(scene.Template, "cross-monitor", StringComparison.OrdinalIgnoreCase);

        var targets = new List<(MonitorInfo Monitor, string? Note)>();
        int roundRobin = 0;

        foreach (SceneItem item in scene.Items)
        {
            string? name = forceMonitor ?? item.Monitor;

            if (string.IsNullOrWhiteSpace(name))
            {
                if (cross)
                {
                    targets.Add((ordered[roundRobin % ordered.Count], null));
                    roundRobin++;
                }
                else
                {
                    targets.Add((Resolve(scene.Monitor, ordered, out string? note), note));
                }
            }
            else
            {
                MonitorInfo monitor = Resolve(name, ordered, out string? note);
                targets.Add((monitor, note));
                if (cross) roundRobin++;
            }
        }

        var groups = new Dictionary<MonitorInfo, List<int>>();
        for (int i = 0; i < targets.Count; i++)
        {
            MonitorInfo monitor = targets[i].Monitor;
            if (!groups.TryGetValue(monitor, out List<int>? list))
            {
                list = new List<int>();
                groups[monitor] = list;
            }
            list.Add(i);
        }

        var result = new Slot[scene.Items.Count];
        foreach ((MonitorInfo monitor, List<int> indices) in groups)
        {
            List<double[]> rects = Rects(cross ? "auto" : scene.Template, indices.Count);
            for (int k = 0; k < indices.Count; k++)
            {
                int index = indices[k];
                result[index] = new Slot
                {
                    Monitor = monitor,
                    Rect = scene.Items[index].Rect ?? rects[k],
                    Note = targets[index].Note,
                };
            }
        }

        slots.AddRange(result);
        return slots;
    }

    private static MonitorInfo Resolve(string? name, List<MonitorInfo> screens, out string? note)
    {
        note = null;

        if (string.IsNullOrWhiteSpace(name) || name.Equals("primary", StringComparison.OrdinalIgnoreCase))
            return screens.FirstOrDefault(s => s.Primary) ?? screens[0];

        if (int.TryParse(name, out int index))
        {
            MonitorInfo? byIndex = screens.FirstOrDefault(s => s.Index == index);
            if (byIndex is not null) return byIndex;
        }
        else
        {
            MonitorInfo? byDevice = screens.FirstOrDefault(s =>
                                         s.DeviceKey.Equals(name, StringComparison.OrdinalIgnoreCase))
                                     ?? screens.FirstOrDefault(s =>
                                         s.Device.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (byDevice is not null) return byDevice;
        }

        note = $"显示器 {name} 不可用，已回退到主屏";
        return screens.FirstOrDefault(s => s.Primary) ?? screens[0];
    }

    public static List<double[]> Rects(string? template, int count)
    {
        if (count <= 0) return new List<double[]>();

        string t = (template ?? "auto").ToLowerInvariant();
        if (t == "auto")
            t = count switch
            {
                1 => "full",
                2 => "left-right",
                3 => "left-right-top-bottom",
                _ => "grid",
            };

        List<double[]> rects = t switch
        {
            "full" => new List<double[]> { new[] { 0.0, 0.0, 1.0, 1.0 } },
            "row" => Row(count),
            "column" => Column(count),
            "grid" => Grid(count),
            "left-right" => LeftRight(count),
            "left-right-top-bottom" => LeftRightTopBottom(count),
            "focus+side" => FocusSide(count),
            _ => Grid(count),
        };

        if (rects.Count < count) rects = Grid(count);
        return rects;
    }

    private static List<double[]> Row(int n) =>
        Enumerable.Range(0, n)
            .Select(i => new[] { (double)i / n, 0.0, (double)(i + 1) / n, 1.0 })
            .ToList();

    private static List<double[]> Column(int n) =>
        Enumerable.Range(0, n)
            .Select(i => new[] { 0.0, (double)i / n, 1.0, (double)(i + 1) / n })
            .ToList();

    private static List<double[]> Grid(int n)
    {
        int cols = (int)Math.Ceiling(Math.Sqrt(n));
        int rows = (int)Math.Ceiling((double)n / cols);
        var list = new List<double[]>();
        for (int i = 0; i < n; i++)
        {
            int r = i / cols, c = i % cols;
            list.Add(new[]
            {
                (double)c / cols, (double)r / rows,
                (double)(c + 1) / cols, (double)(r + 1) / rows,
            });
        }
        return list;
    }

    private static List<double[]> LeftRight(int n)
    {
        if (n == 1) return new List<double[]> { new[] { 0.0, 0.0, 1.0, 1.0 } };
        var list = new List<double[]> { new[] { 0.0, 0.0, 0.5, 1.0 } };
        int rest = n - 1;
        for (int i = 0; i < rest; i++)
            list.Add(new[] { 0.5, (double)i / rest, 1.0, (double)(i + 1) / rest });
        return list;
    }

    private static List<double[]> LeftRightTopBottom(int n)
    {
        if (n != 3) return LeftRight(n);
        return new List<double[]>
        {
            new[] { 0.0, 0.0, 0.5, 1.0 },
            new[] { 0.5, 0.0, 1.0, 0.5 },
            new[] { 0.5, 0.5, 1.0, 1.0 },
        };
    }

    private static List<double[]> FocusSide(int n)
    {
        if (n <= 1) return new List<double[]> { new[] { 0.0, 0.0, 1.0, 1.0 } };
        var list = new List<double[]> { new[] { 0.0, 0.0, 0.66, 1.0 } };
        int rest = n - 1;
        for (int i = 0; i < rest; i++)
            list.Add(new[] { 0.66, (double)i / rest, 1.0, (double)(i + 1) / rest });
        return list;
    }
}
