namespace StickyTodo;

// Kept for the lifetime of the task, so reopening a note does not reset its throttle.
public sealed class DetailsSaveSession
{
    readonly Action<string> _write;
    readonly Func<DateTimeOffset> _now;
    DateTimeOffset? _lastSave;
    int _autoSaveIntervalSeconds = 60;
    public string Text { get; private set; }
    public bool IsDirty { get; private set; }
    public bool IsDiscarded { get; private set; }
    public int AutoSaveIntervalSeconds
    {
        get => _autoSaveIntervalSeconds;
        set => _autoSaveIntervalSeconds = Math.Clamp(value, 0, 3600);
    }

    public DetailsSaveSession(string text, Action<string> write, Func<DateTimeOffset>? now = null)
    {
        Text = text;
        _write = write;
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    // Null means autosave is disabled; forced saves still bypass scheduling.
    public TimeSpan? SaveDelay => AutoSaveIntervalSeconds == 0 ? null : _lastSave is { } last
        ? TimeSpan.FromTicks(Math.Max(0, (last.AddSeconds(AutoSaveIntervalSeconds) - _now()).Ticks)) : TimeSpan.Zero;

    public void Edit(string text)
    {
        if (IsDiscarded || text == Text) return;
        Text = text;
        IsDirty = true;
    }

    public void Save(bool force = false)
    {
        if (IsDiscarded || !IsDirty || (!force && (SaveDelay is not { } delay || delay > TimeSpan.Zero))) return;
        _write(Text);
        _lastSave = _now();
        IsDirty = false;
    }

    public void Discard() { IsDiscarded = true; IsDirty = false; }

    public void Replace(string text)
    {
        if (IsDirty || IsDiscarded) throw new InvalidOperationException("Cannot replace unsaved or discarded details.");
        Text = text;
    }
}
