using System.Text;

namespace Setpiece;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        Native.SetProcessDpiAwarenessContext(new IntPtr(-4));
        Console.OutputEncoding = Encoding.UTF8;

        if (args.Length == 0)
            return Usage();

        string command = args[0].ToLowerInvariant();
        var (opts, pos) = Parse(args.Skip(1));
        string root = Path.GetFullPath(opts.GetValueOrDefault("project") ?? Directory.GetCurrentDirectory());

        try
        {
            return command switch
            {
                "doctor" => Doctor(root),
                "init" => Init(root, opts),
                "scan" => ScanProject(root),
                "list" => List(root),
                "apply" => Apply(root, opts, pos),
                "capture" => Capture(root, opts, pos),
                "close" => Close(root, opts, pos),
                "help" or "--help" or "-h" => Usage(),
                _ => Unknown(command),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"错误: {ex.Message}");
            return 2;
        }
    }

    private static int Apply(string root, Dictionary<string, string?> opts, List<string> pos)
    {
        if (pos.Count == 0) return UsageError("apply 需要场景名");
        SetpieceConfig cfg = ConfigStore.Load(root);
        Configure(cfg);
        WarnIntegrity();
        bool dryRun = opts.ContainsKey("dry-run");
        int timeout = int.TryParse(opts.GetValueOrDefault("timeout"), out int t) ? t : 25000;
        return SceneRunner.Apply(root, cfg, pos[0], opts.GetValueOrDefault("monitor"), dryRun, timeout);
    }

    private static int Capture(string root, Dictionary<string, string?> opts, List<string> pos)
    {
        if (pos.Count == 0) return UsageError("capture 需要场景名");
        SetpieceConfig cfg = ConfigStore.Load(root);
        Configure(cfg);
        return SceneRunner.Capture(root, cfg, pos[0], out _);
    }

    private static int Close(string root, Dictionary<string, string?> opts, List<string> pos)
    {
        if (pos.Count == 0) return UsageError("close 需要场景名");
        SetpieceConfig cfg = ConfigStore.Load(root);
        Configure(cfg);
        return SceneRunner.Close(root, cfg, pos[0]);
    }

    private static int Init(string root, Dictionary<string, string?> opts)
    {
        if (ConfigStore.Exists(root))
        {
            Console.WriteLine($"配置已存在: {ConfigStore.ConfigPath(root)}");
            return 0;
        }

        string sceneName = opts.GetValueOrDefault("scene") ?? "main";
        var files = (opts.GetValueOrDefault("files") ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(f => Path.IsPathRooted(f) ? Path.GetRelativePath(root, f) : f)
            .ToList();

        SetpieceConfig cfg = ConfigStore.CreateDefault(root, sceneName, files);
        ConfigStore.Save(root, cfg);
        Console.WriteLine($"已创建 {ConfigStore.ConfigPath(root)}");
        if (files.Count == 0)
            Console.WriteLine("提示: 用 --files a.csv,b.csv,main.py 指定场景文件。");
        return 0;
    }

    private static int ScanProject(string root)
    {
        if (!Directory.Exists(root))
        {
            Console.Error.WriteLine($"目录不存在: {root}");
            return 2;
        }

        List<ScanCandidate> candidates = ProjectScanner.Scan(root);
        if (candidates.Count == 0)
        {
            Console.WriteLine("没有找到可加入场景的候选文件。");
            return 0;
        }

        string? category = null;
        foreach (ScanCandidate candidate in candidates)
        {
            if (candidate.Category != category)
            {
                Console.WriteLine();
                Console.WriteLine($"## {candidate.Category}");
                category = candidate.Category;
            }
            Console.WriteLine($"  {candidate.RelativePath}");
        }

        Console.WriteLine();
        Console.WriteLine($"共 {candidates.Count} 个候选（扫描深度 3，已跳过 bin/obj/node_modules 等目录）");
        return 0;
    }

    private static int List(string root)
    {
        SetpieceConfig cfg = ConfigStore.Load(root);
        if (cfg.Scenes.Count == 0)
        {
            Console.WriteLine("没有场景。");
            return 0;
        }

        foreach (Scene scene in cfg.Scenes)
        {
            Console.WriteLine($"- {scene.Name}  ({scene.Items.Count} 个文件, 模板={scene.Template}, 显示器={scene.Monitor})");
            foreach (SceneItem item in scene.Items)
                Console.WriteLine($"    {item.File}  [{item.Adapter ?? "auto"}]");
        }
        return 0;
    }

    private static int Doctor(string root)
    {
        Console.WriteLine("=== setpiece doctor ===");
        Console.WriteLine($"项目目录: {root}");
        string config = ConfigStore.ConfigPath(root);
        Console.WriteLine($"配置: {config} {(File.Exists(config) ? "(存在)" : "(缺失，先运行 setpiece init)")}");

        string? profile = null;
        if (File.Exists(config))
        {
            try { profile = ConfigStore.Load(root).VscodeProfile; } catch { /* 配置损坏时忽略 */ }
        }
        profile ??= Environment.GetEnvironmentVariable("WORKBENCH_VSCODE_PROFILE");
        Console.WriteLine($"VSCode profile: {profile ?? "(默认)"}");
        Console.WriteLine();

        string integrity = WindowScanner.IntegrityLevel();
        Console.WriteLine($"进程完整性: {integrity}");
        if (integrity == "Low")
        {
            Console.WriteLine("  [警告] Low 完整性下跨进程窗口操作会被拒绝。");
            Console.WriteLine("         请对 exe 签名，或改用 dotnet setpiece.dll 运行。");
        }
        Console.WriteLine();

        Console.WriteLine("显示器:");
        foreach (MonitorInfo screen in Native.Screens())
            Console.WriteLine($"  {screen}");
        Console.WriteLine();

        Console.WriteLine("适配器:");
        Console.WriteLine($"  excel  : {(AdapterFactory.ExcelRegistered() ? "可用" : "不可用（Excel.Application 未注册）")}");
        string? code = AdapterFactory.VsCodePath();
        Console.WriteLine($"  vscode : {(code is not null ? $"可用 ({code})" : "不可用（未找到 Code.exe）")}");
        Console.WriteLine("  shell  : 可用");
        return 0;
    }

    private static void Configure(SetpieceConfig cfg) => Runtime.Configure(cfg);

    private static void WarnIntegrity()
    {
        if (WindowScanner.IntegrityLevel() != "Low") return;
        Console.WriteLine("[警告] 进程处于 Low 完整性，窗口操作可能全部失败。");
        Console.WriteLine("       请对 exe 签名，或改用 dotnet setpiece.dll 运行。");
        Console.WriteLine();
    }

    private static (Dictionary<string, string?> Opts, List<string> Pos) Parse(IEnumerable<string> args)
    {
        var opts = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var pos = new List<string>();
        string? pending = null;

        foreach (string arg in args)
        {
            if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                if (pending is not null) opts[pending] = null;
                pending = arg[2..];
            }
            else if (pending is not null)
            {
                opts[pending] = arg;
                pending = null;
            }
            else
            {
                pos.Add(arg);
            }
        }

        if (pending is not null) opts[pending] = null;
        return (opts, pos);
    }

    private static int Usage()
    {
        Console.WriteLine("""
setpiece <命令> [选项]

命令:
  init      创建 .setpiece/setpiece.json
  scan      扫描项目，列出可加入场景的候选文件
  list      列出场景
  apply     按场景打开并摆放窗口
  capture   捕获当前窗口位置写回场景
  close     关闭场景中的窗口
  doctor    环境自检（完整性、显示器、适配器）

选项:
  --project <目录>   项目根目录（默认当前目录）
  --scene <名称>     init 时使用的场景名（默认 main）
  --files a,b,c      init 时写入的文件
  --monitor <名称>   primary 或显示器序号
  --dry-run          只打印计划，不启动或移动窗口
  --timeout <毫秒>   等待窗口的超时（默认 25000）

示例:
  setpiece scan
  setpiece init --files src/main.py,results/summary.csv,paper/paper.pdf
  setpiece apply main
  setpiece capture main
  setpiece close main
  setpiece doctor
""");
        return 0;
    }

    private static int UsageError(string message)
    {
        Console.Error.WriteLine(message);
        return 2;
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"未知命令: {command}");
        Usage();
        return 2;
    }
}
