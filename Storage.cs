using System.Globalization;
using System.IO;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace StickyTodo;

public class TaskDto
{
    public string Title { get; set; } = "";
    public string? Due { get; set; }
    public string Priority { get; set; } = "normal";
    public DateTime? CompletedAt { get; set; }
}

public static class Storage
{
    static readonly string Dir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
    public static readonly string TasksPath = Path.Combine(Dir, "tasks.yaml");
    public static readonly string ArchivePath = Path.Combine(Dir, "task-archive.yaml");
    public static readonly string SettingsPath = Path.Combine(Dir, "settings.yaml");

    public static AppearanceSettings LoadSettings()
    {
        try
        {
            return File.Exists(SettingsPath)
                ? Deser.Deserialize<AppearanceSettings>(File.ReadAllText(SettingsPath)) ?? new()
                : new();
        }
        catch { return new(); }
    }

    public static void SaveSettings(AppearanceSettings settings)
    {
        var tmp = SettingsPath + ".tmp";
        File.WriteAllText(tmp, Ser.Serialize(settings));
        File.Move(tmp, SettingsPath, true);
    }

    static readonly ISerializer Ser = new SerializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
        .Build();

    static readonly IDeserializer Deser = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    static List<TaskDto> Read(string path)
    {
        if (!File.Exists(path)) return new();
        try
        {
            var text = File.ReadAllText(path);
            return Deser.Deserialize<List<TaskDto>>(text) ?? new();
        }
        catch
        {
            try { File.Copy(path, path + ".bad", true); } catch { }
            return new();
        }
    }

    static void Write(string path, List<TaskDto> list)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, Ser.Serialize(list));
        File.Move(tmp, path, true);
    }

    static TaskDto ToDto(TaskItem t, DateTime? completed = null) => new()
    {
        Title = t.Title,
        Due = t.DueSerialized,
        Priority = t.IsHigh ? "high" : "normal",
        CompletedAt = completed
    };

    public static List<TaskItem> LoadTasks()
    {
        var result = new List<TaskItem>();
        foreach (var d in Read(TasksPath))
        {
            if (string.IsNullOrWhiteSpace(d.Title)) continue;
            var t = new TaskItem { Title = d.Title.Trim(), IsHigh = string.Equals(d.Priority, "high", StringComparison.OrdinalIgnoreCase) };
            if (!string.IsNullOrWhiteSpace(d.Due))
            {
                var parts = d.Due.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (DateTime.TryParseExact(parts[0], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                {
                    TimeSpan? time = null;
                    if (parts.Length > 1 && TaskItem.TryParseTime(parts[1], out var ts)) time = ts;
                    t.SetDue(date, time);
                }
            }
            result.Add(t);
        }
        return result;
    }

    public static void SaveTasks(IEnumerable<TaskItem> tasks) => Write(TasksPath, tasks.Select(t => ToDto(t)).ToList());

    public static void Archive(TaskItem t)
    {
        var list = Read(ArchivePath);
        list.Add(ToDto(t, DateTime.Now));
        Write(ArchivePath, list);
    }
}
