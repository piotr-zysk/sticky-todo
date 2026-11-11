using System.Globalization;
using System.IO;
using System.Text.Json.Serialization;

namespace StickyTodo;

public sealed class SyncTask : TaskDto
{
    [JsonRequired] public string Details { get; set; } = "";
}

// Sync bookkeeping only: deleted subtask content is never retained.
public sealed class TaskDeletion
{
    [JsonRequired] public Guid Id { get; set; }
    [JsonRequired] public Guid ParentId { get; set; }
    [JsonRequired] public DateTimeOffset DeletedAt { get; set; }
}

public sealed class SyncSnapshot
{
    [JsonRequired] public int Version { get; set; } = 2;
    [JsonRequired] public List<SyncTask> Tasks { get; set; } = new();
    public List<TaskDeletion> Deletions { get; set; } = new();

    public void Validate()
    {
        if (Version is not (1 or 2) || Tasks == null || Deletions == null)
            throw new InvalidDataException("Unsupported Google sync file format.");
        if (Version == 1 && (Deletions.Count != 0 || Tasks.Any(t => t?.ParentId != null || t?.SubtasksDeletedAt != null)))
            throw new InvalidDataException("Version 1 cannot contain subtasks.");
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
            if (task.CompletedAt.HasValue && (task.Details.Length != 0 || task.ParentId != null))
                throw new InvalidDataException("Completed tasks cannot contain details.");
            if (task.SubtasksDeletedAt is { } deletedAt && (deletedAt == default || deletedAt > task.UpdatedAt))
                throw new InvalidDataException("Invalid subtask deletion timestamp.");
        }
        var records = Tasks.ToDictionary(t => t.Id);
        var parents = Tasks.ToDictionary(t => t.Id, t => t.ParentId);
        foreach (var deletion in Deletions)
        {
            if (deletion == null || deletion.Id == Guid.Empty || deletion.ParentId == Guid.Empty ||
                !ids.Add(deletion.Id) || deletion.DeletedAt == default || deletion.DeletedAt == DateTimeOffset.MaxValue)
                throw new InvalidDataException("Invalid or duplicate subtask deletion marker.");
            parents.Add(deletion.Id, deletion.ParentId);
        }
        foreach (var task in Tasks.Where(t => t.ParentId != null))
            if (!records.TryGetValue(task.ParentId!.Value, out var parent) || parent.CompletedAt.HasValue)
                throw new InvalidDataException("An active subtask must have an active parent.");
        foreach (var id in parents.Keys)
        {
            var seen = new HashSet<Guid> { id };
            var current = id;
            while (parents[current] is { } parent)
            {
                if (!seen.Add(parent) || !parents.ContainsKey(parent))
                    throw new InvalidDataException("Task hierarchy contains a cycle or missing parent.");
                current = parent;
            }
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
        var result = new SyncSnapshot
        {
            Tasks = local.Tasks.Select(t => google.TryGetValue(t.Id, out var other) && other.UpdatedAt >= t.UpdatedAt ? other : t)
                .Concat(remote.Tasks.Where(t => !localIds.Contains(t.Id))).ToList(),
            Deletions = local.Deletions.Concat(remote.Deletions).GroupBy(d => d.Id)
                .Select(g => g.OrderBy(d => d.DeletedAt).Last()).ToList()
        };
        var tasks = result.Tasks.ToDictionary(t => t.Id);
        // Deletion history is bookkeeping, independent of the winning task's editable fields.
        foreach (var group in local.Tasks.Concat(remote.Tasks).GroupBy(t => t.Id))
        {
            var cutoff = group.Max(t => t.CompletedAt.HasValue ? t.UpdatedAt : t.SubtasksDeletedAt);
            var task = tasks[group.Key];
            if (cutoff != task.SubtasksDeletedAt) tasks[group.Key] = Copy(task, task.UpdatedAt, cutoff);
        }
        var deletions = result.Deletions.ToDictionary(d => d.Id);
        foreach (var deletion in result.Deletions)
        {
            if (tasks.TryGetValue(deletion.Id, out var task) && (task.UpdatedAt > deletion.DeletedAt ||
                task.UpdatedAt == deletion.DeletedAt && google.ContainsKey(task.Id)))
            {
                if (task.SubtasksDeletedAt == null || task.SubtasksDeletedAt < deletion.DeletedAt)
                    tasks[task.Id] = Copy(task, task.UpdatedAt, deletion.DeletedAt);
                deletions.Remove(deletion.Id);
            }
            else tasks.Remove(deletion.Id);
        }

        // Keep active candidates only until this merge finishes, to restore ancestors of newer child edits.
        var active = local.Tasks.Concat(remote.Tasks).Where(t => !t.CompletedAt.HasValue)
            .GroupBy(t => t.Id).ToDictionary(g => g.Key, g => g.OrderBy(t => t.UpdatedAt).Last());
        var suppressed = new Dictionary<Guid, DateTimeOffset>();
        var restored = new Dictionary<Guid, DateTimeOffset>();
        var restoredCutoffs = new Dictionary<Guid, DateTimeOffset>();
        foreach (var leaf in tasks.Values.Where(t => !t.CompletedAt.HasValue).ToList())
        {
            var chain = new List<Guid>();
            Guid? current = leaf.ParentId;
            var barrier = DateTimeOffset.MinValue;
            var seen = new HashSet<Guid> { leaf.Id };
            while (current is { } id)
            {
                if (!seen.Add(id)) throw new InvalidDataException("Merged task hierarchy contains a cycle.");
                chain.Add(id);
                if (tasks.TryGetValue(id, out var parent))
                {
                    if (parent.CompletedAt.HasValue && parent.UpdatedAt > barrier) barrier = parent.UpdatedAt;
                    if (parent.SubtasksDeletedAt is { } cutoff && cutoff > barrier) barrier = cutoff;
                    current = parent.ParentId;
                }
                else if (deletions.TryGetValue(id, out var deletion))
                {
                    if (deletion.DeletedAt > barrier) barrier = deletion.DeletedAt;
                    current = deletion.ParentId;
                }
                else throw new InvalidDataException("Merged subtask has no parent.");
            }
            if (barrier == DateTimeOffset.MinValue) continue;
            if (leaf.UpdatedAt > barrier)
            {
                foreach (var id in chain)
                {
                    if (!restored.TryGetValue(id, out var previous) || leaf.UpdatedAt > previous)
                        restored[id] = leaf.UpdatedAt;
                    if (!restoredCutoffs.TryGetValue(id, out var cutoff) || barrier > cutoff) restoredCutoffs[id] = barrier;
                }
            }
            else suppressed[leaf.Id] = barrier;
        }
        // A newly restored ancestor can make an otherwise old sibling accessible again; retain the
        // deletion decision made above so only independent newer edits restore deleted content.
        foreach (var (id, deletionTime) in suppressed)
        {
            if (restored.ContainsKey(id)) continue;
            if (!tasks.TryGetValue(id, out var task)) continue;
            tasks.Remove(id);
            deletions[id] = new TaskDeletion { Id = id, ParentId = task.ParentId!.Value, DeletedAt = deletionTime };
        }
        foreach (var (id, timestamp) in restored)
        {
            if (tasks.TryGetValue(id, out var parent) && !parent.CompletedAt.HasValue && !suppressed.ContainsKey(id)) continue;
            if (!active.TryGetValue(id, out var source))
                throw new InvalidDataException("Cannot restore a deleted ancestor without an active record.");
            var cutoff = restoredCutoffs[id];
            if (source.SubtasksDeletedAt is { } previous && previous > cutoff) cutoff = previous;
            tasks[id] = Copy(source, TaskItem.NextTimestamp(timestamp), cutoff);
            deletions.Remove(id);
        }
        var order = result.Tasks.Select(t => t.Id).Concat(local.Tasks.Select(t => t.Id))
            .Concat(remote.Tasks.Select(t => t.Id)).Distinct().ToList();
        result.Tasks = order.Where(tasks.ContainsKey).Select(id => tasks[id]).ToList();
        result.Deletions = deletions.Values.ToList();
        result.Validate();
        return result;
    }

    static SyncTask Copy(SyncTask source, DateTimeOffset timestamp, DateTimeOffset? cutoff) => new()
    {
        Id = source.Id, ParentId = source.ParentId, Title = source.Title, Due = source.Due,
        Priority = source.Priority, Details = source.Details, CompletedAt = source.CompletedAt,
        UpdatedAt = timestamp, SubtasksDeletedAt = cutoff
    };
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
