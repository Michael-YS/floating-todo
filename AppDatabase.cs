using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;

namespace FloatingTodo;

public sealed class AppDatabase
{
    private const int DefaultHudOpacityPercent = 100;
    private readonly string _connectionString;
    private readonly object _gate = new();

    public AppDatabase(string? path = null)
    {
        path ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FloatingTodo",
            "floating-todo.db");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true
        }.ToString();
        Initialize();
    }

    public IReadOnlyList<AccountRecord> GetAccounts()
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT id, name, base_url, username, enabled FROM accounts ORDER BY name";
            using var reader = command.ExecuteReader();
            var result = new List<AccountRecord>();
            while (reader.Read())
            {
                result.Add(new AccountRecord(
                    reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetBoolean(4)));
            }

            return result;
        }
    }

    public int GetHudOpacityPercent()
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT value FROM app_settings WHERE key = 'hud_opacity_percent'";
            var value = command.ExecuteScalar() as string;
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var percent)
                ? Math.Clamp(percent, 0, 100)
                : DefaultHudOpacityPercent;
        }
    }

    public void SetHudOpacityPercent(int percent)
    {
        percent = Math.Clamp(percent, 0, 100);
        lock (_gate)
        {
            using var connection = Open();
            Execute(connection, """
                INSERT INTO app_settings (key, value)
                VALUES ('hud_opacity_percent', $value)
                ON CONFLICT(key) DO UPDATE SET value = excluded.value
                """,
                ("$value", percent.ToString(CultureInfo.InvariantCulture)));
        }
    }

    public bool GetPowerSavingEnabled()
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT value FROM app_settings WHERE key = 'power_saving_enabled'";
            return (command.ExecuteScalar() as string) == "1";
        }
    }

    public void SetPowerSavingEnabled(bool enabled)
    {
        lock (_gate)
        {
            using var connection = Open();
            Execute(connection, """
                INSERT INTO app_settings (key, value)
                VALUES ('power_saving_enabled', $value)
                ON CONFLICT(key) DO UPDATE SET value = excluded.value
                """, ("$value", enabled ? "1" : "0"));
        }
    }

    public void UpsertAccount(AccountRecord account)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO accounts (id, name, base_url, username, enabled)
                VALUES ($id, $name, $url, $username, $enabled)
                ON CONFLICT(id) DO UPDATE SET
                    name = excluded.name,
                    base_url = excluded.base_url,
                    username = excluded.username,
                    enabled = excluded.enabled;
                """;
            command.Parameters.AddWithValue("$id", account.Id);
            command.Parameters.AddWithValue("$name", account.Name);
            command.Parameters.AddWithValue("$url", account.BaseUrl);
            command.Parameters.AddWithValue("$username", account.Username);
            command.Parameters.AddWithValue("$enabled", account.Enabled);
            command.ExecuteNonQuery();
        }
    }

    public void DeleteAccount(string accountId)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            Execute(connection, "DELETE FROM accounts WHERE id = $id", ("$id", accountId));
            transaction.Commit();
        }
    }

    public IReadOnlyList<CalendarRecord> GetCalendars(string? accountId = null, bool enabledOnly = false)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            var filters = new List<string>();
            if (accountId is not null)
            {
                filters.Add("account_id = $accountId");
                command.Parameters.AddWithValue("$accountId", accountId);
            }

            if (enabledOnly)
            {
                filters.Add("enabled = 1");
            }

            command.CommandText = "SELECT id, account_id, remote_url, display_name, color, enabled, ctag, sync_token FROM calendars" +
                                  (filters.Count == 0 ? string.Empty : " WHERE " + string.Join(" AND ", filters)) +
                                  " ORDER BY display_name";
            using var reader = command.ExecuteReader();
            var result = new List<CalendarRecord>();
            while (reader.Read())
            {
                result.Add(ReadCalendar(reader));
            }

            return result;
        }
    }

    public void UpsertCalendar(CalendarRecord calendar)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO calendars (id, account_id, remote_url, display_name, color, enabled, ctag, sync_token)
                VALUES ($id, $account, $url, $name, $color, $enabled, $ctag, $syncToken)
                ON CONFLICT(id) DO UPDATE SET
                    remote_url = excluded.remote_url,
                    display_name = excluded.display_name,
                    color = excluded.color,
                    enabled = excluded.enabled,
                    ctag = COALESCE(excluded.ctag, calendars.ctag),
                    sync_token = COALESCE(excluded.sync_token, calendars.sync_token);
                """;
            AddCalendarParameters(command, calendar);
            command.ExecuteNonQuery();
        }
    }

    public void UpdateCalendarSyncState(string calendarId, string? ctag, string? syncToken)
    {
        lock (_gate)
        {
            using var connection = Open();
            Execute(connection,
                "UPDATE calendars SET ctag = $ctag, sync_token = $syncToken WHERE id = $id",
                ("$ctag", ctag), ("$syncToken", syncToken), ("$id", calendarId));
        }
    }

    public void UpdateCalendarPresentation(string calendarId, bool enabled, string color)
    {
        lock (_gate)
        {
            using var connection = Open();
            Execute(connection,
                "UPDATE calendars SET enabled = $enabled, color = $color WHERE id = $id",
                ("$enabled", enabled), ("$color", color), ("$id", calendarId));
        }
    }

    public void SetCalendarsEnabledForAccount(string accountId, bool enabled)
    {
        lock (_gate)
        {
            using var connection = Open();
            Execute(connection,
                "UPDATE calendars SET enabled = $enabled WHERE account_id = $accountId",
                ("$enabled", enabled), ("$accountId", accountId));
        }
    }

    public IReadOnlyList<TodoItem> LoadTasks(bool includeDeleted = false)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT id, uid, calendar_id, remote_href, etag, raw_ics, base_ics,
                       title, description, start_value, due_value, has_due_time,
                       priority, recurrence_rule, completed_at, updated_at,
                       dirty, deleted, has_conflict, is_placeholder
                FROM tasks
                """ + (includeDeleted ? string.Empty : " WHERE deleted = 0") + " ORDER BY updated_at";
            using var reader = command.ExecuteReader();
            var result = new List<TodoItem>();
            while (reader.Read())
            {
                result.Add(ReadTask(reader));
            }

            return result;
        }
    }

    public void SaveLocalTask(TodoItem task, string rawICalendar, bool queueForSync)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            HydrateMissingRemoteState(connection, task);
            task.RawICalendar = rawICalendar;
            task.IsDirty = queueForSync;
            task.UpdatedAt = DateTimeOffset.Now;
            UpsertTask(connection, task);
            if (queueForSync)
            {
                Queue(connection, task.Id, OutboxOperation.Upsert);
            }
            transaction.Commit();
        }
    }

    public void SaveRemoteTask(TodoItem task, string rawICalendar, string remoteHref, string? etag)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            task.RawICalendar = rawICalendar;
            task.BaseICalendar = rawICalendar;
            task.RemoteHref = remoteHref;
            task.ETag = etag;
            task.IsDirty = false;
            task.IsDeleted = false;
            task.HasConflict = false;
            UpsertTask(connection, task);
            Execute(connection, "DELETE FROM outbox WHERE task_id = $id", ("$id", task.Id.ToString("D")));
            Execute(connection, "DELETE FROM conflicts WHERE task_id = $id", ("$id", task.Id.ToString("D")));
            transaction.Commit();
        }
    }

    public void MarkTaskDeleted(TodoItem task, bool queueForSync)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            if (!queueForSync || string.IsNullOrWhiteSpace(task.RemoteHref))
            {
                Execute(connection, "DELETE FROM tasks WHERE id = $id", ("$id", task.Id.ToString("D")));
            }
            else
            {
                Execute(connection,
                    "UPDATE tasks SET deleted = 1, dirty = 1, updated_at = $updated WHERE id = $id",
                    ("$updated", Format(DateTimeOffset.Now)), ("$id", task.Id.ToString("D")));
                Queue(connection, task.Id, OutboxOperation.Delete);
            }
            transaction.Commit();
        }
    }

    public int DeletePlaceholderTasks()
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM tasks WHERE is_placeholder = 1";
            return command.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<PendingChange> GetPendingChanges()
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT o.task_id, o.operation, o.queued_at,
                       t.id, t.uid, t.calendar_id, t.remote_href, t.etag, t.raw_ics, t.base_ics,
                       t.title, t.description, t.start_value, t.due_value, t.has_due_time,
                       t.priority, t.recurrence_rule, t.completed_at, t.updated_at,
                       t.dirty, t.deleted, t.has_conflict, t.is_placeholder,
                       c.id, c.account_id, c.remote_url, c.display_name, c.color, c.enabled, c.ctag, c.sync_token,
                       a.id, a.name, a.base_url, a.username, a.enabled
                FROM outbox o
                JOIN tasks t ON t.id = o.task_id
                JOIN calendars c ON c.id = t.calendar_id
                JOIN accounts a ON a.id = c.account_id
                WHERE a.enabled = 1
                  AND c.enabled = 1
                  AND c.account_id <> 'local'
                ORDER BY o.queued_at;
                """;
            using var reader = command.ExecuteReader();
            var result = new List<PendingChange>();
            while (reader.Read())
            {
                var task = ReadTask(reader, 3);
                var calendar = ReadCalendar(reader, 23);
                var account = new AccountRecord(
                    reader.GetString(31), reader.GetString(32), reader.GetString(33), reader.GetString(34), reader.GetBoolean(35));
                result.Add(new PendingChange(
                    task.Id,
                    Enum.Parse<OutboxOperation>(reader.GetString(1), true),
                    ParseOffset(reader.GetString(2)),
                    task,
                    calendar,
                    account));
            }

            return result;
        }
    }

    public void MarkUploadSucceeded(Guid taskId, string remoteHref, string? etag, string rawICalendar)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            using var currentCommand = connection.CreateCommand();
            currentCommand.CommandText = "SELECT raw_ics FROM tasks WHERE id = $id";
            currentCommand.Parameters.AddWithValue("$id", taskId.ToString("D"));
            var currentRaw = currentCommand.ExecuteScalar() as string;
            var hasNewerLocalChange = currentRaw is not null &&
                                      !string.Equals(currentRaw, rawICalendar, StringComparison.Ordinal);
            Execute(connection, """
                UPDATE tasks
                SET remote_href = $href, etag = $etag, raw_ics = $raw, base_ics = $raw,
                    dirty = 0, deleted = 0, has_conflict = 0
                WHERE id = $id
                """,
                ("$href", remoteHref), ("$etag", etag), ("$raw", rawICalendar), ("$id", taskId.ToString("D")));
            if (hasNewerLocalChange)
            {
                // The upload result still supplies the remote identity and base
                // revision, but it must not replace an edit made while the
                // request was in flight.
                Execute(connection, """
                    UPDATE tasks
                    SET raw_ics = $currentRaw, dirty = 1
                    WHERE id = $id
                    """,
                    ("$currentRaw", currentRaw), ("$id", taskId.ToString("D")));
            }
            else
            {
                Execute(connection, "DELETE FROM outbox WHERE task_id = $id", ("$id", taskId.ToString("D")));
                Execute(connection, "DELETE FROM conflicts WHERE task_id = $id", ("$id", taskId.ToString("D")));
            }
            transaction.Commit();
        }
    }

    public void MarkDeleteSucceeded(Guid taskId)
    {
        lock (_gate)
        {
            using var connection = Open();
            Execute(connection, "DELETE FROM tasks WHERE id = $id", ("$id", taskId.ToString("D")));
        }
    }

    public void SaveConflict(TodoItem localTask, string serverICalendar, string? serverEtag)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            Execute(connection, """
                INSERT INTO conflicts (task_id, calendar_id, base_ics, local_ics, server_ics, detected_at)
                VALUES ($taskId, $calendarId, $base, $local, $server, $detected)
                ON CONFLICT(task_id) DO UPDATE SET
                    base_ics = excluded.base_ics,
                    local_ics = excluded.local_ics,
                    server_ics = excluded.server_ics,
                    detected_at = excluded.detected_at;
                """,
                ("$taskId", localTask.Id.ToString("D")),
                ("$calendarId", localTask.ListId),
                ("$base", localTask.BaseICalendar),
                ("$local", localTask.RawICalendar),
                ("$server", serverICalendar),
                ("$detected", Format(DateTimeOffset.Now)));
            Execute(connection,
                "UPDATE tasks SET has_conflict = 1, etag = $etag, base_ics = $server WHERE id = $id",
                ("$etag", serverEtag), ("$server", serverICalendar), ("$id", localTask.Id.ToString("D")));
            transaction.Commit();
        }
    }

    public void ResolveConflict(TodoItem mergedTask, string mergedICalendar)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            mergedTask.RawICalendar = mergedICalendar;
            mergedTask.IsDirty = true;
            mergedTask.HasConflict = false;
            mergedTask.UpdatedAt = DateTimeOffset.Now;
            UpsertTask(connection, mergedTask);
            Queue(connection, mergedTask.Id, OutboxOperation.Upsert);
            Execute(connection, "DELETE FROM conflicts WHERE task_id = $id", ("$id", mergedTask.Id.ToString("D")));
            transaction.Commit();
        }
    }

    public IReadOnlyList<StoredConflict> GetConflicts()
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT task_id, calendar_id, base_ics, local_ics, server_ics, detected_at FROM conflicts ORDER BY detected_at";
            using var reader = command.ExecuteReader();
            var result = new List<StoredConflict>();
            while (reader.Read())
            {
                result.Add(new StoredConflict(
                    Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2),
                    reader.GetString(3), reader.GetString(4), ParseOffset(reader.GetString(5))));
            }

            return result;
        }
    }

    public void RemoveRemoteTasksMissingFrom(string calendarId, IReadOnlySet<string> remoteHrefs)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT id, remote_href FROM tasks WHERE calendar_id = $calendar AND dirty = 0 AND remote_href IS NOT NULL";
            command.Parameters.AddWithValue("$calendar", calendarId);
            using var reader = command.ExecuteReader();
            var stale = new List<string>();
            while (reader.Read())
            {
                if (!remoteHrefs.Contains(reader.GetString(1)))
                {
                    stale.Add(reader.GetString(0));
                }
            }
            reader.Close();
            foreach (var id in stale)
            {
                Execute(connection, "DELETE FROM tasks WHERE id = $id", ("$id", id));
            }
        }
    }

    private void Initialize()
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                PRAGMA journal_mode = WAL;
                PRAGMA foreign_keys = ON;
                PRAGMA busy_timeout = 5000;

                CREATE TABLE IF NOT EXISTS accounts (
                    id TEXT PRIMARY KEY,
                    name TEXT NOT NULL,
                    base_url TEXT NOT NULL,
                    username TEXT NOT NULL,
                    enabled INTEGER NOT NULL DEFAULT 1
                );

                CREATE TABLE IF NOT EXISTS calendars (
                    id TEXT PRIMARY KEY,
                    account_id TEXT NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
                    remote_url TEXT NOT NULL,
                    display_name TEXT NOT NULL,
                    color TEXT NOT NULL,
                    enabled INTEGER NOT NULL DEFAULT 1,
                    ctag TEXT NULL,
                    sync_token TEXT NULL,
                    UNIQUE(account_id, remote_url)
                );

                CREATE TABLE IF NOT EXISTS tasks (
                    id TEXT PRIMARY KEY,
                    uid TEXT NOT NULL,
                    calendar_id TEXT NOT NULL REFERENCES calendars(id) ON DELETE CASCADE,
                    remote_href TEXT NULL,
                    etag TEXT NULL,
                    raw_ics TEXT NOT NULL,
                    base_ics TEXT NOT NULL,
                    title TEXT NOT NULL,
                    description TEXT NOT NULL,
                    start_value TEXT NULL,
                    due_value TEXT NULL,
                    has_due_time INTEGER NOT NULL DEFAULT 0,
                    priority INTEGER NOT NULL DEFAULT 0,
                    recurrence_rule TEXT NOT NULL,
                    completed_at TEXT NULL,
                    updated_at TEXT NOT NULL,
                    dirty INTEGER NOT NULL DEFAULT 0,
                    deleted INTEGER NOT NULL DEFAULT 0,
                    has_conflict INTEGER NOT NULL DEFAULT 0,
                    is_placeholder INTEGER NOT NULL DEFAULT 0,
                    UNIQUE(calendar_id, uid, remote_href)
                );

                CREATE INDEX IF NOT EXISTS idx_tasks_calendar ON tasks(calendar_id);
                CREATE INDEX IF NOT EXISTS idx_tasks_due ON tasks(due_value);

                CREATE TABLE IF NOT EXISTS outbox (
                    task_id TEXT PRIMARY KEY REFERENCES tasks(id) ON DELETE CASCADE,
                    operation TEXT NOT NULL,
                    queued_at TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS conflicts (
                    task_id TEXT PRIMARY KEY REFERENCES tasks(id) ON DELETE CASCADE,
                    calendar_id TEXT NOT NULL,
                    base_ics TEXT NOT NULL,
                    local_ics TEXT NOT NULL,
                    server_ics TEXT NOT NULL,
                    detected_at TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS app_settings (
                    key TEXT PRIMARY KEY,
                    value TEXT NOT NULL
                );
                """;
            command.ExecuteNonQuery();
            if (EnsurePlaceholderColumn(connection))
            {
                MarkLegacyPrototypeTasks(connection);
            }
            RemoveLocalPendingChanges(connection);
        }
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
        command.ExecuteNonQuery();
        return connection;
    }

    private static void UpsertTask(SqliteConnection connection, TodoItem task)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO tasks (
                id, uid, calendar_id, remote_href, etag, raw_ics, base_ics,
                title, description, start_value, due_value, has_due_time,
                priority, recurrence_rule, completed_at, updated_at,
                dirty, deleted, has_conflict, is_placeholder)
            VALUES (
                $id, $uid, $calendar, $href, $etag, $raw, $base,
                $title, $description, $start, $due, $hasDueTime,
                $priority, $recurrence, $completed, $updated,
                $dirty, $deleted, $conflict, $placeholder)
            ON CONFLICT(id) DO UPDATE SET
                uid = excluded.uid,
                calendar_id = excluded.calendar_id,
                remote_href = excluded.remote_href,
                etag = excluded.etag,
                raw_ics = excluded.raw_ics,
                base_ics = excluded.base_ics,
                title = excluded.title,
                description = excluded.description,
                start_value = excluded.start_value,
                due_value = excluded.due_value,
                has_due_time = excluded.has_due_time,
                priority = excluded.priority,
                recurrence_rule = excluded.recurrence_rule,
                completed_at = excluded.completed_at,
                updated_at = excluded.updated_at,
                dirty = excluded.dirty,
                deleted = excluded.deleted,
                has_conflict = excluded.has_conflict,
                is_placeholder = excluded.is_placeholder;
            """;
        command.Parameters.AddWithValue("$id", task.Id.ToString("D"));
        command.Parameters.AddWithValue("$uid", task.Uid);
        command.Parameters.AddWithValue("$calendar", task.ListId);
        command.Parameters.AddWithValue("$href", Db(task.RemoteHref));
        command.Parameters.AddWithValue("$etag", Db(task.ETag));
        command.Parameters.AddWithValue("$raw", task.RawICalendar);
        command.Parameters.AddWithValue("$base", task.BaseICalendar);
        command.Parameters.AddWithValue("$title", task.Title);
        command.Parameters.AddWithValue("$description", task.Description);
        command.Parameters.AddWithValue("$start", Db(task.Start?.ToString("O", CultureInfo.InvariantCulture)));
        command.Parameters.AddWithValue("$due", Db(task.Due?.ToString("O", CultureInfo.InvariantCulture)));
        command.Parameters.AddWithValue("$hasDueTime", task.HasDueTime);
        command.Parameters.AddWithValue("$priority", (int)task.Priority);
        command.Parameters.AddWithValue("$recurrence", task.RecurrenceRule);
        command.Parameters.AddWithValue("$completed", Db(task.CompletedAt is null ? null : Format(task.CompletedAt.Value)));
        command.Parameters.AddWithValue("$updated", Format(task.UpdatedAt));
        command.Parameters.AddWithValue("$dirty", task.IsDirty);
        command.Parameters.AddWithValue("$deleted", task.IsDeleted);
        command.Parameters.AddWithValue("$conflict", task.HasConflict);
        command.Parameters.AddWithValue("$placeholder", task.IsPlaceholder);
        command.ExecuteNonQuery();
    }

    private static void HydrateMissingRemoteState(SqliteConnection connection, TodoItem task)
    {
        if (!string.IsNullOrWhiteSpace(task.RemoteHref))
        {
            return;
        }

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT remote_href, etag, base_ics FROM tasks WHERE id = $id";
        command.Parameters.AddWithValue("$id", task.Id.ToString("D"));
        using var reader = command.ExecuteReader();
        if (!reader.Read() || reader.IsDBNull(0))
        {
            return;
        }

        // An editor can hold a draft created before the initial upload
        // completed. Preserve the remote identity learned by the database so a
        // later save updates the resource instead of creating a duplicate.
        task.RemoteHref = reader.GetString(0);
        task.ETag = reader.IsDBNull(1) ? null : reader.GetString(1);
        task.BaseICalendar = reader.GetString(2);
    }

    private static TodoItem ReadTask(SqliteDataReader reader, int offset = 0) => new()
    {
        Id = Guid.Parse(reader.GetString(offset)),
        Uid = reader.GetString(offset + 1),
        ListId = reader.GetString(offset + 2),
        RemoteHref = NullableString(reader, offset + 3),
        ETag = NullableString(reader, offset + 4),
        RawICalendar = reader.GetString(offset + 5),
        BaseICalendar = reader.GetString(offset + 6),
        Title = reader.GetString(offset + 7),
        Description = reader.GetString(offset + 8),
        Start = ParseDateTime(NullableString(reader, offset + 9)),
        Due = ParseDateTime(NullableString(reader, offset + 10)),
        HasDueTime = reader.GetBoolean(offset + 11),
        Priority = (TodoPriority)reader.GetInt32(offset + 12),
        RecurrenceRule = reader.GetString(offset + 13),
        CompletedAt = ParseNullableOffset(NullableString(reader, offset + 14)),
        UpdatedAt = ParseOffset(reader.GetString(offset + 15)),
        IsDirty = reader.GetBoolean(offset + 16),
        IsDeleted = reader.GetBoolean(offset + 17),
        HasConflict = reader.GetBoolean(offset + 18),
        IsPlaceholder = reader.GetBoolean(offset + 19)
    };

    private static bool EnsurePlaceholderColumn(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(tasks)";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), "is_placeholder", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        reader.Close();
        Execute(connection, "ALTER TABLE tasks ADD COLUMN is_placeholder INTEGER NOT NULL DEFAULT 0");
        return true;
    }

    private static void MarkLegacyPrototypeTasks(SqliteConnection connection)
    {
        // Older databases predate the explicit placeholder flag. Restrict this
        // one-time migration to the exact bundled sample rows so user-created
        // local tasks are never classified as disposable examples.
        Execute(connection, """
            UPDATE tasks
            SET is_placeholder = 1
            WHERE remote_href IS NULL
              AND etag IS NULL
              AND dirty = 0
              AND description = ''
              AND recurrence_rule = ''
              AND EXISTS (
                  SELECT 1
                  FROM calendars c
                  JOIN accounts a ON a.id = c.account_id
                  WHERE c.id = tasks.calendar_id
                    AND a.id = 'local'
                    AND a.base_url = 'local://prototype'
              )
              AND (
                  (calendar_id = 'personal' AND title IN ('提交项目报告', '整理照片', '清理下载目录'))
                  OR (calendar_id = 'work' AND title IN ('回复邮件', '检查服务器备份'))
                  OR (calendar_id = 'shopping' AND title = '购买硬盘')
                  OR (calendar_id = 'health' AND title = '预约体检')
              )
            """);
    }

    private static void RemoveLocalPendingChanges(SqliteConnection connection)
    {
        // Local calendars use SQLite as their final store and must never enter
        // the CalDAV outbox, including rows queued by older builds.
        Execute(connection, """
            DELETE FROM outbox
            WHERE task_id IN (
                SELECT t.id
                FROM tasks t
                JOIN calendars c ON c.id = t.calendar_id
                WHERE c.account_id = 'local'
            )
            """);
        Execute(connection, """
            UPDATE tasks
            SET dirty = 0
            WHERE calendar_id IN (
                SELECT id
                FROM calendars
                WHERE account_id = 'local'
            )
            """);
    }

    private static CalendarRecord ReadCalendar(SqliteDataReader reader, int offset = 0) => new(
        reader.GetString(offset), reader.GetString(offset + 1), reader.GetString(offset + 2), reader.GetString(offset + 3),
        reader.GetString(offset + 4), reader.GetBoolean(offset + 5), NullableString(reader, offset + 6), NullableString(reader, offset + 7));

    private static void AddCalendarParameters(SqliteCommand command, CalendarRecord calendar)
    {
        command.Parameters.AddWithValue("$id", calendar.Id);
        command.Parameters.AddWithValue("$account", calendar.AccountId);
        command.Parameters.AddWithValue("$url", calendar.RemoteUrl);
        command.Parameters.AddWithValue("$name", calendar.DisplayName);
        command.Parameters.AddWithValue("$color", calendar.Color);
        command.Parameters.AddWithValue("$enabled", calendar.Enabled);
        command.Parameters.AddWithValue("$ctag", Db(calendar.CTag));
        command.Parameters.AddWithValue("$syncToken", Db(calendar.SyncToken));
    }

    private static void Queue(SqliteConnection connection, Guid taskId, OutboxOperation operation)
    {
        Execute(connection, """
            INSERT INTO outbox (task_id, operation, queued_at)
            VALUES ($id, $operation, $queued)
            ON CONFLICT(task_id) DO UPDATE SET operation = excluded.operation, queued_at = excluded.queued_at
            """,
            ("$id", taskId.ToString("D")), ("$operation", operation.ToString()), ("$queued", Format(DateTimeOffset.Now)));
    }

    private static void Execute(SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter.Name, Db(parameter.Value));
        }
        command.ExecuteNonQuery();
    }

    private static object Db(object? value) => value ?? DBNull.Value;
    private static string? NullableString(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    private static DateTime? ParseDateTime(string? value) => string.IsNullOrWhiteSpace(value) ? null : DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    private static DateTimeOffset? ParseNullableOffset(string? value) => string.IsNullOrWhiteSpace(value) ? null : ParseOffset(value);
    private static DateTimeOffset ParseOffset(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    private static string Format(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);
}
