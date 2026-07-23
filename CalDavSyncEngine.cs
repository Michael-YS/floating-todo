using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FloatingTodo;

public sealed class CalDavSyncEngine : ICalDavSyncEngine
{
    private readonly AppDatabase _database;
    private readonly CredentialVault _vault;
    private readonly CalDavClient _client;
    private readonly ICalendarCodec _codec;
    private readonly SemaphoreSlim _syncGate = new(1, 1);

    public CalDavSyncEngine(AppDatabase database, CredentialVault vault, CalDavClient client, ICalendarCodec codec)
    {
        _database = database;
        _vault = vault;
        _client = client;
        _codec = codec;
    }

    public event Action<SyncStatus>? StatusChanged;
    public event Action? DataChanged;

    public async Task<SyncResult> SynchronizeAsync(SyncTrigger trigger, CancellationToken cancellationToken = default)
    {
        if (!await _syncGate.WaitAsync(0, cancellationToken))
        {
            return new SyncResult(0, 0, _database.GetConflicts().Select(ToTaskConflict).ToList(), DateTimeOffset.Now);
        }

        var logContext = new SyncLogContext(trigger);
        try
        {
            SetStatus(SyncState.Synchronizing, "正在同步");
            logContext.Stage = "upload pending changes";
            var uploaded = await UploadPendingAsync(logContext, cancellationToken);
            var downloaded = 0;
            foreach (var account in _database.GetAccounts().Where(account => account.Enabled))
            {
                logContext.SetAccount(account);
                cancellationToken.ThrowIfCancellationRequested();
                var password = _vault.ReadPassword(account.Id)
                               ?? throw new CalDavException($"账号“{account.Name}”缺少密码");
                foreach (var calendar in _database.GetCalendars(account.Id, enabledOnly: true))
                {
                    logContext.SetCalendar(calendar);
                    if (trigger == SyncTrigger.Scheduled && !string.IsNullOrWhiteSpace(calendar.CTag))
                    {
                        logContext.Stage = "read calendar state";
                        var state = await _client.GetCalendarStateAsync(account, password, calendar, cancellationToken);
                        if (string.Equals(state.CTag, calendar.CTag, StringComparison.Ordinal))
                        {
                            continue;
                        }
                    }
                    logContext.Stage = "download calendar";
                    downloaded += await DownloadCalendarAsync(account, password, calendar, cancellationToken);
                }
            }

            var conflicts = _database.GetConflicts().Select(ToTaskConflict).ToList();
            SetStatus(
                conflicts.Count > 0 ? SyncState.Conflict : SyncState.Idle,
                conflicts.Count > 0 ? $"{conflicts.Count} 个冲突待处理" : "刚刚同步");
            DataChanged?.Invoke();
            return new SyncResult(downloaded, uploaded, conflicts, DateTimeOffset.Now);
        }
        catch (OperationCanceledException)
        {
            SetStatus(SyncState.Idle, "同步已取消");
            throw;
        }
        catch (Exception exception)
        {
            AppLogger.Error("CalDAV synchronization failed.", exception, logContext.ToFields());
            SetStatus(SyncState.Error, exception.Message);
            throw;
        }
        finally
        {
            _syncGate.Release();
        }
    }

    private async Task<int> UploadPendingAsync(SyncLogContext logContext, CancellationToken cancellationToken)
    {
        var uploaded = 0;
        foreach (var pending in _database.GetPendingChanges())
        {
            logContext.SetPending(pending);
            cancellationToken.ThrowIfCancellationRequested();
            var password = _vault.ReadPassword(pending.Account.Id)
                           ?? throw new CalDavException($"账号“{pending.Account.Name}”缺少密码");
            if (pending.Operation == OutboxOperation.Delete)
            {
                logContext.Stage = "delete remote task";
                if (!string.IsNullOrWhiteSpace(pending.Task.RemoteHref))
                {
                    await _client.DeleteAsync(
                        pending.Account, password, pending.Task.RemoteHref, pending.Task.ETag, cancellationToken);
                }
                _database.MarkDeleteSucceeded(pending.TaskId);
                uploaded++;
                continue;
            }

            var isCreate = string.IsNullOrWhiteSpace(pending.Task.RemoteHref);
            var target = isCreate
                ? BuildTaskUrl(pending.Calendar.RemoteUrl, pending.Task.Uid)
                : pending.Task.RemoteHref!;
            logContext.Stage = isCreate ? "create remote task" : "update remote task";
            var result = await _client.PutAsync(
                pending.Account,
                password,
                target,
                pending.Task.RawICalendar,
                pending.Task.ETag,
                isCreate,
                cancellationToken);
            if (result.Conflict)
            {
                logContext.Stage = "read conflicting remote task";
                var server = await _client.GetRawAsync(pending.Account, password, target, cancellationToken);
                _database.SaveConflict(pending.Task, server.RawICalendar, server.ETag);
                continue;
            }

            _database.MarkUploadSucceeded(pending.TaskId, target, result.ETag, pending.Task.RawICalendar);
            uploaded++;
        }

        return uploaded;
    }

    private async Task<int> DownloadCalendarAsync(
        AccountRecord account,
        string password,
        CalendarRecord calendar,
        CancellationToken cancellationToken)
    {
        var snapshot = await _client.DownloadCalendarAsync(account, password, calendar, cancellationToken);
        var localByHref = _database.LoadTasks(includeDeleted: true)
            .Where(task => task.ListId == calendar.Id && !string.IsNullOrWhiteSpace(task.RemoteHref))
            .ToDictionary(task => task.RemoteHref!, StringComparer.OrdinalIgnoreCase);
        var remoteHrefs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var downloaded = 0;

        foreach (var resource in snapshot.Resources)
        {
            remoteHrefs.Add(resource.RemoteHref);
            localByHref.TryGetValue(resource.RemoteHref, out var local);
            if (local is not null && local.IsDirty && !EtagEquals(local.ETag, resource.ETag))
            {
                _database.SaveConflict(local, resource.RawICalendar, resource.ETag);
                continue;
            }

            if (local is not null && EtagEquals(local.ETag, resource.ETag) && !local.IsDirty)
            {
                continue;
            }

            try
            {
                var parsed = _codec.Parse(resource.RawICalendar, calendar.Id, resource.RemoteHref, resource.ETag, local?.Id);
                _database.SaveRemoteTask(parsed, resource.RawICalendar, resource.RemoteHref, resource.ETag);
                downloaded++;
            }
            catch (FormatException exception)
            {
                AppLogger.Warning(
                    "Skipped a malformed CalDAV resource.",
                    exception,
                    ("account", account.Name),
                    ("calendar", calendar.DisplayName),
                    ("calendarUrl", calendar.RemoteUrl),
                    ("resourceUrl", resource.RemoteHref));
                // One malformed resource must not block every valid task in the collection.
            }
        }

        _database.RemoveRemoteTasksMissingFrom(calendar.Id, remoteHrefs);
        _database.UpdateCalendarSyncState(calendar.Id, snapshot.CTag, snapshot.SyncToken);
        return downloaded;
    }

    private void SetStatus(SyncState state, string message) =>
        StatusChanged?.Invoke(new SyncStatus(state, message, DateTimeOffset.Now));

    private static string BuildTaskUrl(string calendarUrl, string uid)
    {
        var root = calendarUrl.EndsWith('/') ? calendarUrl : calendarUrl + "/";
        return new Uri(new Uri(root, UriKind.Absolute), Uri.EscapeDataString(uid) + ".ics").AbsoluteUri;
    }

    private static bool EtagEquals(string? left, string? right) =>
        string.Equals(left?.Trim(), right?.Trim(), StringComparison.Ordinal);

    private static TaskConflict ToTaskConflict(StoredConflict conflict) => new(
        conflict.TaskId,
        conflict.CalendarId,
        conflict.BaseICalendar,
        conflict.LocalICalendar,
        conflict.ServerICalendar,
        ["TITLE", "DESCRIPTION", "DTSTART", "DUE", "PRIORITY", "RRULE", "STATUS"]);

    private sealed class SyncLogContext(SyncTrigger trigger)
    {
        public string Stage { get; set; } = "start";
        private string? AccountName { get; set; }
        private string? AccountUrl { get; set; }
        private string? CalendarName { get; set; }
        private string? CalendarUrl { get; set; }
        private string? TaskUid { get; set; }
        private string? Operation { get; set; }

        public void SetAccount(AccountRecord account)
        {
            AccountName = account.Name;
            AccountUrl = account.BaseUrl;
            CalendarName = null;
            CalendarUrl = null;
            TaskUid = null;
            Operation = null;
        }

        public void SetCalendar(CalendarRecord calendar)
        {
            CalendarName = calendar.DisplayName;
            CalendarUrl = calendar.RemoteUrl;
            TaskUid = null;
            Operation = null;
        }

        public void SetPending(PendingChange pending)
        {
            SetAccount(pending.Account);
            SetCalendar(pending.Calendar);
            TaskUid = pending.Task.Uid;
            Operation = pending.Operation.ToString();
        }

        public (string Key, string? Value)[] ToFields() =>
        [
            ("trigger", trigger.ToString()),
            ("stage", Stage),
            ("account", AccountName),
            ("accountUrl", AccountUrl),
            ("calendar", CalendarName),
            ("calendarUrl", CalendarUrl),
            ("taskUid", TaskUid),
            ("operation", Operation)
        ];
    }
}
