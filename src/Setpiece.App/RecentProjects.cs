using System.IO;
using System.Text.Json;

namespace Setpiece.App;

internal static class RecentProjects
{
    private const int MaxItems = 8;

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Setpiece", "recent.json");

    public static List<string> Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new List<string>();
            return JsonSerializer.Deserialize<List<string>>(File.ReadAllText(FilePath))
                   ?? new List<string>();
        }
        catch
        {
            return new List<string>();
        }
    }

    public static void Add(string root)
    {
        try
        {
            List<string> items = Load();
            items.RemoveAll(p => string.Equals(p, root, StringComparison.OrdinalIgnoreCase));
            items.Insert(0, root);
            if (items.Count > MaxItems) items.RemoveRange(MaxItems, items.Count - MaxItems);

            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(items,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // 最近项目只是便利功能，写不进去不影响使用。
        }
    }
}
