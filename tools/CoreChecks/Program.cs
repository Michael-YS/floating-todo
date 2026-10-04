using FloatingTodo;
using System.IO;

var failures = new List<string>();
void Check(bool condition, string message)
{
    if (!condition) failures.Add(message);
}

var root = Path.Combine(Path.GetTempPath(), "FloatingTodo-CoreChecks");
Directory.CreateDirectory(root);
var databasePath = Path.Combine(root, $"checks-{Guid.NewGuid():N}.db");

try
{
    var codec = new ICalendarCodec();
    var editorThread = new Thread(() =>
    {
        try { EditorChecks.Run(Check); }
        catch (Exception ex) { Check(false, "recurrence editor check failed: " + ex); }
    });
    editorThread.SetApartmentState(ApartmentState.STA);
    editorThread.Start();
    editorThread.Join();
    var fortnightly = new RecurrenceConfiguration { Frequency = "WEEKLY", Interval = 2, Days = ["MO", "WE", "FR"], Count = 6 };
    Check(fortnightly.Build(false) == "FREQ=WEEKLY;INTERVAL=2;BYDAY=MO,WE,FR;COUNT=6", "weekly configuration generated an incorrect rule");
    var lastMonday = new RecurrenceConfiguration { Frequency = "MONTHLY", Days = ["MO"], Position = -1, Count = 2 };
    Check(RecurrenceConfiguration.TryParse(lastMonday.Build(true), true, out var loadedMonthly) && loadedMonthly.Position == -1,
        "monthly weekday configuration did not load");
    Check(RecurrenceConfiguration.TryParse("FREQ=MONTHLY;BYDAY=-1MO", true, out var ordinalMonthly) && ordinalMonthly.Position == -1 && ordinalMonthly.Days.SequenceEqual(new[] { "MO" }),
        "ordinal weekday rule did not load into visual mode");
    var monthDates = new RecurrenceConfiguration { Frequency = "MONTHLY", MonthDays = "1, 15,-1" };
    Check(monthDates.Build(false) == "FREQ=MONTHLY;BYMONTHDAY=1,15,-1", "monthly dates configuration failed");
    foreach (var unsupported in new[] { "FREQ=YEARLY;BYMONTH=3;BYDAY=-1SU", "FREQ=WEEKLY;WKST=SU;BYDAY=MO", "FREQ=MONTHLY;BYDAY=MO,FR;BYSETPOS=-1", "FREQ=DAILY;COUNT=2;COUNT=3" })
        Check(!RecurrenceConfiguration.TryParse(unsupported, true, out _), "unsupported rule would lose fields in visual mode: " + unsupported);
    foreach (var invalid in new[] { new RecurrenceConfiguration { Interval = 0 }, new RecurrenceConfiguration { Count = 0 },
        new RecurrenceConfiguration { Frequency = "MONTHLY", MonthDays = "0" }, new RecurrenceConfiguration { Frequency = "MONTHLY", MonthDays = "32" },
        new RecurrenceConfiguration { Count = 2, Until = DateTime.Today } })
    {
        var rejected = false;
        try { invalid.Build(false); } catch (FormatException) { rejected = true; }
        Check(rejected, "invalid recurrence configuration was accepted");
    }
    foreach (var timed in new[] { false, true })
    {
        var ending = new RecurrenceConfiguration { Frequency = "DAILY", Until = new DateTime(2026, 10, 31) };
        var endingRule = ending.Build(timed);
        codec.ValidateRecurrence(endingRule);
        Check(RecurrenceConfiguration.TryParse(endingRule, timed, out var loadedEnd) && loadedEnd.Until == ending.Until,
            "recurrence end date did not round-trip");
        var endingTask = new TodoItem { Title = "Until", Due = new DateTime(2026, 10, 30, timed ? 9 : 0, 0, 0), HasDueTime = timed, RecurrenceRule = endingRule };
        endingTask.RawICalendar = codec.Serialize(endingTask);
        var next = codec.CompleteOccurrence(endingTask, DateTimeOffset.Now).NextOccurrence;
        Check(next?.Due?.Date == new DateTime(2026, 10, 31), $"end date excluded its final day (timed={timed}): {next?.Due:O}");
        if (next is not null) Check(codec.CompleteOccurrence(next, DateTimeOffset.Now).NextOccurrence is null, "recurrence continued after UNTIL");
    }
    var monthlyTask = new TodoItem { Title = "Last Monday", Start = new DateTime(2026, 10, 26, 9, 0, 0), Due = new DateTime(2026, 10, 26, 10, 0, 0), HasDueTime = true, RecurrenceRule = lastMonday.Build(true) };
    monthlyTask.RawICalendar = codec.Serialize(monthlyTask);
    var nextMonthly = codec.CompleteOccurrence(monthlyTask, DateTimeOffset.Now).NextOccurrence;
    Check(nextMonthly?.Due == new DateTime(2026, 11, 30, 10, 0, 0), $"last weekday recurrence did not advance correctly: {nextMonthly?.Due:O}");
    if (nextMonthly is not null)
    {
        nextMonthly.Title = "Edited second occurrence";
        nextMonthly.RawICalendar = codec.Serialize(nextMonthly);
        Check(codec.CompleteOccurrence(nextMonthly, DateTimeOffset.Now).NextOccurrence is null, "COUNT restarted after editing the next occurrence");
    }
    var database = new AppDatabase(databasePath);
    database.UpsertAccount(new AccountRecord("test", "Test", "https://example.invalid/", "tester", true));
    database.UpsertCalendar(new CalendarRecord("calendar", "test", "https://example.invalid/tasks/", "Tasks", "#55C9B0", false, null, null));

    var discoveryUpdate = CalendarDiscoveryUpdater.Apply(database, "test",
    [
        new DiscoveredCalendar("https://example.invalid/tasks/", "Renamed Tasks", "ctag-1", "sync-1"),
        new DiscoveredCalendar("https://example.invalid/new/", "New Tasks", "ctag-2", "sync-2")
    ], ["#55C9B0", "#6FA8FF"]);
    var discoveredCalendars = database.GetCalendars("test");
    Check(discoveryUpdate == new CalendarDiscoveryUpdate(2, 1), "calendar rediscovery counts were incorrect");
    Check(discoveredCalendars.Count == 2, "calendar rediscovery did not add exactly one calendar");
    Check(discoveredCalendars.Single(calendar => calendar.Id == "calendar") is { DisplayName: "Renamed Tasks", Color: "#55C9B0", Enabled: false, CTag: "ctag-1", SyncToken: "sync-1" },
        "calendar rediscovery did not update remote state while preserving presentation");
    Check(discoveredCalendars.Single(calendar => calendar.RemoteUrl.EndsWith("/new/", StringComparison.Ordinal)).Enabled,
        "newly discovered calendar was not enabled");
    database.UpdateCalendarPresentation("calendar", true, "#55C9B0");

    var task = new TodoItem
    {
        Title = "月底复盘",
        Description = "保留未知属性",
        ListId = "calendar",
        Start = new DateTime(2026, 7, 22, 9, 30, 0),
        Due = new DateTime(2026, 7, 22, 17, 0, 0),
        HasDueTime = true,
        Priority = TodoPriority.High,
        RecurrenceRule = "FREQ=MONTHLY;BYDAY=MO;BYSETPOS=-1"
    };
    var raw = codec.Serialize(task).Replace("END:VTODO", "X-FLOATING-TEST:keep\r\nEND:VTODO", StringComparison.Ordinal);
    task.RawICalendar = raw;
    var updated = codec.Serialize(task);
    Check(updated.Contains("X-FLOATING-TEST:keep", StringComparison.Ordinal), "unknown iCalendar property was lost");

    var parsed = codec.Parse(updated, "calendar", "https://example.invalid/tasks/test.ics", "\"etag\"", task.Id);
    Check(parsed.Title == task.Title, "title did not round-trip");
    Check(parsed.Priority == TodoPriority.High, "priority did not round-trip");
    Check(parsed.RecurrenceRule.Contains("BYSETPOS=-1", StringComparison.Ordinal), "complex recurrence did not round-trip");

    parsed.Due = null;
    parsed.HasDueTime = false;
    var withoutDue = codec.Serialize(parsed);
    var reparsedWithoutDue = codec.Parse(withoutDue, "calendar", "https://example.invalid/tasks/test.ics", "\"etag-2\"", task.Id);
    Check(reparsedWithoutDue.Due is null, "cleared due date reappeared after round-trip");
    Check(!reparsedWithoutDue.HasDueTime, "cleared due date retained its time flag");

    var daily = new TodoItem
    {
        Title = "Daily",
        ListId = "calendar",
        Start = new DateTime(2026, 7, 22, 9, 0, 0),
        Due = new DateTime(2026, 7, 22, 10, 0, 0),
        HasDueTime = true,
        RecurrenceRule = "FREQ=DAILY"
    };
    daily.RawICalendar = codec.Serialize(daily);
    var completion = codec.CompleteOccurrence(daily, new DateTimeOffset(2026, 7, 22, 10, 15, 0, TimeSpan.FromHours(8)));
    Check(completion.RawICalendar.Contains("RECURRENCE-ID", StringComparison.Ordinal), "recurring completion exception missing");
    Check(completion.NextOccurrence?.Due == new DateTime(2026, 7, 23, 10, 0, 0), $"recurring task did not advance to next due date: {completion.NextOccurrence?.Due:O}");
    Check(!completion.RawICalendar.Contains("RRULE:FREQ=DAILY\r\nSTATUS:COMPLETED", StringComparison.Ordinal), "recurring master was completed");
    var reparsedRecurring = codec.Parse(completion.RawICalendar, "calendar", "https://example.invalid/tasks/daily.ics", "\"next\"", daily.Id);
    Check(reparsedRecurring.Due == new DateTime(2026, 7, 23, 10, 0, 0), "recurring task lost its next occurrence after reload");

    database.SaveLocalTask(parsed, updated, queueForSync: true);

    var racingTask = new TodoItem { Title = "Create in flight", ListId = "calendar", Due = new DateTime(2026, 7, 24) };
    var racingCreateRaw = codec.Serialize(racingTask);
    database.SaveLocalTask(racingTask, racingCreateRaw, queueForSync: true);
    racingTask.Due = null;
    var racingEditRaw = codec.Serialize(racingTask);
    database.SaveLocalTask(racingTask, racingEditRaw, queueForSync: true);
    database.MarkUploadSucceeded(racingTask.Id, "https://example.invalid/tasks/race.ics", "\"race-1\"", racingCreateRaw);
    var preservedEdit = database.LoadTasks().Single(item => item.Id == racingTask.Id);
    Check(preservedEdit.RawICalendar == racingEditRaw && preservedEdit.IsDirty, "in-flight create overwrote a newer local edit");
    Check(database.GetPendingChanges().Any(change => change.TaskId == racingTask.Id), "newer local edit was removed from the outbox");
    Check(preservedEdit.RemoteHref == "https://example.invalid/tasks/race.ics" && preservedEdit.ETag == "\"race-1\"", "create result did not hydrate remote identity");
    database.MarkUploadSucceeded(racingTask.Id, preservedEdit.RemoteHref!, "\"race-2\"", racingEditRaw);
    Check(!database.LoadTasks().Single(item => item.Id == racingTask.Id).IsDirty, "latest local edit remained dirty after upload");

    var staleDraft = racingTask.Clone();
    staleDraft.RemoteHref = null;
    staleDraft.ETag = null;
    staleDraft.BaseICalendar = string.Empty;
    staleDraft.Title = "Edited from stale UI state";
    var staleDraftRaw = codec.Serialize(staleDraft);
    database.SaveLocalTask(staleDraft, staleDraftRaw, queueForSync: true);
    var hydratedDraft = database.LoadTasks().Single(item => item.Id == racingTask.Id);
    Check(hydratedDraft.RemoteHref == "https://example.invalid/tasks/race.ics" && hydratedDraft.ETag == "\"race-2\"", "stale editor state erased remote identity");
    database.MarkUploadSucceeded(racingTask.Id, hydratedDraft.RemoteHref!, "\"race-3\"", staleDraftRaw);
    database.MarkTaskDeleted(hydratedDraft, queueForSync: false);

    var placeholder = new TodoItem
    {
        Title = "Placeholder",
        ListId = "calendar",
        IsPlaceholder = true
    };
    database.SaveLocalTask(placeholder, codec.Serialize(placeholder), queueForSync: false);
    var loaded = database.LoadTasks();
    Check(loaded.Count == 2, "database task count mismatch");
    Check(loaded.Any(item => item.Id == placeholder.Id && item.IsPlaceholder), "placeholder flag was not persisted");
    Check(database.DeletePlaceholderTasks() == 1, "placeholder task was not deleted");
    loaded = database.LoadTasks();
    Check(loaded.Count == 1 && loaded[0].Id == parsed.Id, "placeholder cleanup deleted a real task");
    Check(loaded[0].IsDirty, "outbox dirty flag missing");

    database.UpsertAccount(new AccountRecord("local", "Local", "local://prototype", string.Empty, false));
    database.UpsertCalendar(new CalendarRecord("local-calendar", "local", "local://tasks", "Local", "#55C9B0", true, null, null));
    var localTask = new TodoItem { Title = "Local only", ListId = "local-calendar" };
    database.SaveLocalTask(localTask, codec.Serialize(localTask), queueForSync: true);
    Check(database.GetPendingChanges().Count == 1, "outbox entry missing");

    var reopened = new AppDatabase(databasePath);
    var cleanedLocalTask = reopened.LoadTasks().Single(item => item.Id == localTask.Id);
    Check(!cleanedLocalTask.IsDirty, "legacy local task remained dirty");
    Check(reopened.GetPendingChanges().Count == 1, "local outbox cleanup removed a remote change");
    Check(reopened.GetHudOpacityPercent() == 100, "default HUD opacity was not 100 percent");
    reopened.SetHudOpacityPercent(63);
    Check(new AppDatabase(databasePath).GetHudOpacityPercent() == 63, "HUD opacity was not persisted");
    reopened.SetHudOpacityPercent(0);
    Check(reopened.GetHudOpacityPercent() == 0, "zero HUD opacity was not persisted");
    reopened.SetHudOpacityPercent(120);
    Check(reopened.GetHudOpacityPercent() == 100, "HUD opacity was not clamped");

    reopened.SetCalendarsEnabledForAccount("local", false);
    var store = new TaskStore(reopened, codec);
    Check(store.Items.All(item => item.ListId != "local-calendar"), "disabled local task remained visible");

    var vault = new CredentialVault();
    var credentialId = $"core-check-{Guid.NewGuid():N}";
    try
    {
        vault.WritePassword(credentialId, "tester", "temporary-secret");
        Check(vault.ReadPassword(credentialId) == "temporary-secret", "credential round-trip failed");
    }
    finally
    {
        vault.DeletePassword(credentialId);
    }
}
finally
{
    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    foreach (var path in Directory.EnumerateFiles(root, Path.GetFileNameWithoutExtension(databasePath) + ".db*"))
    {
        File.Delete(path);
    }
}

if (failures.Count > 0)
{
    Console.Error.WriteLine(string.Join(Environment.NewLine, failures));
    return 1;
}

Console.WriteLine("Core checks passed: SQLite, outbox, iCalendar, RRULE, Credential Manager");
return 0;
