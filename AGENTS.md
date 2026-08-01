# Floating Todo 项目规则

- 项目定位：Windows 11 上的 WPF 桌面待办 HUD；SQLite 本地清单可独立使用，CalDAV VTODO 同步可选。
- 技术栈：.NET 8、WPF、Microsoft.Data.Sqlite、Ical.Net；发布目标为 Windows x64。
- 验证：先执行 `dotnet restore .\tools\CoreChecks\CoreChecks.csproj`，再执行 `dotnet build .\FloatingTodo.csproj -c Release --no-restore` 和 `dotnet run --project .\tools\CoreChecks\CoreChecks.csproj -c Release --no-restore`。
- 目录：应用源码位于仓库根目录；验证工具位于 `tools/CoreChecks`，发布逻辑位于 `tools/Publish.ps1`，发布配置位于 `Properties/PublishProfiles`。
- 约定：以当前代码、项目配置和核心检查为事实来源；只为非显然的系统边界、迁移与竞态不变量写注释，用户行为变化同步到 README，已发布历史保留在 CHANGELOG。
- 当前边界：仅支持 HTTPS Basic Authentication；本地任务不会自动迁移到 CalDAV；密码只存入 Windows Credential Manager。
