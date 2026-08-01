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

public sealed record SyncStatus(SyncState State, string Message, DateTimeOffset ChangedAt);

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
