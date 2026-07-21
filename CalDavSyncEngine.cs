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

        try
        {
            SetStatus(SyncState.Synchronizing, "正在同步");
            var uploaded = await UploadPendingAsync(cancellationToken);
            var downloaded = 0;
            foreach (var account in _database.GetAccounts().Where(account => account.Enabled))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var password = _vault.ReadPassword(account.Id)
                               ?? throw new CalDavException($"账号“{account.Name}”缺少密码");
                foreach (var calendar in _database.GetCalendars(account.Id, enabledOnly: true))
                {
                    if (trigger == SyncTrigger.Scheduled && !string.IsNullOrWhiteSpace(calendar.CTag))
                    {
                        var state = await _client.GetCalendarStateAsync(account, password, calendar, cancellationToken);
                        if (string.Equals(state.CTag, calendar.CTag, StringComparison.Ordinal))
                        {
                            continue;
                        }
                    }
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
            SetStatus(SyncState.Error, exception.Message);
            throw;
        }
        finally
        {
            _syncGate.Release();
        }
    }

    private async Task<int> UploadPendingAsync(CancellationToken cancellationToken)
    {
        var uploaded = 0;
        foreach (var pending in _database.GetPendingChanges())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var password = _vault.ReadPassword(pending.Account.Id)
                           ?? throw new CalDavException($"账号“{pending.Account.Name}”缺少密码");
            if (pending.Operation == OutboxOperation.Delete)
            {
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
            catch (FormatException)
            {
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
}
