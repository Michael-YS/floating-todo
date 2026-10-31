using System;

namespace FloatingTodo;

internal static class BackgroundSchedule
{
    internal static TimeSpan RefreshInterval(bool saving) => TimeSpan.FromMinutes(saving ? 5 : 1);
    internal static TimeSpan CountdownInterval(bool saving) => TimeSpan.FromSeconds(saving ? 5 : 1);
    internal static TimeSpan SyncInterval(bool saving, bool pluggedIn) =>
        TimeSpan.FromMinutes(saving ? 15 : pluggedIn ? 1 : 5);
}
