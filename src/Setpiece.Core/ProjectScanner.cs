namespace Setpiece;

internal sealed class ScanCandidate
{
    public string RelativePath = "";
    public string Category = "";
    public long Size;
    public DateTime Modified;

    public override string ToString() => $"[{Category}] {RelativePath}";
}

internal static class ProjectScanner
{
    private static readonly HashSet<string> ExcludedDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", ".git", ".svn", ".hg", ".vs", ".idea", ".vscode",
        "node_modules", ".venv", "venv", "env", "__pycache__", ".pytest_cache",
        "dist", "build", "target", "out", "coverage", ".next", ".nuxt",
        "packages", ".setpiece", ".workbench",
    };

    private static readonly string[] CategoryOrder = { "代码", "表格", "图片", "文档", "网页" };

    private static readonly Dictionary<string, string> CategoryByExt = new(StringComparer.OrdinalIgnoreCase)
    {
        [".py"] = "代码", [".js"] = "代码", [".ts"] = "代码", [".cs"] = "代码",
        [".java"] = "代码", [".cpp"] = "代码", [".c"] = "代码", [".h"] = "代码",
        [".go"] = "代码", [".rs"] = "代码", [".ipynb"] = "代码", [".sql"] = "代码",
        [".r"] = "代码", [".m"] = "代码",

        [".csv"] = "表格", [".xlsx"] = "表格", [".xls"] = "表格", [".xlsm"] = "表格",
        [".parquet"] = "表格", [".ods"] = "表格",

        [".pdf"] = "文档", [".md"] = "文档", [".docx"] = "文档", [".doc"] = "文档",
        [".pptx"] = "文档", [".ppt"] = "文档", [".txt"] = "文档", [".tex"] = "文档",
        [".rtf"] = "文档", [".odt"] = "文档", [".odp"] = "文档", [".epub"] = "文档",

        [".png"] = "图片", [".jpg"] = "图片", [".jpeg"] = "图片", [".gif"] = "图片",
        [".bmp"] = "图片", [".webp"] = "图片", [".svg"] = "图片", [".tif"] = "图片",
        [".tiff"] = "图片",

        [".html"] = "网页", [".htm"] = "网页",
    };

    public static bool IsExcludedDir(string name) => ExcludedDirs.Contains(name);

    public static bool IsSupportedExtension(string extension) => CategoryByExt.ContainsKey(extension);

    // 某个目录（含子目录）下的全部文件，返回相对项目根的路径（统一用 /）。
    public static List<string> FilesUnder(string root, string folder, int maxFiles = 5000)
    {
        var files = new List<string>();
        Collect(new DirectoryInfo(folder), root, 0, 24, maxFiles, files);

        for (int i = 0; i < files.Count; i++)
            files[i] = files[i].Replace('\\', '/');

        return files;
    }

    // 扫描整个项目（不假设固定目录），跳过构建产物和依赖目录。
    public static List<ScanCandidate> Scan(string root, int maxDepth = 3, int maxPerCategory = 12)
    {
        var found = new List<ScanCandidate>();
        Walk(new DirectoryInfo(root), root, 0, maxDepth, found);

        return found
            .GroupBy(c => c.Category)
            .SelectMany(g => g.OrderByDescending(c => c.Modified).Take(maxPerCategory))
            .OrderBy(c => Array.IndexOf(CategoryOrder, c.Category))
            .ThenByDescending(c => c.Modified)
            .ToList();
    }

    // 列出项目里的全部文件（用于按标题匹配已打开的窗口）。
    public static List<string> AllFiles(string root, int maxDepth = 4, int maxFiles = 20000)
    {
        var files = new List<string>();
        Collect(new DirectoryInfo(root), root, 0, maxDepth, maxFiles, files);
        return files;
    }

    private static void Collect(DirectoryInfo dir, string root, int depth, int maxDepth,
        int maxFiles, List<string> files)
    {
        if (files.Count >= maxFiles) return;

        FileInfo[] entries;
        try
        {
            entries = dir.GetFiles();
        }
        catch
        {
            return;
        }

        foreach (FileInfo file in entries)
        {
            if (file.Extension.Length < 2) continue;
            files.Add(Path.GetRelativePath(root, file.FullName));
            if (files.Count >= maxFiles) return;
        }

        if (depth >= maxDepth) return;

        DirectoryInfo[] dirs;
        try
        {
            dirs = dir.GetDirectories();
        }
        catch
        {
            return;
        }

        foreach (DirectoryInfo sub in dirs)
        {
            if (sub.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
            if (sub.Name.StartsWith('.')) continue;
            if (ExcludedDirs.Contains(sub.Name)) continue;
            Collect(sub, root, depth + 1, maxDepth, maxFiles, files);
        }
    }

    private static void Walk(DirectoryInfo dir, string root, int depth, int maxDepth,
        List<ScanCandidate> found)
    {
        FileInfo[] files;
        try
        {
            files = dir.GetFiles();
        }
        catch
        {
            return;
        }

        foreach (FileInfo file in files)
        {
            if (!CategoryByExt.TryGetValue(file.Extension, out string? category)) continue;

            found.Add(new ScanCandidate
            {
                RelativePath = Path.GetRelativePath(root, file.FullName),
                Category = category,
                Size = file.Length,
                Modified = file.LastWriteTime,
            });
        }

        if (depth >= maxDepth) return;

        DirectoryInfo[] dirs;
        try
        {
            dirs = dir.GetDirectories();
        }
        catch
        {
            return;
        }

        foreach (DirectoryInfo sub in dirs)
        {
            if (sub.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
            if (sub.Name.StartsWith('.')) continue;
            if (ExcludedDirs.Contains(sub.Name)) continue;
            Walk(sub, root, depth + 1, maxDepth, found);
        }
    }
}
