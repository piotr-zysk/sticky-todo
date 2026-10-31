using System.Globalization;
using System.IO;
using System.Text.Json.Serialization;

namespace StickyTodo;

public sealed class SyncTask : TaskDto
{
    [JsonRequired] public string Details { get; set; } = "";
}

public sealed class SyncSnapshot
{
    [JsonRequired] public int Version { get; set; } = 1;
    [JsonRequired] public List<SyncTask> Tasks { get; set; } = new();

    public void Validate()
    {
        if (Version != 1 || Tasks == null) throw new InvalidDataException("Unsupported Google sync file format.");
        var ids = new HashSet<Guid>();
        foreach (var task in Tasks)
        {
            if (task == null || task.Id == Guid.Empty || !ids.Add(task.Id) ||
                string.IsNullOrWhiteSpace(task.Title) || task.UpdatedAt == default ||
                task.UpdatedAt == DateTimeOffset.MaxValue || task.Details == null ||
                task.Priority is not ("normal" or "high"))
                throw new InvalidDataException("The sync file contains an invalid or duplicate task.");
            if (task.Due != null && !DateTime.TryParseExact(task.Due,
                    new[] { "yyyy-MM-dd", "yyyy-MM-dd HH:mm" }, CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out _))
                throw new InvalidDataException("The sync file contains an invalid due date.");
            if (task.CompletedAt.HasValue && task.Details.Length != 0)
                throw new InvalidDataException("Completed tasks cannot contain details.");
        }
    }
}

public static class TaskMerge
{
    // Keep the local ordering, then append newly discovered remote records.
    public static SyncSnapshot Merge(SyncSnapshot local, SyncSnapshot remote)
    {
        local.Validate();
        remote.Validate();
        var google = remote.Tasks.ToDictionary(t => t.Id);
        var localIds = local.Tasks.Select(t => t.Id).ToHashSet();
        return new SyncSnapshot
        {
            Tasks = local.Tasks.Select(t => google.TryGetValue(t.Id, out var other) && other.UpdatedAt >= t.UpdatedAt ? other : t)
                .Concat(remote.Tasks.Where(t => !localIds.Contains(t.Id))).ToList()
        };
    }
}

public sealed record RemoteSnapshot(SyncSnapshot Snapshot, string FileId, string ETag);

public interface IDriveTransport
{
    Task<RemoteSnapshot> ReadAsync(CancellationToken cancellationToken);
    // False means another client changed the file since ReadAsync.
    Task<bool> WriteAsync(RemoteSnapshot original, SyncSnapshot merged, CancellationToken cancellationToken);
}

public static class SyncEngine
{
    public static async Task<SyncSnapshot> MergeAndUploadAsync(SyncSnapshot local, IDriveTransport drive,
        CancellationToken cancellationToken)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remote = await drive.ReadAsync(cancellationToken);
            var merged = TaskMerge.Merge(local, remote.Snapshot);
            if (await drive.WriteAsync(remote, merged, cancellationToken)) return merged;
        }
        throw new IOException("Google Drive kept changing during sync. Please retry.");
    }
}
