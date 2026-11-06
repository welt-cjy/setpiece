using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Setpiece;

internal sealed class InlineDoubleArrayConverter : JsonConverter<double[]>
{
    private static readonly JsonSerializerOptions Compact = new() { WriteIndented = false };
    private static readonly JsonSerializerOptions Plain = new();

    public override double[] Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        JsonSerializer.Deserialize<double[]>(ref reader, Plain) ?? Array.Empty<double>();

    public override void Write(Utf8JsonWriter writer, double[] value, JsonSerializerOptions options) =>
        writer.WriteRawValue(JsonSerializer.Serialize(value, Compact));
}

internal static class ConfigStore
{
    public const string DirName = ".setpiece";
    public const string FileName = "setpiece.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new InlineDoubleArrayConverter() },
    };

    public static string ConfigPath(string root) => Path.Combine(root, DirName, FileName);

    public static bool Exists(string root) => File.Exists(ConfigPath(root));

    public static SetpieceConfig Load(string root)
    {
        string path = ConfigPath(root);
        if (!File.Exists(path))
            throw new FileNotFoundException($"未找到配置: {path}（先运行 setpiece init）");

        SetpieceConfig cfg = JsonSerializer.Deserialize<SetpieceConfig>(File.ReadAllText(path), Options)
                              ?? new SetpieceConfig();

        if (cfg.Extensions is not null)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in cfg.Extensions) map[kv.Key.ToLowerInvariant()] = kv.Value;
            cfg.Extensions = map.Count > 0 ? map : null;
        }

        return cfg;
    }

    public static void Save(string root, SetpieceConfig cfg)
    {
        if (cfg.Viewers is { Count: 0 }) cfg.Viewers = null;
        if (cfg.Extensions is { Count: 0 }) cfg.Extensions = null;

        string path = ConfigPath(root);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(cfg, Options));
    }

    public static SetpieceConfig CreateDefault(string root, string sceneName, IEnumerable<string> files)
    {
        var cfg = new SetpieceConfig();
        var scene = new Scene { Name = sceneName, Template = "auto", Monitor = "primary" };
        foreach (string f in files)
            scene.Items.Add(new SceneItem { File = f.Replace('\\', '/') });
        cfg.Scenes.Add(scene);
        return cfg;
    }
}
