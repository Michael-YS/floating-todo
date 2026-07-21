# Floating Todo

Floating Todo 是一个仅面向 Windows 11 的轻量 CalDAV VTODO 桌面 HUD。它常驻桌面层，普通应用会覆盖它；点击托盘图标可临时置顶 15 秒。

## v0.1.0 功能

- 合并显示多个 CalDAV 账户和任务清单，以任务左侧色条区分清单。
- 按逾期、今天、明天和后续日期分组；内容少时自动缩短，最多占满当前屏幕工作区且不遮挡任务栏。
- 在 HUD 内查看、创建和完成任务；新任务默认今天，标题、清单和日期可用一次键盘操作完成。
- 完成后保留 5 分钟，可再次点击撤销。
- 托盘左键临时显示 15 秒；悬停暂停，移出后重新计时 15 秒。
- 详情编辑标题、清单、开始时间、截止日期和时间、描述、四档优先级以及 RFC 5545 重复规则。
- 常用重复预设及高级 RRULE；完成重复任务时写入 `RECURRENCE-ID` 例外，不结束整个系列。
- 本地 SQLite 缓存和待上传队列，界面操作不等待网络。
- ETag 条件写入、逐字段冲突合并、未知 iCalendar 属性保留。
- 密码只写入 Windows Credential Manager；配置和数据库不保存密码，也不引入应用自定义加密。
- 插电时每分钟检查，电池供电时每五分钟检查；有 CTag 时先做轻量变更检测。恢复联网或系统唤醒后立即同步。

## 使用

1. 解压发布 ZIP，运行 `FloatingTodo.exe`。
2. 右键托盘图标，打开“设置”。
3. 输入 Radicale 的 HTTPS 地址、用户名和密码，连接并发现 VTODO 清单。
4. 选择要显示的清单并点击左侧色条切换颜色。

密码由当前 Windows 用户的 Credential Manager 保护。便携 ZIP 可以移动，但换电脑或换 Windows 用户后需要重新输入密码；任务缓存位于 `%LOCALAPPDATA%\FloatingTodo`。

## 开发与验证

仓库内的 `.dotnet` 是开发阶段使用的本地 .NET 8 SDK，不会进入 Git 或发布 ZIP。

```powershell
$env:DOTNET_CLI_HOME = "$PWD\.dotnet-home"
.\.dotnet\dotnet.exe restore
.\.dotnet\dotnet.exe build -c Release --no-restore
.\.dotnet\dotnet.exe run --project .\tools\CoreChecks\CoreChecks.csproj --no-restore
```

核心检查覆盖 SQLite、离线队列、iCalendar 往返、未知属性、复杂 RRULE、重复任务实例完成和 Credential Manager。

## 已知边界

- 仅支持 HTTPS CalDAV 和 Basic Authentication，已按 Radicale 的标准 CalDAV 端点设计。
- v0.1.0 仅提供 Windows x64 便携 ZIP，不含安装器、自动更新或开机启动。
- 提醒功能不在 v0.1.0 中。
