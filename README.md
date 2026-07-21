# Floating Todo

A Windows 11 desktop-layer CalDAV VTODO HUD. The first prototype focuses on the
interaction shell: grouped tasks, quick creation, five-minute completion undo,
task editing, tray activation, and native desktop positioning.

## Run the prototype

```powershell
.\.dotnet\dotnet.exe run --project .\FloatingTodo.csproj
```

The prototype currently uses a small local JSON cache and sample tasks. The
CalDAV/SQLite synchronization layer is the next implementation slice; the UI
does not yet connect to a real server.

`SyncContracts.cs` defines the boundary for that layer now, so adding CalDAV,
ETag/outbox handling, raw iCalendar preservation, and field-level conflicts
does not require rewriting the HUD.

## Current interaction model

- The HUD lives at the bottom of the normal window stack and returns there
  after temporary activation.
- Left-click the tray icon to bring it above other windows for 15 seconds.
- Hovering or opening an editor pauses the countdown.
- Use `+` to create a task. Click the colored strip to rotate task lists.
- Complete a task from the HUD; click it again within five minutes to undo.
- Click a task title to open the detail editor. Closing commits the draft;
  `撤销本次编辑` discards it.
