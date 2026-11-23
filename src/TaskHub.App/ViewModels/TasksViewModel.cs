using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using TaskHub.Abstractions.Events;
using TaskHub.Core.Domain;
using TaskHub.Core.Events;
using TaskHub.Core.Hosting;
using TaskHub.Core.Scheduling;
using TaskHub.Core.Services;

namespace TaskHub.App.ViewModels;

/// <summary>
/// The task screen: sidebar (views, lists, tags), quick add with live preview, search, the task
/// list, and the editor for the selected task. Everything goes through <see cref="TaskService"/>;
/// the screen reloads whenever a task/list event arrives — including changes made via the CLI/API.
/// </summary>
public sealed partial class TasksViewModel : ObservableObject, IDisposable
{
    private readonly TaskService _tasks;
    private readonly IUiDispatcher _ui;
    private readonly IClock _clock;
    private readonly IDisposable[] _subscriptions;
    private CancellationTokenSource? _reloadCts;
    private CancellationTokenSource? _searchCts;
    private Dictionary<Guid, string> _listNames = [];
    private Dictionary<Guid, string?> _listColors = [];
    private bool _loadingEditor;
    private readonly System.Windows.Threading.DispatcherTimer _clockTimer;
    private bool _reloading; // list is being rebuilt: selection churn must not reset the editor

    public TasksViewModel(TaskService tasks, IEventBus bus, IUiDispatcher ui, IClock clock)
    {
        _tasks = tasks;
        _ui = ui;
        _clock = clock;
        _subscriptions =
        [
            bus.Subscribe("task.*", _ => ui.Post(ScheduleReload)),
            bus.Subscribe("list.*", _ => ui.Post(ScheduleReload)),
            bus.Subscribe("data.*", _ => ui.Post(ScheduleReload)),
            // Firing and snoozing change a task's next reminder without a task.* event.
            bus.Subscribe("reminder.*", e => ui.Post(() =>
            {
                if (e.Type == EventTypes.ReminderSnoozed && e.DataAs<ReminderSnoozedEvent>() is { } s)
                    ShowStatus($"Snoozed “{s.Task.Title}” until {s.Until.ToLocalTime().ToString("t", CultureInfo.CurrentCulture)}");
                ScheduleReload();
            })),
        ];
        _tasks.Undo.Changed += (_, _) => ui.Post(RefreshUndo);
        PriorityOptions = Enum.GetValues<Priority>();
        // Keep "in 12 min" reminder chips current.
        _clockTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _clockTimer.Tick += (_, _) =>
        {
            foreach (var row in Tasks) row.RefreshTime();
        };
        _clockTimer.Start();

        NavView = CollectionViewSource.GetDefaultView(Nav);
        NavView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(NavItem.Section)));
        TasksView = CollectionViewSource.GetDefaultView(Tasks);
    }

    /// <summary>The task list as shown (grouped into Overdue / Today / … in date-based views).</summary>
    public ICollectionView TasksView { get; }

    /// <summary>Parsed pieces of the quick-add text ("Tomorrow 09:00", "#home", "High").</summary>
    public ObservableCollection<PreviewChip> QuickAddChips { get; } = [];

    /// <summary>Sidebar items grouped into Views / Lists / Tags.</summary>
    public ICollectionView NavView { get; }

    public ObservableCollection<NavItem> Nav { get; } = [];
    public ObservableCollection<TaskRowViewModel> Tasks { get; } = [];
    public ObservableCollection<ListOption> ListOptions { get; } = [];
    public IReadOnlyList<Priority> PriorityOptions { get; }

    [ObservableProperty] private NavItem? _selectedNav;
    [ObservableProperty] private TaskRowViewModel? _selectedTask;
    [ObservableProperty] private string _quickAddText = "";
    [ObservableProperty] private string? _quickAddPreview;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private string _newListName = "";
    [ObservableProperty] private bool _isAddingList;

    // Today's progress ("3 of 7 done"), shown next to the Today heading.
    [ObservableProperty] private bool _showProgress;
    [ObservableProperty] private string? _progressText;
    [ObservableProperty] private System.Windows.Media.Geometry? _progressArc;
    private int _doneToday;
    private int _openToday;
    [ObservableProperty] private string _heading = "All tasks";
    [ObservableProperty] private string? _emptyText;
    [ObservableProperty] private string? _statusMessage;
    [ObservableProperty] private bool _statusIsError;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UndoTooltip))]
    private string? _undoDescription;

    /// <summary>Set after adding a task that the current view doesn't show; the status bar offers "Show".</summary>
    [ObservableProperty] private NavItem? _jumpTarget;
    private Guid? _jumpTaskId;

    public string? UndoTooltip => UndoDescription is null ? null : $"Undo: {UndoDescription}  (Ctrl+Z)";

    // Editor fields (bound to the right-hand panel)
    [ObservableProperty] private bool _hasSelection;
    [ObservableProperty] private bool _selectionIsDeleted;
    [ObservableProperty] private string _editTitle = "";
    [ObservableProperty] private string? _editNotes;
    [ObservableProperty] private DateTime? _editDueDate;
    [ObservableProperty] private string? _editDueTime;
    [ObservableProperty] private Priority _editPriority;
    [ObservableProperty] private string? _editTags;
    [ObservableProperty] private Guid _editListId;
    [ObservableProperty] private string? _editRecurrence;
    [ObservableProperty] private string? _recurrencePreview;
    [ObservableProperty] private bool _recurrenceInvalid;
    [ObservableProperty] private MinutesChoice? _editReminder;
    [ObservableProperty] private MinutesChoice? _editRepeatReminder;
    [ObservableProperty] private string? _nextReminderText;
    [ObservableProperty] private string? _editReminderActions;
    [ObservableProperty] private string? _reminderActionsHint;
    [ObservableProperty] private bool _reminderActionsInvalid;
    [ObservableProperty] private bool _editorDirty;
    private bool _attachmentsChanged;

    /// <summary>Files attached to the selected task (screenshots, captured files).</summary>
    public ObservableCollection<AttachmentRow> Attachments { get; } = [];
    private bool _reminderChoiceTouched;
    private string? _originalRecurrenceText;

    public IReadOnlyList<MinutesChoice> ReminderChoices { get; } = MinutesChoice.ReminderChoices;
    public IReadOnlyList<MinutesChoice> RepeatChoices { get; } = MinutesChoice.RepeatChoices;
    [ObservableProperty] private string? _editInfo;

    public bool IsTrashView => SelectedNav?.Kind == NavKind.View && SelectedNav.View == TaskView.Trash;

    /// <summary>The list being viewed (for the colour button next to the heading), or null.</summary>
    public NavItem? SelectedList => SelectedNav?.Kind == NavKind.List ? SelectedNav : null;

    // ------------------------------------------------------------------ loading

    public async Task InitializeAsync()
    {
        await RebuildNavAsync();
        SelectedNav ??= Nav.FirstOrDefault(n => n.Kind == NavKind.View && n.View == TaskView.Today);
        RefreshUndo();
    }

    private void ScheduleReload()
    {
        _reloadCts?.Cancel();
        _reloadCts = new CancellationTokenSource();
        var token = _reloadCts.Token;
        _ = Task.Delay(80, token).ContinueWith(async t =>
        {
            if (t.IsCanceled) return;
            await _ui.Invoke(ReloadAllAsync);
        }, TaskScheduler.Default);
    }

    private async Task ReloadAllAsync()
    {
        await RebuildNavAsync();
        await LoadTasksAsync();
    }

    private async Task RebuildNavAsync()
    {
        try
        {
            var lists = await _tasks.GetListsAsync();
            var tags = await _tasks.GetTagsAsync();
            var inbox = await _tasks.CountInboxAsync();
            var today = (await _tasks.QueryAsync(new TaskQuery { View = TaskView.Today })).Count;
            var overdue = (await _tasks.QueryAsync(new TaskQuery { View = TaskView.Overdue })).Count;
            var completed = await _tasks.QueryAsync(new TaskQuery { View = TaskView.Completed });
            var todayDate = _clock.Today();
            _doneToday = completed.Count(t => t.CompletedUtc is { } c && DateOnly.FromDateTime(c.ToLocalTime().DateTime) == todayDate);
            _openToday = today;
            UpdateProgress();
            _listNames = lists.ToDictionary(l => l.Id, l => l.Name);
            _listColors = lists.ToDictionary(l => l.Id, l => l.Color);

            var selectedKey = SelectedNav?.Key;
            var items = new List<NavItem>
            {
                new(NavKind.View, "Views", "Inbox", "") { View = TaskView.Inbox, Count = inbox },
                new(NavKind.View, "Views", "Today", "") { View = TaskView.Today, Count = today },
                new(NavKind.View, "Views", "Upcoming", "") { View = TaskView.Upcoming },
                new(NavKind.View, "Views", "Overdue", "") { View = TaskView.Overdue, Count = overdue },
                new(NavKind.View, "Views", "All tasks", "") { View = TaskView.All },
                new(NavKind.View, "Views", "Completed", "") { View = TaskView.Completed },
                new(NavKind.View, "Views", "Trash", "") { View = TaskView.Trash },
            };
            items.AddRange(lists.Select(l => new NavItem(NavKind.List, "Lists", l.Name, ListIcons.GlyphFor(l.Icon)) { ListId = l.Id, Count = l.OpenCount, Color = l.Color, Icon = l.Icon }));
            items.AddRange(tags.Select(t => new NavItem(NavKind.Tag, "Tags", "#" + t.Name, "") { Tag = t.Name, Count = t.OpenCount }));

            Nav.Clear();
            foreach (var item in items) Nav.Add(item);

            ListOptions.Clear();
            ListOptions.Add(new ListOption(Guid.Empty, "Inbox"));
            foreach (var l in lists) ListOptions.Add(new ListOption(l.Id, l.Name, l.Color));

            // Keep the same sidebar selection (by key) without triggering a second load.
            var reselect = Nav.FirstOrDefault(n => n.Key == selectedKey);
            if (reselect is not null)
            {
#pragma warning disable MVVMTK0034 // set the field directly: we're restoring, not changing, the selection
                _selectedNav = reselect;
#pragma warning restore MVVMTK0034
                OnPropertyChanged(nameof(SelectedNav));
                OnPropertyChanged(nameof(SelectedList));
            }
            else if (selectedKey is not null)
            {
                SelectedNav = Nav.First(n => n.Kind == NavKind.View && n.View == TaskView.All);
            }
        }
        catch (Exception ex)
        {
            ShowError("Could not load lists", ex);
        }
    }

    private async Task LoadTasksAsync()
    {
        var nav = SelectedNav;
        if (nav is null) return;
        try
        {
            var query = nav.Kind switch
            {
                NavKind.List => new TaskQuery { ListId = nav.ListId },
                NavKind.Tag => new TaskQuery { Tag = nav.Tag },
                _ => new TaskQuery { View = nav.View },
            };
            if (!string.IsNullOrWhiteSpace(SearchText))
                query = query with { Text = SearchText.Trim(), IncludeCompleted = true };

            var tasks = await _tasks.QueryAsync(query);
            var today = _clock.Today();
            var now = _clock.UtcNow;
            var showList = nav.Kind != NavKind.List;
            var selectedId = SelectedTask?.Id;
            // Date sections only make sense where tasks have mixed dates.
            var grouped = !(nav.Kind == NavKind.View && nav.View is TaskView.Trash or TaskView.Completed or TaskView.Overdue or TaskView.Inbox);

            var rows = tasks.Select(t => new TaskRowViewModel(t,
                t.ListId is { } id ? _listNames.GetValueOrDefault(id) : null, today, now, showList,
                t.ListId is { } cid ? _listColors.GetValueOrDefault(cid) : null, grouped));
            if (grouped) rows = rows.OrderBy(r => r.GroupRank); // stable: keeps the query's order inside a section

            _reloading = true;
            try
            {
                Tasks.Clear();
                foreach (var row in rows) Tasks.Add(row);
                using (TasksView.DeferRefresh())
                {
                    TasksView.GroupDescriptions.Clear();
                    // A single "Today" heading over the Today view adds nothing; show sections once there are two.
                    if (grouped && Tasks.Select(r => r.GroupRank).Distinct().Count() > 1)
                        TasksView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(TaskRowViewModel.Group)));
                }
                SelectedTask = selectedId is { } sid ? Tasks.FirstOrDefault(r => r.Id == sid) : null;
            }
            finally
            {
                _reloading = false;
            }

            // Keep unsaved edits unless the task left this view.
            if (!EditorDirty || SelectedTask is null) LoadEditor(SelectedTask);
            else SelectionIsDeleted = SelectedTask.IsDeleted;

            Heading = string.IsNullOrWhiteSpace(SearchText) ? nav.Title : $"{nav.Title} · “{SearchText.Trim()}”";
            EmptyText = Tasks.Count > 0 ? null : nav.View switch
            {
                _ when !string.IsNullOrWhiteSpace(SearchText) => "No tasks match your search.",
                TaskView.Today when nav.Kind == NavKind.View => "Nothing due today. 🎉",
                TaskView.Overdue when nav.Kind == NavKind.View => "Nothing overdue.",
                TaskView.Trash when nav.Kind == NavKind.View => "The trash is empty.",
                TaskView.Completed when nav.Kind == NavKind.View => "No completed tasks yet.",
                _ => "No tasks here. Add one above.",
            };

            OnPropertyChanged(nameof(IsTrashView));
            OnPropertyChanged(nameof(SelectedList));
            ShowProgress = nav.Kind == NavKind.View && nav.View == TaskView.Today && string.IsNullOrWhiteSpace(SearchText);
        }
        catch (Exception ex)
        {
            ShowError("Could not load tasks", ex);
        }
    }

    private void UpdateProgress()
    {
        var total = _doneToday + _openToday;
        ProgressText = total == 0 ? "Nothing planned" : $"{_doneToday} of {total} done";
        ProgressArc = BuildArc(total == 0 ? 0 : (double)_doneToday / total);
    }

    /// <summary>A ring segment for the progress indicator (18×18 box, radius 7.5, starting at 12 o'clock).</summary>
    private static System.Windows.Media.Geometry? BuildArc(double fraction)
    {
        if (fraction <= 0) return null;
        const double c = 9, r = 7.5;
        if (fraction >= 0.999)
        {
            var full = new System.Windows.Media.EllipseGeometry(new System.Windows.Point(c, c), r, r);
            full.Freeze();
            return full;
        }
        var angle = fraction * 2 * Math.PI;
        var start = new System.Windows.Point(c, c - r);
        var end = new System.Windows.Point(c + r * Math.Sin(angle), c - r * Math.Cos(angle));
        var geometry = new System.Windows.Media.StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(start, false, false);
            ctx.ArcTo(end, new System.Windows.Size(r, r), 0, fraction > 0.5, System.Windows.Media.SweepDirection.Clockwise, true, false);
        }
        geometry.Freeze();
        return geometry;
    }

    partial void OnSelectedNavChanged(NavItem? value)
    {
        if (value is null) return;
        EditorDirty = false;
        SelectedTask = null;
        _ = LoadTasksAsync();
    }

    partial void OnSearchTextChanged(string value)
    {
        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        var token = _searchCts.Token;
        _ = Task.Delay(200, token).ContinueWith(async t =>
        {
            if (!t.IsCanceled) await _ui.Invoke(LoadTasksAsync);
        }, TaskScheduler.Default);
    }

    // ------------------------------------------------------------------ quick add

    partial void OnQuickAddTextChanged(string value)
    {
        QuickAddChips.Clear();
        if (string.IsNullOrWhiteSpace(value))
        {
            QuickAddPreview = null;
            return;
        }
        var p = _tasks.Preview(value);
        if (p.DueDate is { } dueDate) QuickAddChips.Add(new PreviewChip("\uE787", TaskRowViewModel.FormatDue(dueDate, p.DueTime, _clock.Today())));
        if (p.Priority is { } prio && prio != Priority.None)
            QuickAddChips.Add(new PreviewChip("\uE7C1", prio.ToString(), prio switch
            {
                Priority.Urgent => "#E5484D",
                Priority.High => "#F76B15",
                Priority.Medium => "#3E8EF7",
                _ => null,
            }));
        if (p.Recurrence is { } rec) QuickAddChips.Add(new PreviewChip("\uE8EE", Recurrence.Describe(rec)));
        if (p.ListName is { } listName)
        {
            var color = _listNames.Where(kv => string.Equals(kv.Value, listName.Trim(), StringComparison.OrdinalIgnoreCase))
                .Select(kv => _listColors.GetValueOrDefault(kv.Key)).FirstOrDefault();
            QuickAddChips.Add(new PreviewChip("\uE8B7", listName, color));
        }
        foreach (var tag in p.Tags) QuickAddChips.Add(new PreviewChip("\uE8EC", "#" + tag));
        var parts = new List<string> { $"“{p.Title}”" };
        if (p.DueDate is { } d) parts.Add("📅 " + TaskRowViewModel.FormatDue(d, p.DueTime, _clock.Today()));
        if (p.Priority is { } pr && pr != Priority.None) parts.Add("⚑ " + pr);
        if (p.Recurrence is { } r) parts.Add("↻ " + Recurrence.Describe(r));
        if (p.ListName is { } l) parts.Add("@" + l);
        if (p.Tags.Count > 0) parts.Add(string.Join(' ', p.Tags.Select(t => "#" + t)));
        QuickAddPreview = string.Join("   ", parts);
    }

    [RelayCommand]
    private async Task QuickAddAsync()
    {
        var text = QuickAddText.Trim();
        if (text.Length == 0) return;
        try
        {
            var p = _tasks.Preview(text);
            var nav = SelectedNav;

            // Context-aware defaults: adding while viewing a list/tag/Today fills those in.
            Guid? listId = nav?.Kind == NavKind.List ? nav.ListId : null;
            if (p.ListName is { } name)
            {
                var existing = _listNames.Where(kv => string.Equals(kv.Value, name.Trim(), StringComparison.OrdinalIgnoreCase))
                    .Select(kv => (Guid?)kv.Key).FirstOrDefault();
                listId = existing ?? (await _tasks.CreateListAsync(name, null, EventSources.Ui)).Id;
            }
            var tags = p.Tags.ToList();
            if (nav?.Kind == NavKind.Tag && nav.Tag is { } navTag && !tags.Contains(navTag)) tags.Add(navTag);
            var due = p.DueDate ?? (nav?.Kind == NavKind.View && nav.View == TaskView.Today ? _clock.Today() : null);

            var task = await _tasks.CreateAsync(new NewTask
            {
                Title = p.Title,
                DueDate = due,
                DueTime = p.DueTime,
                Priority = p.Priority ?? Priority.None,
                Tags = tags,
                ListId = listId,
                Recurrence = p.Recurrence,
            }, EventSources.Ui);

            QuickAddText = "";
            await ReloadAllAsync();
            var row = Tasks.FirstOrDefault(r => r.Id == task.Id);
            if (row is not null)
            {
                SelectedTask = row;
                ShowStatus($"Added “{task.Title}”");
            }
            else
            {
                var home = HomeFor(task);
                ShowStatus($"Added “{task.Title}” to {home?.Title ?? "your tasks"} — it isn't shown in {nav?.Title}.");
                JumpTarget = home;
                _jumpTaskId = task.Id;
            }
        }
        catch (Exception ex)
        {
            ShowError("Could not add the task", ex);
        }
    }

    /// <summary>The sidebar entry where a task naturally shows up.</summary>
    private NavItem? HomeFor(TaskItem task)
    {
        if (task.ListId is { } listId) return Nav.FirstOrDefault(n => n.Kind == NavKind.List && n.ListId == listId);
        var view = task.DueDate is { } d ? (d <= _clock.Today() ? TaskView.Today : TaskView.Upcoming) : TaskView.Inbox;
        return Nav.FirstOrDefault(n => n.Kind == NavKind.View && n.View == view);
    }

    /// <summary>Selects a sidebar view (used by the launcher).</summary>
    public void ShowView(TaskView view)
    {
        SearchText = "";
        SelectedNav = Nav.FirstOrDefault(n => n.Kind == NavKind.View && n.View == view) ?? SelectedNav;
    }

    /// <summary>Navigates to where the task(s) live and selects them (used by "Open" on a banner).</summary>
    public async Task ShowTasksAsync(IReadOnlyList<Guid> ids)
    {
        if (ids.Count == 0) return;
        SearchText = "";
        if (ids.Count == 1 && await _tasks.GetAsync(ids[0]) is { } task)
        {
            SelectedNav = HomeFor(task) ?? Nav.First(n => n.Kind == NavKind.View && n.View == TaskView.All);
        }
        else
        {
            SelectedNav = Nav.First(n => n.Kind == NavKind.View && n.View == TaskView.Today);
        }
        await LoadTasksAsync();
        SelectedTask = Tasks.FirstOrDefault(r => r.Id == ids[0]);
    }

    [RelayCommand]
    private async Task JumpToAddedAsync()
    {
        if (JumpTarget is not { } target) return;
        var id = _jumpTaskId;
        SelectedNav = Nav.FirstOrDefault(n => n.Key == target.Key) ?? target;
        await LoadTasksAsync();
        SelectedTask = Tasks.FirstOrDefault(r => r.Id == id);
        JumpTarget = null;
    }

    // ------------------------------------------------------------------ task actions

    [RelayCommand(AllowConcurrentExecutions = true)] // several rows can fade out at once
    private async Task ToggleCompleteAsync(TaskRowViewModel? row)
    {
        row ??= SelectedTask;
        if (row is null || row.IsDeleted) return;
        try
        {
            if (row.IsCompleted) await _tasks.ReopenAsync(row.Id, EventSources.Ui);
            else
            {
                if (row.IsCompleting) return; // second click while it fades
                row.IsCompleting = true;      // the row fades and slides out first
                await Task.Delay(320);
                var after = await _tasks.CompleteAsync(row.Id, EventSources.Ui);
                ShowStatus(after.IsOpen && after.DueDate is { } next
                    ? $"Completed “{row.Title}” — next: {TaskRowViewModel.FormatDue(next, after.DueTime, _clock.Today())}"
                    : $"Completed “{row.Title}”");
            }
        }
        catch (Exception ex)
        {
            row.IsCompleting = false;
            ShowError("Could not update the task", ex);
        }
    }

    /// <summary>Row action: move the task to tomorrow (keeping its time).</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task PostponeAsync(TaskRowViewModel? row)
    {
        row ??= SelectedTask;
        if (row is null || row.IsDeleted || row.IsCompleted) return;
        try
        {
            var tomorrow = _clock.Today().AddDays(1);
            await _tasks.UpdateAsync(row.Id, new TaskPatch { DueDate = (DateOnly?)tomorrow, DueTime = row.Task.DueTime }, EventSources.Ui);
            ShowStatus($"Moved “{row.Title}” to tomorrow");
        }
        catch (Exception ex)
        {
            ShowError("Could not move the task", ex);
        }
    }

    /// <summary>Editor close button / Esc: saves pending edits, then hides the editor.</summary>
    [RelayCommand]
    private async Task CloseEditorAsync()
    {
        if (EditorDirty) await SaveEditorAsync();
        if (EditorDirty) return; // save failed: keep the editor open so nothing is lost
        SelectedTask = null;
    }

    [RelayCommand]
    private async Task SetListColorAsync(ListColorAction? choice)
    {
        if (choice is null) return;
        try
        {
            await _tasks.SetListColorAsync(choice.ListId, choice.Hex, EventSources.Ui);
        }
        catch (Exception ex)
        {
            ShowError("Could not change the colour", ex);
        }
    }

    [RelayCommand]
    private async Task SetListIconAsync(ListIconAction? choice)
    {
        if (choice is null) return;
        try
        {
            await _tasks.SetListIconAsync(choice.ListId, choice.Key, EventSources.Ui);
        }
        catch (Exception ex)
        {
            ShowError("Could not change the icon", ex);
        }
    }

    [RelayCommand]
    private void StartNewList()
    {
        NewListName = "";
        IsAddingList = true;
    }

    [RelayCommand]
    private void CancelNewList()
    {
        NewListName = "";
        IsAddingList = false;
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task DeleteSelectedAsync(TaskRowViewModel? row)
    {
        row ??= SelectedTask;
        if (row is null) return;
        try
        {
            if (row.IsDeleted) return;
            if (row == SelectedTask) EditorDirty = false;
            await _tasks.DeleteAsync(row.Id, EventSources.Ui);
            ShowStatus($"Moved “{row.Title}” to the trash");
        }
        catch (Exception ex)
        {
            ShowError("Could not delete the task", ex);
        }
    }

    [RelayCommand]
    private async Task RestoreSelectedAsync(TaskRowViewModel? row)
    {
        row ??= SelectedTask;
        if (row is null) return;
        try
        {
            await _tasks.RestoreAsync(row.Id, EventSources.Ui);
            ShowStatus($"Restored “{row.Title}”");
        }
        catch (Exception ex)
        {
            ShowError("Could not restore the task", ex);
        }
    }

    [RelayCommand]
    private async Task UndoAsync()
    {
        try
        {
            EditorDirty = false;
            var what = await _tasks.UndoAsync(EventSources.Ui);
            ShowStatus(what is null ? "Nothing to undo" : $"Undid: {what}");
        }
        catch (Exception ex)
        {
            ShowError("Undo failed", ex);
        }
    }

    [RelayCommand]
    private async Task CreateListAsync()
    {
        var name = NewListName.Trim();
        if (name.Length == 0) return;
        try
        {
            var list = await _tasks.CreateListAsync(name, null, EventSources.Ui);
            NewListName = "";
            IsAddingList = false;
            await RebuildNavAsync();
            SelectedNav = Nav.FirstOrDefault(n => n.ListId == list.Id);
        }
        catch (Exception ex)
        {
            ShowError("Could not create the list", ex);
        }
    }

    private void RefreshUndo() => UndoDescription = _tasks.Undo.Peek();

    // ------------------------------------------------------------------ editor

    partial void OnSelectedTaskChanged(TaskRowViewModel? value)
    {
        if (!_reloading) LoadEditor(value);
    }

    private void LoadEditor(TaskRowViewModel? value)
    {
        _loadingEditor = true;
        try
        {
            HasSelection = value is not null;
            SelectionIsDeleted = value?.IsDeleted ?? false;
            var t = value?.Task;
            EditTitle = t?.Title ?? "";
            EditNotes = t?.Notes;
            EditDueDate = t?.DueDate?.ToDateTime(TimeOnly.MinValue);
            EditDueTime = t?.DueTime?.ToString("HH:mm", CultureInfo.InvariantCulture);
            EditPriority = t?.Priority ?? Priority.None;
            EditTags = t is null ? null : string.Join(" ", t.Tags.Select(x => "#" + x));
            EditListId = t?.ListId ?? Guid.Empty;
            EditInfo = t is null ? null : BuildInfo(t);
            EditRecurrence = t?.Recurrence is { } rule ? Recurrence.Describe(rule) : null;
            _originalRecurrenceText = EditRecurrence;
            EditReminder = t is null ? null
                : MinutesChoice.ReminderChoices.FirstOrDefault(c => t.ReminderOffsets.Count == 0 ? c.Minutes is null : c.Minutes == t.ReminderOffsets[0])
                  ?? new MinutesChoice($"{t.ReminderOffsets[0]} minutes before", t.ReminderOffsets[0]);
            EditRepeatReminder = MinutesChoice.RepeatChoices.FirstOrDefault(c => c.Minutes == t?.RepeatReminderMinutes) ?? MinutesChoice.RepeatChoices[0];
            EditReminderActions = t?.ReminderActions;
            OnEditReminderActionsChanged(EditReminderActions); // refresh the hint even if the text didn't change
            Attachments.Clear();
            foreach (var path in t?.Attachments ?? []) Attachments.Add(new AttachmentRow(path));
            _attachmentsChanged = false;
            NextReminderText = t?.NextReminderUtc is { } nr ? $"Next reminder: {nr.ToLocalTime().ToString("dddd d MMM, t", CultureInfo.CurrentCulture)}"
                : t?.DueDate is null ? "Reminders need a due date." : null;
            _reminderChoiceTouched = false;
            EditorDirty = false;
        }
        finally
        {
            _loadingEditor = false;
        }
    }

    private static string BuildInfo(TaskItem t)
    {
        var culture = CultureInfo.CurrentCulture;
        var info = $"Created {t.CreatedUtc.ToLocalTime().ToString("g", culture)}";
        if (t.CompletedUtc is { } c) info += $" · completed {c.ToLocalTime().ToString("g", culture)}";
        if (t.DeletedUtc is { } d) info += $" · deleted {d.ToLocalTime().ToString("g", culture)}";
        return info;
    }

    partial void OnEditTitleChanged(string value) => MarkDirty();
    partial void OnEditNotesChanged(string? value) => MarkDirty();
    partial void OnEditDueDateChanged(DateTime? value) => MarkDirty();
    partial void OnEditDueTimeChanged(string? value) => MarkDirty();
    partial void OnEditPriorityChanged(Priority value) => MarkDirty();
    partial void OnEditTagsChanged(string? value) => MarkDirty();
    partial void OnEditListIdChanged(Guid value) => MarkDirty();
    partial void OnEditRepeatReminderChanged(MinutesChoice? value) => MarkDirty();

    partial void OnEditReminderActionsChanged(string? value)
    {
        MarkDirty();
        if (string.IsNullOrWhiteSpace(value))
        {
            ReminderActionsHint = "e.g. open https://meet.example.com/standup — one action per line";
            ReminderActionsInvalid = false;
        }
        else if (Core.Automation.ActionScript.TryParse(value, out var actions, out var error))
        {
            ReminderActionsHint = actions.Count == 1 ? "1 action runs with the first reminder" : $"{actions.Count} actions run with the first reminder";
            ReminderActionsInvalid = false;
        }
        else
        {
            ReminderActionsHint = error;
            ReminderActionsInvalid = true;
        }
    }

    [RelayCommand]
    private void OpenAttachment(AttachmentRow? row)
    {
        if (row is null) return;
        if (!System.IO.File.Exists(row.Path))
        {
            ShowStatus($"“{row.Name}” isn't there any more.");
            StatusIsError = true;
            return;
        }
        if (AttachmentRow.IsRunnable(row.Path))
        {
            // Don't launch programs/scripts from a click on an attachment: show them in Explorer instead.
            TrayIconService.OpenPath("/select,\"" + row.Path + "\"", "explorer.exe");
            return;
        }
        TrayIconService.OpenPath(row.Path);
    }

    [RelayCommand]
    private void ShowAttachmentInFolder(AttachmentRow? row)
    {
        if (row is not null) TrayIconService.OpenPath("/select,\"" + row.Path + "\"", "explorer.exe");
    }

    [RelayCommand]
    private void RemoveAttachment(AttachmentRow? row)
    {
        if (row is null) return;
        Attachments.Remove(row);
        _attachmentsChanged = true;
        MarkDirty();
    }

    partial void OnEditReminderChanged(MinutesChoice? value)
    {
        if (!_loadingEditor) _reminderChoiceTouched = true;
        MarkDirty();
    }

    partial void OnEditRecurrenceChanged(string? value)
    {
        MarkDirty();
        if (string.IsNullOrWhiteSpace(value))
        {
            RecurrencePreview = "Does not repeat";
            RecurrenceInvalid = false;
        }
        else if (Recurrence.TryParse(value, out var rule, out var error))
        {
            RecurrencePreview = Recurrence.Describe(rule);
            RecurrenceInvalid = false;
        }
        else
        {
            RecurrencePreview = error;
            RecurrenceInvalid = true;
        }
    }

    private void MarkDirty()
    {
        if (!_loadingEditor && HasSelection) EditorDirty = true;
    }

    [RelayCommand]
    private async Task SaveEditorAsync()
    {
        if (SelectedTask is not { } row || !EditorDirty) return;
        try
        {
            TimeOnly? time = string.IsNullOrWhiteSpace(EditDueTime) ? null : ParseTime(EditDueTime);
            await _tasks.UpdateAsync(row.Id, new TaskPatch
            {
                Title = EditTitle,
                Notes = EditNotes,
                DueDate = EditDueDate is { } d ? DateOnly.FromDateTime(d) : (DateOnly?)null,
                DueTime = time,
                Priority = EditPriority,
                Tags = Core.Domain.Tags.Parse(EditTags),
                ListId = EditListId == Guid.Empty ? (Guid?)null : EditListId,
                // The field shows a description ("Every 2 weeks on Mon, Thu"); only re-parse it if the user edited it.
                Recurrence = EditRecurrence == _originalRecurrenceText
                    ? default
                    : new Optional<string?>(string.IsNullOrWhiteSpace(EditRecurrence) ? null : EditRecurrence),
                // Only overwrite reminder offsets when the drop-down was changed (tasks from the API may have several).
                ReminderOffsets = _reminderChoiceTouched
                    ? new Optional<IReadOnlyList<int>>(EditReminder?.Minutes is { } m ? [m] : [])
                    : default,
                RepeatReminderMinutes = EditRepeatReminder?.Minutes,
                ReminderActions = string.IsNullOrWhiteSpace(EditReminderActions) ? null : EditReminderActions,
                Attachments = _attachmentsChanged
                    ? new Optional<IReadOnlyList<string>>(Attachments.Select(a => a.Path).ToList())
                    : default,
            }, EventSources.Ui);
            EditorDirty = false;
            ShowStatus("Saved");
        }
        catch (Exception ex)
        {
            ShowError("Could not save", ex);
        }
    }

    [RelayCommand]
    private void RevertEditor()
    {
        LoadEditor(SelectedTask);
    }

    /// <summary>Accepts 9:30, 0930, 9.30, 9am, 9:30 pm.</summary>
    internal static TimeOnly ParseTime(string text)
    {
        var s = text.Trim().ToLowerInvariant().Replace('.', ':');
        string[] formats = ["H:mm", "HH:mm", "Hmm", "HHmm", "h:mmtt", "h:mm tt", "htt", "h tt", "H"];
        if (DateTime.TryParseExact(s.ToUpperInvariant(), formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
            return TimeOnly.FromDateTime(dt);
        throw new TaskValidationException($"Invalid time '{text}'. Use e.g. 09:30 or 9:30 PM.");
    }

    // ------------------------------------------------------------------ status

    private void ShowStatus(string message)
    {
        JumpTarget = null;
        StatusIsError = false;
        StatusMessage = message;
    }

    private void ShowError(string what, Exception ex)
    {
        if (ex is not (TaskValidationException or TaskNotFoundException))
            Log.Error(ex, "{What}", what);
        JumpTarget = null;
        StatusIsError = true;
        StatusMessage = ex is TaskValidationException or TaskNotFoundException ? ex.Message : $"{what}: {ex.Message}";
    }

    public void Dispose()
    {
        _clockTimer.Stop();
        foreach (var s in _subscriptions) s.Dispose();
        _reloadCts?.Dispose();
        _searchCts?.Dispose();
    }
}

/// <summary>An attached file in the editor.</summary>
public sealed record AttachmentRow(string Path)
{
    private static readonly string[] RunnableExtensions =
        [".exe", ".bat", ".cmd", ".com", ".ps1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh", ".msi", ".msp", ".scr", ".lnk", ".url", ".hta", ".cpl", ".reg", ".pif", ".appref-ms"];

    public string Name => System.IO.Path.GetFileName(Path);
    public string Folder => System.IO.Path.GetDirectoryName(Path) ?? "";
    public bool IsImage => System.IO.Path.GetExtension(Path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp";

    /// <summary>Programs and scripts: clicking shows them in Explorer rather than running them.</summary>
    public static bool IsRunnable(string path) =>
        RunnableExtensions.Contains(System.IO.Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
}
