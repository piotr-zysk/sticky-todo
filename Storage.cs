using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace StickyTodo;

public class TaskDto
{
    [JsonRequired] public Guid Id { get; set; }
    [JsonRequired] public string Title { get; set; } = "";
    public string? Due { get; set; }
    [JsonRequired] public string Priority { get; set; } = "normal";
    public DateTime? CompletedAt { get; set; }
    [JsonRequired] public DateTimeOffset UpdatedAt { get; set; }
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
        RecoverTransaction(destination);
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
        .WithTypeConverter(new TimestampConverter())
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
        .Build();

    static readonly IDeserializer Deser = new DeserializerBuilder()
        .WithTypeConverter(new TimestampConverter())
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    sealed class TimestampConverter : IYamlTypeConverter
    {
        public bool Accepts(Type type) => type == typeof(DateTimeOffset);
        public object ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer) =>
            DateTimeOffset.ParseExact(parser.Consume<Scalar>().Value, "O", CultureInfo.InvariantCulture, DateTimeStyles.None);
        public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer) =>
            emitter.Emit(new Scalar(((DateTimeOffset)value!).ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)));
    }

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

    public static TaskDto ToDto(TaskItem t, DateTime? completed = null) => new()
    {
        Id = t.Id,
        Title = t.Title,
        Due = t.DueSerialized,
        Priority = t.IsHigh ? "high" : "normal",
        CompletedAt = completed,
        UpdatedAt = t.UpdatedAt
    };

    public static List<TaskItem> LoadTasks()
    {
        InitializeTaskStorage();
        MigrateRecords();
        return Read(TasksPath).Where(d => !string.IsNullOrWhiteSpace(d.Title)).Select(ToTask).ToList();
    }

    static void MigrateRecords()
    {
        var writes = new Dictionary<string, string?>();
        var all = new Dictionary<string, List<TaskDto>>();
        foreach (var path in new[] { TasksPath, ArchivePath })
        {
            var ids = new HashSet<Guid>();
            bool changed = false;
            var records = Read(path);
            all[path] = records;
            var fileTime = File.Exists(path) ? new DateTimeOffset(File.GetLastWriteTimeUtc(path)) : DateTimeOffset.UnixEpoch;
            foreach (var d in records)
            {
                if (d.Id == Guid.Empty || !ids.Add(d.Id))
                {
                    d.Id = Guid.NewGuid();
                    ids.Add(d.Id);
                    changed = true;
                }
                if (d.UpdatedAt == default)
                {
                    var timestamp = fileTime;
                    if (File.Exists(DetailsPath(d.Id)))
                    {
                        var detailsTime = new DateTimeOffset(File.GetLastWriteTimeUtc(DetailsPath(d.Id)));
                        if (detailsTime > timestamp) timestamp = detailsTime;
                    }
                    if (d.CompletedAt is { } completed && new DateTimeOffset(completed.ToUniversalTime()) > timestamp)
                        timestamp = new DateTimeOffset(completed.ToUniversalTime());
                    d.UpdatedAt = timestamp;
                    changed = true;
                }
            }
            if (changed) writes[Path.GetFileName(path)] = Ser.Serialize(records);
        }
        // Older releases could be interrupted between archiving and removing the active record.
        // Keep one identity and the latest version instead of inventing a second task.
        var archivedById = all[ArchivePath].ToDictionary(t => t.Id);
        foreach (var active in all[TasksPath].ToList())
        {
            if (!archivedById.TryGetValue(active.Id, out var archived)) continue;
            if (archived.UpdatedAt >= active.UpdatedAt) all[TasksPath].Remove(active);
            else all[ArchivePath].Remove(archived);
            writes["tasks.yaml"] = Ser.Serialize(all[TasksPath]);
            writes["task-archive.yaml"] = Ser.Serialize(all[ArchivePath]);
        }
        if (writes.Count > 0) WriteTransaction(writes);
    }

    public static TaskItem ToTask(TaskDto d)
    {
        var t = new TaskItem { Id = d.Id, Title = d.Title.Trim(), IsHigh = string.Equals(d.Priority, "high", StringComparison.OrdinalIgnoreCase) };
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
        t.UpdatedAt = d.UpdatedAt;
        return t;
    }

    public static void SaveTasks(IEnumerable<TaskItem> tasks) => WriteTransaction(new()
    {
        ["tasks.yaml"] = Ser.Serialize(tasks.Select(t => ToDto(t)).ToList())
    });

    public static void Archive(TaskItem t)
    {
        var list = Read(ArchivePath);
        if (list.All(item => item.Id != t.Id)) list.Add(ToDto(t, DateTime.Now));
        Write(ArchivePath, list);
    }

    public static void CompleteTask(TaskItem task, IEnumerable<TaskItem> remaining)
    {
        var archive = Read(ArchivePath).Where(t => t.Id != task.Id).ToList();
        archive.Add(ToDto(task, DateTime.UtcNow));
        WriteTransaction(new()
        {
            ["tasks.yaml"] = Ser.Serialize(remaining.Select(t => ToDto(t)).ToList()),
            ["task-archive.yaml"] = Ser.Serialize(archive),
            [$"{task.Id:N}.txt"] = null
        });
    }

    public static void SaveTaskDetails(TaskItem task, string text, IEnumerable<TaskItem> tasks)
    {
        var timestamp = TaskItem.NextTimestamp(task.UpdatedAt);
        var records = tasks.Select(t => ToDto(t)).ToList();
        records.Single(t => t.Id == task.Id).UpdatedAt = timestamp;
        WriteTransaction(new()
        {
            [$"{task.Id:N}.txt"] = text,
            ["tasks.yaml"] = Ser.Serialize(records)
        });
        task.UpdatedAt = timestamp;
    }

    public static SyncSnapshot LoadSnapshot()
    {
        InitializeTaskStorage();
        MigrateRecords();
        var snapshot = new SyncSnapshot
        {
            Tasks = Read(TasksPath).Concat(Read(ArchivePath)).Select(d => new SyncTask
            {
                Id = d.Id, Title = d.Title, Due = d.Due, Priority = d.Priority,
                CompletedAt = d.CompletedAt, UpdatedAt = d.UpdatedAt,
                Details = d.CompletedAt.HasValue ? "" : LoadDetails(d.Id)
            }).ToList()
        };
        snapshot.Validate();
        return snapshot;
    }

    public static void ApplySnapshot(SyncSnapshot snapshot)
    {
        snapshot.Validate();
        var writes = new Dictionary<string, string?>
        {
            ["tasks.yaml"] = Ser.Serialize(snapshot.Tasks.Where(t => !t.CompletedAt.HasValue).Select(Metadata).ToList()),
            ["task-archive.yaml"] = Ser.Serialize(snapshot.Tasks.Where(t => t.CompletedAt.HasValue).Select(Metadata).ToList())
        };
        foreach (var task in snapshot.Tasks)
            writes[$"{task.Id:N}.txt"] = task.CompletedAt.HasValue ? null : task.Details;
        WriteTransaction(writes);
    }

    static TaskDto Metadata(SyncTask t) => new()
    {
        Id = t.Id, Title = t.Title, Due = t.Due, Priority = t.Priority,
        CompletedAt = t.CompletedAt, UpdatedAt = t.UpdatedAt
    };

    // A durable undo journal makes multi-file saves recoverable after either an I/O failure or a crash.
    static void WriteTransaction(Dictionary<string, string?> writes)
    {
        Directory.CreateDirectory(TasksDirectory);
        RecoverTransaction(TasksDirectory);
        var previous = writes.Keys.ToDictionary(name => name, name =>
            File.Exists(Path.Combine(TasksDirectory, name)) ? File.ReadAllText(Path.Combine(TasksDirectory, name)) : null);
        var journal = Path.Combine(TasksDirectory, ".transaction.json");
        WriteText(journal, JsonSerializer.Serialize(previous));
        try
        {
            ApplyFiles(TasksDirectory, writes);
            File.Delete(journal);
        }
        catch
        {
            RecoverTransaction(TasksDirectory);
            throw;
        }
    }

    static void RecoverTransaction(string directory)
    {
        var journal = Path.Combine(directory, ".transaction.json");
        if (!File.Exists(journal)) return;
        var previous = JsonSerializer.Deserialize<Dictionary<string, string?>>(File.ReadAllText(journal))
            ?? throw new IOException("Cannot read the task storage recovery journal.");
        ApplyFiles(directory, previous);
        File.Delete(journal);
    }

    static void ApplyFiles(string directory, Dictionary<string, string?> files)
    {
        foreach (var (name, text) in files)
        {
            if (Path.GetFileName(name) != name || name is "." or ".." || name.Contains(':'))
                throw new IOException("Invalid path in the task storage journal.");
            var path = Path.Combine(directory, name);
            // Recovery need not replace a file that never changed (it may still be locked).
            if (text != null && File.Exists(path) && File.ReadAllText(path) == text) continue;
            if (text == null) File.Delete(path); else WriteText(path, text);
        }
    }
}
