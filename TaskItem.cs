using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Threading;

namespace StickyTodo;

public class TaskItem : INotifyPropertyChanged
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public void Touch() => UpdatedAt = NextTimestamp(UpdatedAt);
    public static DateTimeOffset NextTimestamp(DateTimeOffset previous)
    {
        var now = DateTimeOffset.UtcNow;
        return now > previous ? now : previous.AddTicks(1);
    }
    string _title = "";
    DateTime? _date;
    TimeSpan? _time;
    bool _high, _done, _editTitle, _editDue;
    int _drop;

    public DispatcherTimer? RemoveTimer;

    public event PropertyChangedEventHandler? PropertyChanged;
    void Raise([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

    public string Title { get => _title; set { if (_title == value) return; _title = value; Touch(); Raise(); } }
    public bool IsHigh { get => _high; set { if (_high == value) return; _high = value; Touch(); Raise(); } }
    public bool IsDone { get => _done; set { if (_done == value) return; _done = value; Touch(); Raise(); Raise(nameof(IsOverdue)); } }
    public bool IsEditingTitle { get => _editTitle; set { _editTitle = value; Raise(); } }
    public bool IsEditingDue { get => _editDue; set { _editDue = value; Raise(); } }
    public int DropMarker { get => _drop; set { _drop = value; Raise(); } }

    DateTime? _editDate;
    public DateTime? EditDate { get => _editDate; set { _editDate = value; Raise(); } }
    string _editTitleText = "";
    public string EditTitle { get => _editTitleText; set { _editTitleText = value; Raise(); } }
    int? _eh, _em;
    public int? EditHour { get => _eh; set { _eh = value; Raise(); Raise(nameof(EditTimeText)); } }
    public int? EditMinute { get => _em; set { _em = value; Raise(); Raise(nameof(EditTimeText)); } }
    public string EditTimeText => _eh.HasValue ? $"Time: {_eh:00}:{(_em ?? 0):00}" : "Time: none";

    public DateTime? DueDate => _date;
    public TimeSpan? DueTime => _time;

    public void SetDue(DateTime? date, TimeSpan? time)
    {
        date = date?.Date;
        time = date.HasValue ? time : null;
        if (_date == date && _time == time) return;
        _date = date?.Date;
        _time = date.HasValue ? time : null;
        Touch();
        Raise(nameof(DueDate));
        Raise(nameof(DueTime));
        Raise(nameof(DueText));
        Raise(nameof(IsOverdue));
    }

    public DateTime? Due => _date.HasValue ? _date.Value + (_time ?? new TimeSpan(23, 59, 59)) : null;

    public bool IsOverdue => !_done && Due.HasValue && Due.Value < DateTime.Now;

    public string DueText => !_date.HasValue
        ? "+ date"
        : _date.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + (_time.HasValue ? " " + _time.Value.ToString(@"hh\:mm") : "");

    public bool HasDue => _date.HasValue;

    public void RefreshOverdue() => Raise(nameof(IsOverdue));

    public string? DueSerialized => !_date.HasValue
        ? null
        : _date.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + (_time.HasValue ? " " + _time.Value.ToString(@"hh\:mm") : "");

    public static bool TryParseTime(string text, out TimeSpan ts)
    {
        ts = default;
        text = text.Trim();
        if (text.Length is >= 1 and <= 4 && text.All(c => c is >= '0' and <= '9'))
            text = text.Length <= 2 ? text + ":00" : text.Insert(text.Length - 2, ":");
        return TimeSpan.TryParseExact(text, new[] { @"h\:mm", @"hh\:mm" }, CultureInfo.InvariantCulture, out ts)
               && ts >= TimeSpan.Zero && ts < TimeSpan.FromHours(24);
    }
}
