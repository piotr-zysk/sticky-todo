using System.IO;

namespace StickyTodo;

public static class TaskTree
{
    public static IEnumerable<TaskItem> Flatten(IEnumerable<TaskItem> roots)
    {
        var seen = new HashSet<Guid>();
        var pending = new Stack<TaskItem>(roots.Reverse());
        while (pending.TryPop(out var task))
        {
            if (!seen.Add(task.Id)) throw new InvalidDataException("Duplicate or cyclic task identity.");
            yield return task;
            foreach (var child in task.Children.Reverse()) pending.Push(child);
        }
    }

    public static List<TaskItem> Build(IEnumerable<TaskItem> items)
    {
        var tasks = items.ToList();
        var byId = tasks.ToDictionary(t => t.Id);
        foreach (var task in tasks) task.Children.Clear();
        foreach (var task in tasks)
        {
            var seen = new HashSet<Guid> { task.Id };
            var current = task;
            while (current.ParentId is { } parentId)
            {
                if (!seen.Add(parentId) || !byId.TryGetValue(parentId, out current!))
                    throw new InvalidDataException("Task hierarchy contains a cycle or missing parent.");
            }
            if (task.ParentId is { } id) byId[id].Children.Add(task);
        }
        return tasks.Where(t => t.ParentId == null).ToList();
    }
}
