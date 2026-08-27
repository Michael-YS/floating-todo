using System;
using System.Collections.Generic;
using System.Linq;

namespace FloatingTodo;

public static class CalendarDiscoveryUpdater
{
    public static CalendarDiscoveryUpdate Apply(
        AppDatabase database,
        string accountId,
        IReadOnlyList<DiscoveredCalendar> discovered,
        IReadOnlyList<string> palette)
    {
        if (palette.Count == 0)
        {
            throw new ArgumentException("Calendar palette cannot be empty.", nameof(palette));
        }

        var existing = database.GetCalendars(accountId)
            .ToDictionary(calendar => calendar.RemoteUrl, StringComparer.OrdinalIgnoreCase);
        var added = 0;
        var nextColorIndex = existing.Count;

        foreach (var remote in discovered)
        {
            if (existing.TryGetValue(remote.RemoteUrl, out var known))
            {
                database.UpsertCalendar(known with
                {
                    DisplayName = remote.DisplayName,
                    CTag = remote.CTag,
                    SyncToken = remote.SyncToken
                });
                continue;
            }

            var calendar = new CalendarRecord(
                Guid.NewGuid().ToString("D"),
                accountId,
                remote.RemoteUrl,
                remote.DisplayName,
                palette[nextColorIndex % palette.Count],
                true,
                remote.CTag,
                remote.SyncToken);
            database.UpsertCalendar(calendar);
            existing.Add(calendar.RemoteUrl, calendar);
            added++;
            nextColorIndex++;
        }

        return new CalendarDiscoveryUpdate(discovered.Count, added);
    }
}

public sealed record CalendarDiscoveryUpdate(int DiscoveredCount, int AddedCount);
