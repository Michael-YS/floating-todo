# Floating Todo

Floating Todo 是一个仅面向 Windows 11 的轻量、本地优先桌面待办 HUD。它可以完全离线使用，也可以按需连接 CalDAV，同步 VTODO 任务。它常驻桌面层，普通应用会覆盖它；点击托盘图标可临时置顶 15 秒。

> **CalDAV 不是必需项。** 首次启动会自动建立“个人”“工作”“购物”“健康”四个本地清单；不配置任何账户也能正常创建、编辑和完成任务。只有需要跨设备同步、远端备份或与其他待办客户端共享任务时，才需要配置 CalDAV。

## 当前功能

- 未配置 CalDAV 时使用四个内置本地清单；连接后 HUD 切换到已启用的远端清单，并可合并显示多个 CalDAV 账户。任务左侧色条用于区分清单。
- 按逾期、今天、明天和后续日期分组；内容少时自动缩短，最多占满当前屏幕工作区且不遮挡任务栏。
- 在 HUD 内查看、创建和完成任务；新任务默认今天，标题、清单和日期可用一次键盘操作完成。
- 完成后保留 5 分钟，可再次点击撤销。
- 托盘左键临时显示 15 秒；悬停暂停，移出后重新计时 15 秒。
- 可在设置中启用当前用户的 Windows 开机启动。
- 可在设置中通过滑块或百分比输入调整 HUD 透明度；设为 0% 时隐藏 HUD，仍可通过托盘临时显示。
- 详情编辑标题、清单、开始时间、截止日期和时间、描述、四档优先级以及 RFC 5545 重复规则。
- 常用重复预设及高级 RRULE；完成重复任务时写入 `RECURRENCE-ID` 例外，不结束整个系列。
- 任务保存在本地 SQLite；CalDAV 任务的离线改动进入待上传队列，界面操作不等待网络。
- 同步及未处理异常的详细信息写入 EXE 同目录的 `FloatingTodo.log`，日志达到 2 MB 时自动轮换。
- ETag 条件写入、逐字段冲突合并、未知 iCalendar 属性保留。
- 密码只写入 Windows Credential Manager；配置和数据库不保存密码，也不引入应用自定义加密。
- 插电时每分钟检查，电池供电时每五分钟检查；有 CTag 时先做轻量变更检测。恢复联网或系统唤醒后立即同步。

## 使用

1. 解压发布 ZIP，运行 `FloatingTodo.exe`。
2. 直接使用自动建立的本地清单创建任务，无需联网或配置账户。
3. 如需同步，右键托盘图标并打开“设置”，输入 CalDAV HTTPS 地址、用户名和密码。
4. 连接并发现 VTODO 清单，然后选择要显示的清单；点击左侧色条可以切换颜色。首次连接成功后，内置示例任务会被删除，本地清单会隐藏。

全部任务数据都保存在 `%LOCALAPPDATA%\FloatingTodo\floating-todo.db`。离线使用时，这个数据库就是任务的本地数据源；配置 CalDAV 后，它同时作为本地缓存和待同步队列。

连接 CalDAV 不会自动迁移已经创建的本地任务。这些任务仍保留在数据库中，并会在删除全部 CalDAV 账户后重新显示。

CalDAV 密码由当前 Windows 用户的 Credential Manager 保护。便携 ZIP 可以移动，但本地数据不会随 EXE 自动迁移；换电脑或换 Windows 用户后，需要复制数据库，并重新输入 CalDAV 密码。

## 开发与验证

开发需要 .NET 8 SDK。若本机使用仓库根目录下的私有 `.dotnet` SDK，它和 NuGet、构建缓存都不会进入 Git 或发布 ZIP。

```powershell
$env:DOTNET_CLI_HOME = "$PWD\.dotnet-home"
dotnet restore .\tools\CoreChecks\CoreChecks.csproj
dotnet build .\FloatingTodo.csproj -c Release --no-restore
dotnet run --project .\tools\CoreChecks\CoreChecks.csproj -c Release --no-restore
```

核心检查覆盖 SQLite、离线队列、iCalendar 往返、未知属性、复杂 RRULE、重复任务实例完成和 Credential Manager。

## 发布

不要直接分发 `bin/Release`，其中可能混有普通构建和旧的自包含运行时。使用发布脚本生成干净 ZIP：

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\Publish.ps1
```

输出位于 `artifacts/releases`：

- `FloatingTodo-v<版本>-win-x64-portable.zip`：自包含单文件，无需预装 .NET；体积较大。
- `FloatingTodo-v<版本>-win-x64-slim.zip`：单文件精简包，需要目标电脑安装 .NET 8 Desktop Runtime。

GitHub Actions 会在 `main`、Pull Request 和手动触发时运行核心检查并上传两个 ZIP。推送 `v*` 标签时，还会自动创建 GitHub Release 并附加这两个文件。

## 已知边界

- CalDAV 同步是可选功能；启用后仅支持 HTTPS 和 Basic Authentication，已按 Radicale 的标准 CalDAV 端点设计。
- 当前版本不会在本地清单与 CalDAV 清单之间自动迁移任务。
- v0.1.2 仅提供 Windows x64 便携 ZIP，不含安装器或自动更新。
- 提醒功能不在 v0.1.2 中。
