namespace Setpiece;

internal sealed class SetpieceConfig
{
    public int Version { get; set; } = 1;

    // 可选：VSCode 的用户数据目录。机器上默认路径被安全软件拦截时，
    // 指向同一份数据的别名路径即可，登录态和插件不受影响。
    public string? VscodeProfile { get; set; }

    // 可选：覆盖 browser / pdf / image 使用的查看器可执行文件
    public Dictionary<string, string>? Viewers { get; set; }

    public Dictionary<string, string> Extensions { get; set; } = DefaultExtensions();
    public List<Scene> Scenes { get; set; } = new();

    public static Dictionary<string, string> DefaultExtensions() => new(StringComparer.OrdinalIgnoreCase)
    {
        [".py"] = "vscode",
        [".js"] = "vscode",
        [".ts"] = "vscode",
        [".md"] = "vscode",
        [".json"] = "vscode",
        [".csv"] = "excel",
        [".xlsx"] = "excel",
        [".xls"] = "excel",
        [".html"] = "browser",
        [".htm"] = "browser",
        [".pdf"] = "pdf",
        [".png"] = "image",
        [".jpg"] = "image",
        [".jpeg"] = "image",
        [".gif"] = "image",
        [".bmp"] = "image",
        [".webp"] = "image",
    };

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
    public double[]? Rect { get; set; }
    public string? State { get; set; }
}

internal static class Layout
{
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
