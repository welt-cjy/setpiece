using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Setpiece;

// 记录「文件 → 窗口」的运行时映射，弥补标题匹配在浏览器窗口上的不足。
// 存放在 %LOCALAPPDATA%，不进项目仓库。
internal sealed class WindowCache
{
    private sealed class Entry
    {
        public long Hwnd { get; set; }
        public int Pid { get; set; }
        public long Started { get; set; }
        public string Exe { get; set; } = "";
        public string Title { get; set; } = "";
    }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly Dictionary<string, Entry> _map = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Setpiece", "runtime.json");

    public static WindowCache Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new WindowCache();
            var data = JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(FilePath), Json);
            var cache = new WindowCache();
            foreach (var kv in data ?? new Dictionary<string, Entry>())
                cache._map[kv.Key] = kv.Value;
            return cache;
        }
        catch
        {
            return new WindowCache();
        }
    }

    public WindowInfo? TryGet(string file)
    {
        lock (_gate)
        {
            if (!_map.TryGetValue(file, out Entry? entry)) return null;

            var hwnd = new IntPtr(entry.Hwnd);
            if (!Native.IsWindow(hwnd)) return null;

            Native.GetWindowThreadProcessId(hwnd, out uint pid);
            if ((int)pid != entry.Pid) return null;

            try
            {
                using Process process = Process.GetProcessById(entry.Pid);
                if (process.HasExited) return null;
                if (entry.Started != 0 && process.StartTime.ToUniversalTime().Ticks != entry.Started) return null;
            }
            catch
            {
                return null;
            }

            string exe = WindowScanner.ExePathOf(entry.Pid);
            if (entry.Exe.Length > 0 &&
                !string.Equals(Path.GetFileName(exe), Path.GetFileName(entry.Exe), StringComparison.OrdinalIgnoreCase))
                return null;

            var title = new StringBuilder(512);
            Native.GetWindowText(hwnd, title, title.Capacity);

            return new WindowInfo
            {
                Handle = hwnd,
                Pid = entry.Pid,
                Title = title.ToString(),
                Exe = exe,
            };
        }
    }

    public void Set(string file, WindowInfo window)
    {
        long started = 0;
        try
        {
            using Process process = Process.GetProcessById(window.Pid);
            started = process.StartTime.ToUniversalTime().Ticks;
        }
        catch
        {
            // 拿不到启动时间时不做该校验
        }

        lock (_gate)
        {
            _map[file] = new Entry
            {
                Hwnd = window.Handle.ToInt64(),
                Pid = window.Pid,
                Started = started,
                Exe = window.Exe,
                Title = window.Title,
            };
            Save();
        }
    }

    public void Remove(string file)
    {
        lock (_gate)
        {
            if (_map.Remove(file)) Save();
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(_map, Json));
        }
        catch
        {
            // 缓存写失败不影响主流程
        }
    }
}
