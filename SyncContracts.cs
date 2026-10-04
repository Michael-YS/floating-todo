using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace FloatingTodo;

/// <summary>
/// Boundary between the HUD and the CalDAV/SQLite synchronization layer. The
/// UI consumes normalized sync state while protocol and persistence details
/// remain behind this interface.
/// </summary>
public interface ICalDavSyncEngine
{
    event Action<SyncStatus>? StatusChanged;

    Task<SyncResult> SynchronizeAsync(SyncTrigger trigger, CancellationToken cancellationToken = default);
}

public enum SyncTrigger
{
    Startup,
    Scheduled,
    LocalChange,
    TrayRequest,
    NetworkRestored,
    Resume
}

public enum SyncState
{
    Offline,
    Idle,
    Synchronizing,
    WaitingToUpload,
    Conflict,
    Error
}

public sealed record SyncStatus(SyncState State, string Message, DateTimeOffset ChangedAt)
{
    // Idle is also emitted on cancellation; only a completed pass clears an alert.
    internal bool CompletedSuccessfully { get; init; }
}

internal sealed class SyncAlertState
{
    internal SyncState State { get; private set; } = SyncState.Idle;
    internal string Message { get; private set; } = "本地";
    internal string? ErrorMessage { get; private set; }
    internal bool IsSynchronizing => State == SyncState.Synchronizing;

    internal bool Apply(SyncStatus status)
    {
        State = status.State;
        Message = status.Message;
        if (status.CompletedSuccessfully)
        {
            ErrorMessage = null;
        }
        if (status.State != SyncState.Error)
        {
            return false;
        }
        var notify = ErrorMessage is null;
        ErrorMessage = status.Message;
        return notify;
    }

    internal void Reset()
    {
        State = SyncState.Idle;
        Message = "本地";
        ErrorMessage = null;
    }
}

public sealed record SyncResult(
    int Downloaded,
    int Uploaded,
    IReadOnlyList<TaskConflict> Conflicts,
    DateTimeOffset CompletedAt);

public sealed record TaskConflict(
    Guid TaskId,
    string CalendarId,
    string BaseICalendar,
    string LocalICalendar,
    string ServerICalendar,
    IReadOnlyList<string> ChangedFields);
