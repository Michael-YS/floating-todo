using FloatingTodo;

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
    var database = new AppDatabase(databasePath);
    database.UpsertAccount(new AccountRecord("test", "Test", "https://example.invalid/", "tester", true));
    database.UpsertCalendar(new CalendarRecord("calendar", "test", "https://example.invalid/tasks/", "Tasks", "#55C9B0", true, null, null));

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
