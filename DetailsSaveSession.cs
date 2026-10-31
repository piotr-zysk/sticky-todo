namespace StickyTodo;

// Kept for the lifetime of the task, so reopening a note does not reset its throttle.
public sealed class DetailsSaveSession
{
    readonly Action<string> _write;
    readonly Func<DateTimeOffset> _now;
    DateTimeOffset? _lastSave;
    public string Text { get; private set; }
    public bool IsDirty { get; private set; }
    public bool IsDiscarded { get; private set; }

    public DetailsSaveSession(string text, Action<string> write, Func<DateTimeOffset>? now = null)
    {
        Text = text;
        _write = write;
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    public TimeSpan SaveDelay => _lastSave is { } last
        ? TimeSpan.FromTicks(Math.Max(0, (last.AddMinutes(1) - _now()).Ticks)) : TimeSpan.Zero;

    public void Edit(string text)
    {
        if (IsDiscarded || text == Text) return;
        Text = text;
        IsDirty = true;
    }

    public void Save(bool force = false)
    {
        if (IsDiscarded || !IsDirty || (!force && SaveDelay > TimeSpan.Zero)) return;
        _write(Text);
        _lastSave = _now();
        IsDirty = false;
    }

    public void Discard() { IsDiscarded = true; IsDirty = false; }
}
