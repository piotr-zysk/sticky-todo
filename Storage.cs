using System.Globalization;
using System.IO;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace StickyTodo;

public class TaskDto
{
    public Guid Id { get; set; }
    public string Title { get; set; } = "";
    public string? Due { get; set; }
    public string Priority { get; set; } = "normal";
    public DateTime? CompletedAt { get; set; }
}

public static class Storage
{
    static readonly string Dir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
    public static readonly string TasksDirectory = Path.Combine(Dir, "tasks");
    public static readonly string TasksPath = Path.Combine(TasksDirectory, "tasks.yaml");
    public static readonly string ArchivePath = Path.Combine(TasksDirectory, "task-archive.yaml");
    public static readonly string SettingsPath = Path.Combine(Dir, "settings.yaml");

    public static void InitializeTaskStorage(string? applicationDirectory = null)
    {
        var root = applicationDirectory ?? Dir;
        var destination = Path.Combine(root, "tasks");
        var names = new[] { "tasks.yaml", "task-archive.yaml" };
        foreach (var name in names)
            if (File.Exists(Path.Combine(root, name)) && File.Exists(Path.Combine(destination, name)))
                throw new IOException($"Both {Path.Combine(root, name)} and {Path.Combine(destination, name)} exist. Move one aside before restarting.");
        Directory.CreateDirectory(destination);
        foreach (var name in names)
            if (File.Exists(Path.Combine(root, name)))
                File.Move(Path.Combine(root, name), Path.Combine(destination, name));
    }

    public static string DetailsPath(Guid id) => Path.Combine(TasksDirectory, $"{id:N}.txt");
    public static string LoadDetails(Guid id) => File.Exists(DetailsPath(id)) ? File.ReadAllText(DetailsPath(id)) : "";
    public static void SaveDetails(Guid id, string text) => WriteText(DetailsPath(id), text);
    public static void DeleteDetails(Guid id) => File.Delete(DetailsPath(id));

    static void WriteText(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, text, new System.Text.UTF8Encoding(false));
        File.Move(tmp, path, true);
    }

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
            throw;
        }
    }

    static void Write(string path, List<TaskDto> list)
    {
        WriteText(path, Ser.Serialize(list));
    }

    static TaskDto ToDto(TaskItem t, DateTime? completed = null) => new()
    {
        Id = t.Id,
        Title = t.Title,
        Due = t.DueSerialized,
        Priority = t.IsHigh ? "high" : "normal",
        CompletedAt = completed
    };

    public static List<TaskItem> LoadTasks()
    {
        InitializeTaskStorage();
        var result = new List<TaskItem>();
        var ids = new HashSet<Guid>();
        bool changed = false;
        foreach (var d in Read(TasksPath))
        {
            if (string.IsNullOrWhiteSpace(d.Title)) continue;
            var id = d.Id;
            if (id == Guid.Empty || !ids.Add(id))
            {
                id = Guid.NewGuid();
                ids.Add(id);
                changed = true;
            }
            var t = new TaskItem { Id = id, Title = d.Title.Trim(), IsHigh = string.Equals(d.Priority, "high", StringComparison.OrdinalIgnoreCase) };
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
        if (changed) SaveTasks(result);
        return result;
    }

    public static void SaveTasks(IEnumerable<TaskItem> tasks) => Write(TasksPath, tasks.Select(t => ToDto(t)).ToList());

    public static void Archive(TaskItem t)
    {
        var list = Read(ArchivePath);
        if (list.All(item => item.Id != t.Id)) list.Add(ToDto(t, DateTime.Now));
        Write(ArchivePath, list);
    }
}
