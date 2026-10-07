# TomCat ASP.NET Core / SQLite 后端

## Web AI 编辑器会话

新增 `EditorSessions.cs`，为相邻 `Tc_Engine_Web_Mcp` 的 LangChain Agent 提供按账号、云端项目和浏览器会话隔离的命令通道。设置 `Agent__Url` 和至少 32 字符的 `Agent__Secret`（与 Python 的 `TOMCAT_AGENT_SECRET` 一致）后，前端可通过 AI 面板执行。

`POST /v1/editor-sessions/` 注册；`GET /{id}/commands` 长轮询；`POST /{id}/commands/{commandId}/result` 回传；`POST /{id}/agent` 运行 Agent；`DELETE /{id}` 撤销。所有这些路径都在 `/v1/editor-sessions` 下且要求现有登录；写请求继续要求 X-TomCat-Request。项目所有权在访问时核对。

`/internal/editor-session` 和 `/internal/editor-session/call` 只接受后端签发的随机会话 Bearer 凭证，拒绝 Origin；应限制私网访问。凭证不返回浏览器。命令结果在进程内去重，未知重试 ID 拒绝，重启使会话失效；单副本运行。Agent 最长请求约 190 秒，部署代理需允许该超时。运行 `node --test tests/*.test.mjs` 包含会话权限、结果绑定、去重、撤销测试。

本仓库由 `Tc_Engine_Web_Front` 的 `codex/aspnet-sqlite` 分支迁移而来。使用 .NET 10 和 Microsoft.Data.Sqlite，SQL 参数化，启动时在事务中按 `PRAGMA user_version` 依次应用 `Migrations/001_initial.sql` 和 `002_uploads.sql`，保留已有修订。SQLite 使用 WAL 和外键约束。

原生 SQLite 使用独立的 `SQLitePCLRaw.bundle_e_sqlite3` 3.0.5 依赖，避免 Microsoft.Data.Sqlite 10.0.3 默认传递引入的旧版原生库。日志写入控制台，便于本地终端和容器收集。

## 运行

需要 .NET 10 SDK 和 Node.js（HTTP 验收脚本）。在仓库根目录运行：

```powershell
dotnet restore TomCat.Api/TomCat.Api.csproj --configfile NuGet.Config
$env:ASPNETCORE_ENVIRONMENT = 'Development'
dotnet run --project TomCat.Api --no-launch-profile -- --urls http://127.0.0.1:5080
```

默认数据库为 `TomCat.Api/App_Data/tomcat.db`。通过环境变量 `Storage__Directory` 指定持久化目录，数据库和 Cookie 加密密钥都会保存在该目录。该目录已被 Git 忽略。部署时挂载持久化卷，备份应包含数据库和密钥，运行中的 SQLite 应通过备份 API 或在服务停止后备份，不能只拷贝活跃的 `.db` 文件而漏掉 WAL。

生产环境必须使用 HTTPS，Cookie 设置为 Secure / HttpOnly / SameSite=Strict。建议前端和 API 通过同一域名的 `/v1` 路由访问，不启用跨域 Cookie。所有写请求（包括注册、登录）必须发送 `X-TomCat-Request: 1`，配合禁止跨域 CORS 防止跨站请求。应用读取反向代理提供的 `X-Forwarded-For` 和 `X-Forwarded-Proto`，认证接口按客户端 IP 每分钟最多 20 次。Netlify 部署须配置 `Proxy__Secret`，与 Netlify Runtime 变量 `TOMCAT_PROXY_SECRET` 相同；应用验证 `x-nf-sign` 的 HS256 签名和有效期后，使用 Netlify 提供的 `x-nf-client-connection-ip` 限流，避免 CDN 出口变化导致计数分散。配置签名密钥后，未签名的认证请求返回 403，认证请通过前端同源 `/v1/auth/*` 访问。当前密码使用 ASP.NET Core PasswordHasher 哈希。

## Docker

构建并在本机运行生产镜像：

```powershell
docker build -t tomcat-api .
docker run --rm -p 8080:8080 -v tomcat-data:/data tomcat-api
```

访问 `http://127.0.0.1:8080/health` 应返回 `{"status":"ok"}`。容器会监听 `PORT`（默认 `8080`），并把 SQLite 数据库、WAL 和 Cookie 加密密钥统一写入 `/data`。

镜像为多阶段构建：除 .NET 发布外，还会用 `emscripten/emsdk` 检出并构建上游引擎的 `tomcat_player`，并用 .NET SDK 构建 C# 脚本编译所需的托管工具链（见下「服务端打包」）。首次构建需要拉取 emsdk 镜像、克隆引擎并编译 C++ 归档，耗时和资源占用明显高于纯 .NET 构建；构建完成后运行时不包含 emsdk 与编译器，但**保留 .NET SDK**（带 C# 脚本的项目要在容器内运行 `dotnet build`）。不需要 C# 打包时，可把 `Dockerfile` 运行时阶段的基础镜像换回 `mcr.microsoft.com/dotnet/aspnet:10.0` 以显著减小体积。

## Railway 部署

1. 在 Railway 新建服务并连接 `Tc_Engine_Web_backend` GitHub 仓库。根目录的 `Dockerfile` 会被自动识别，`railway.json` 会配置 `/health` 健康检查和失败重启。此检查直接访问 Railway 服务容器，不经过前端 Netlify 域名。
2. 给服务添加 Volume，挂载路径必须填写 `/data`。数据库和登录 Cookie 密钥都依赖此卷；没有卷时，重新部署会丢失数据并使现有登录失效。
3. 在 Networking 中生成 Railway 域名。Railway 自动注入 `PORT`，无需手工填写端口或启动命令。
4. 保持单副本运行。当前使用单文件 SQLite 和单个 Railway Volume，不支持多副本并发部署。
5. 前端应通过同源 `/v1` 反向代理访问该服务。当前认证有意不开放跨域 Cookie；若前端与 API 使用不同来源，浏览器登录请求不会工作。

打包器与 C# 编译链都已随镜像构建（`Cook__CliPath` / `Cook__CliArgs` 由 `Dockerfile` 的 `ENV` 预设），因此**不需要**再挂载 Windows 主机、另起 cook 服务或配置 GitHub Actions 去构建桌面 CLI。部署时请给构建步骤留出足够时间与内存：`player` 阶段会编译引擎 C++ 归档，属于重构建；如果 Railway 构建资源紧张，可先在 CI 里 `docker build` 并推送镜像，再让 Railway 拉取。运行时容器会在每次发布带 C# 的项目时执行一次 `dotnet build`（秒级），需要可写的 `/tmp`（`NUGET_PACKAGES` 指向 `/tmp/tomcat-nuget`），且会占用额外 CPU。

首次部署完成后访问 `https://<你的域名>/health`。返回 HTTP 200 后，再备份 Volume，并在 Railway 中启用自动备份策略。

## 接口

除健康检查、注册、邮箱验证、注册完成、登录和已发布作品的公开游玩接口外，接口均要求登录；其他用户的项目返回 404。

| 方法 | 路径 | 功能 |
| --- | --- | --- |
| GET | `/health` | 进程健康检查 |
| POST | `/v1/auth/register` | `{ email, password }` 发送邮箱验证码；验证后直接完成注册 |
| POST | `/v1/auth/login` | `{ email, password }` 登录，仅接受已验证邮箱 |
| POST | `/v1/auth/logout` | 清除当前浏览器登录 Cookie |
| GET | `/v1/auth/me` | 当前用户 |
| GET / POST | `/v1/projects` | 列出自己的项目 / 创建项目 |
| GET / PUT / DELETE | `/v1/projects/{id}` | 读取 / 更新名称描述 / 删除项目及版本 |
| GET / POST | `/v1/projects/{id}/revisions` | 版本列表 / 保存不可变修订 |
| GET | `/v1/projects/{id}/revisions/{revisionId}` | 读取原始修订 JSON |
| POST / GET / DELETE | `/v1/projects/{id}/publish` | 发布 `{ title, description }` / 查询状态 / 取消发布 |
| GET | `/v1/games/published` | 公开列出已发布作品（游客可访问） |
| GET | `/v1/games/published/{id}` | 公开作品详情（游客可访问） |
| GET | `/v1/games/published/{id}/package` | 公开游戏包下载，ETag 为内容 SHA-256（游客可访问） |

邮箱为唯一登录标识，不区分大小写；密码 12–128 字符。创建项目使用 `{ name, description, template }`，模板为 `2D` 或 `空白`。PUT 更新名称和描述，模板创建后固定。一般请求体上限 16 MiB，单个资源上传上限 8 MiB。

完整保存使用前端 `src/engine/cloud.ts` 的 schemaVersion 2 契约。首次修订要求 `If-None-Match: *`，随后发送上次返回的 `If-Match: "revisionId"`。缺少条件返回 428，过期条件返回 412；客户端必须保留旧凭据和本地草稿，不得自动覆盖。

先对每个文件计算 SHA-256，通过 `PUT /v1/projects/{id}/uploads/{contentHash}` 上传原始二进制内容。成功返回 `{ uploadId, contentHash, size }`；同项目相同内容幂等复用。`GET /v1/projects/{id}/uploads/{uploadId}` 返回原始字节，仅项目所有者可访问。所有上传写请求仍要求认证 Cookie 和 X-TomCat-Request。

然后 POST 修订清单：

```json
{
  "schemaVersion": 2,
  "engineCommit": "40 位固定引擎提交",
  "sceneHandle": "18446744073709551615",
  "archive": "活动场景归档文本",
  "files": [
    { "path": "Project.tcproj", "uploadId": "32 位上传 ID", "contentHash": "64 位 SHA-256", "size": 123 }
  ]
}
```

上例只展示文件项格式；实际必须包含 `Project.tcproj`、`ProjectSettings/BuildSettings.json`、`ProjectSettings/ProjectSettings.json`、`ProjectSettings/PlayerSettings.json`，以及全部 Assets 源文件。每张图片（`.png/.jpg/.jpeg/.tga`）与每个 C# 脚本（`.cs`）必须附带同路径 `.tcmeta`，meta 也必须有源文件。清单最多 512 个文件、总量 36 MiB，活动场景归档上限 4 MiB；拒绝路径穿越及大小写重复路径。所有 uint64 Handle 保持字符串。

修订事务持有写锁，校验 ETag、资源所属项目、实际哈希和长度，将修订、文件引用与项目指针一并提交。上传缺失、伪造或跨项目引用不会生成修订。文件为不可变 SQLite BLOB，历史修订始终引用原字节；新上传不会改变旧修订。每项目存储额度 256 MiB，每账号 1 GiB；后续上传时清理该项目超过 24 小时且无修订引用的孤立上传。已引用资源随历史版本保留，删除项目会级联删除全部资源。

旧 schemaVersion 1 修订保持可读；新写入 v1 仅允许空 assets，不能冒充完整资源备份。API 校验结构及完整性，场景/配置/图片的引擎语义由固定版本 WASM 在恢复时验证。尚未实现密码重置和服务器端会话撤销；退出登录清除客户端 Cookie。

## 作品发布与打包

Docker 打包器与前端现统一固定为 `41708b6c756d530a1c71f0e0ef2539a1df1bb03e`，输出 TCPAK v8。前后端应协调上线：双方严格校验当前提交，版本混用会拒绝完整项目保存或游戏包加载。Linux 容器仍使用 Node 驱动 Emscripten Web Player，不运行 Windows CLI 或原生 DLL 模块。

发布把项目最新一次云端保存的修订交给已配置的打包器打包为 TCPAK，产出对游客公开的游戏包。容器镜像内置引擎自带的 Emscripten 打包器（见下「服务端打包」），因此 linux-x64 部署不再依赖 Windows 桌面 CLI。发布前会先把 Redis 待落库快照固化为正式修订（基线冲突返回 409）；同一项目同时只允许一个进行中的发布任务（409），失败会记录原因并可在排除问题后重新发布。取消发布或删除项目会立即移除公开入口（删除项目经外键级联）。CookWorker 为单实例设计：同一时刻只打包一个任务，进程重启后未完成任务自动重试。

配置：

- `Cook__CliPath`：打包工具可执行文件路径。未配置或文件不存在时，发布接口返回 503。容器镜像默认 `/usr/local/bin/node`（镜像内已带 Node 与 Web Player 打包器）。
- `Cook__CliArgs`：参数模板（默认 `cook --project "{project}" --output "{output}"`）。容器镜像默认 `/app/cook/worker.mjs "{project}" "{output}"`，即用 Node 驱动 Emscripten 打包器；也支持上游包装形式，例如 `--cli cook --project "{project}" --output "{output}"`。
- `Cook__TimeoutSeconds`：单次打包超时（默认 900，下限 30）；超时 kills 整个进程树。
- `Cook__PollSeconds`：后台任务轮询间隔（默认 3）。

打包过程：worker 把修订清单中的文件与上传字节物化到临时目录；由于 Web 编辑会话的场景只存在于归档字符串（MEMFS 磁盘上没有场景文件），worker 会写出 `Assets/Scene/WebScene.tomcat`、对应 `.tcmeta`，并把 `ProjectSettings/BuildSettings.json` 指向该场景（`entrySceneHandle` = 修订的 `sceneHandle`），与上游 `Samples/PhysicsPlayground` 的磁盘布局一致。随后运行打包器（项目带 C# 脚本时，容器会先用 `dotnet build` 编译并按发现路径布局落盘，见下；桌面 CLI 也会先编译并校验 C# 源码。两者都要求 `BuildSettings` 配置了入口场景），读取产出的游戏包，按内容 SHA-256 记录入库。游戏包上限 256 MiB，ETag 即 SHA-256，缓存策略与内置示例包一致（immutable + 304）。用户项目是喂给打包器的不可信输入：物化在一次性临时目录中，路径经过 `SafePath` 白名单，资源字节在读取时重新校验归属与哈希，打包器以独立进程运行并受超时约束。

服务端打包（容器内 Node + Emscripten 打包器）：

上游 `54697ebf` 泛化了播放器的 cook 入口（`tc_web_player_cook(projectPath, outputPath)`，旧符号 `tc_web_player_cook_sample` 保留为委托 shim），于是「引擎自带的 TCPAK writer」可以在浏览器之外的 Node 里运行。本仓库据此把打包器搬进了 API 容器，linux-x64 部署不再需要 Windows 主机、额外的 cook 服务，也不需要预编译 CLI。

- `Dockerfile` 的 `source` 阶段检出上游引擎（按顶部 `ARG ENGINE_COMMIT`，当前 `54697ebf723749783bea6df6e9f7f7afd3af6530`）并初始化 `Box2D/glm/spdlog/ImGuizmo` 子模块，供后续阶段共用。
- `player` 阶段用 `emscripten/emsdk:4.0.15` 只构建 `tomcat_player` 目标（`emcmake` + CMake + Ninja）。该目标**不需要 .NET SDK，也不需要 wasm-tools 工作负载**。产物 `tomcat_player.js/.wasm/.data` 在运行时阶段落到 `/app/player`。
- `managed` 阶段用 .NET SDK 构建 `TomCat.Managed.dll`（脚本 API 面）与 `TomCat.ScriptGenerator.dll`（Roslyn 源生成器），与打包器同一提交，落到运行时镜像的 `/app/managed`。
- `cook/worker.mjs` 是容器内的打包入口：CookWorker 物化出的项目目录整棵镜像进模块 MEMFS（同一绝对路径，`Project::Load` 与 `CookToPackage` 都接受绝对路径），调用 `tc_web_player_cook(projectPath, outputPath)`，再把产物拷回宿主路径。失败时打印 `tc_web_player_error()` 并以 cook 返回码（1/2/3）退出，发布记录照常得到真实原因；基础设施错误（缺模块、缺参数、C# 编译失败）用退出码 4 区分。它接受位置参数，也能解析 `cook --project X --output Y` 形态。
- 版本策略：容器内打包器固定 `54697ebf`（泛化 cook 入口从该提交起才有），浏览器播放器继续锁 `684eb8f3`。两者 `ManagedApiCurrent = 5` 一致、TCPAK 格式一致，所以新打包器产出的包（含 C# 载荷）可以被当前网页播放器直接加载，前端 `engine.lock.json` 不需要被迫升级。打包器提交同时写入环境变量 `TOMCAT_COOK_ENGINE_COMMIT` 便于核对，游玩页已有的版本警告横幅作为兜底；后续前端锁升级到 ≥ `54697ebf` 时自然对齐。
- 更换打包器提交只需改 `Dockerfile` 顶部的 `ARG ENGINE_COMMIT`（或构建时传 `--build-arg ENGINE_COMMIT=<sha>`），`source`/`player`/`managed` 阶段会重新检出并构建。

C# 项目打包（替代桌面 `CompileManaged`）：

上游 `ScriptProjectCompiler` 的编译实现整段包在 `#ifdef TC_PLATFORM_WINDOWS` 里，非 Windows 直接返回「Script compilation is not implemented on this platform」。`cook/managed-payload.mjs` 在 Linux 上复刻同一套契约：

1. 扫描 `Assets/` 下的全部 `.cs`，从同路径 `.tcmeta` 的 `Handle:` 取资产 Handle（uint64 十进制，绝不经 `Number`），生成源生成器读取的 `ScriptAssets.json`。
2. 按桌面 `WriteGeneratedProject` 的形态生成 `Library/ScriptProject/Build/<id>/Assembly-CSharp.csproj`：`net10.0`、`AssemblyName=Assembly-CSharp`、`TomCat.Managed` 作为受信 `Reference`、`TomCat.ScriptGenerator` 作为 `Analyzer`、`ScriptAssets.json` 作为 `AdditionalFiles`，并保留上游那套禁用环境/目录导入与 NuGet 引用的加固属性。**不设置 `RuntimeIdentifier`**：载荷由引擎标记为 `portable`。
3. 运行 `dotnet build`（全局属性锁与桌面 `RunRestrictedDotNetBuild` 一致，并清空包源以保证离线），产物在 `Library/ScriptAssemblies/Build/<id>/Assembly-CSharp.dll(.pdb)`。
4. 按上游 `ExtractEmbeddedScriptManifest` 的算法，从 DLL 元数据 `#US` 堆里把 UTF-16LE 的脚本清单原样取出——引擎要求「注入的清单必须与程序集内嵌的清单完全相同」，所以清单不从零构造，而是逐字复用生成器的输出。
5. 按 `AssetManager::LoadProjectManagedPayload` 的磁盘契约落盘：`Library/ScriptAssemblies/last-good.json`（`{version:1, sourceHash: 16 位 hex, buildId, assembly: "Build/<id>/Assembly-CSharp.dll", pdb?}`）与 `Library/ScriptProject/ScriptAssets.json`，worker 把这两处镜像进 MEMFS，cook 的发现路径自行读取。**不**走 `tc_web_player_set_cook_payload` 预注入——`tc_web_player_cook` 内部的 `SetProject`→`Initialize`→`Shutdown` 会清掉载荷覆写（上游 54697ebf 的顺序问题，正向用例实测复现）；发现路径是桌面 CLI 同款的官方通道，零上游改动。

因此运行时镜像刻意使用 .NET SDK 基础镜像而非 aspnet 镜像：容器内需要 MSBuild 与 Roslyn。相关环境变量：`TOMCAT_MANAGED_DIR`（默认 `/app/managed`）、`TOMCAT_DOTNET`（默认 `dotnet`）。离线调试可用 `TOMCAT_COOK_ASSEMBLY` / `TOMCAT_COOK_MANIFEST` / `TOMCAT_COOK_BUILD_ID`（可选 `TOMCAT_COOK_PDB`）直接注入一份现成载荷，此时跳过容器内编译。项目若带 `TomCat.Dependencies.csproj`（项目内依赖工程），容器打包器会明确拒绝并给出诊断，请改用桌面 Editor 打包。

本机 Windows 调试仍可使用桌面 CLI（已不是部署路径）：

- 官方 Editor 发行版没有独立的 CLI 下载：`TomCatCLI.exe` 与 Managed 工具链、Player Template 一起经 Enigma Virtual Box 打包进单个 `TomCat.exe`（上游 `Scripts/Package-Editor.ps1` + `editor.evb`）。调用方式为 `TomCat.exe --cli cook ...`：包装器（`TomCatInputApp.cpp`）校验内嵌清单，把匹配的运行时解压/复用到 `%LOCALAPPDATA%\TomCat\Editor\Runtime`，再以子进程运行真正的 CLI 并**透传退出码**。因此 `Cook__CliPath` 可指向 `TomCat.exe`、`Cook__CliArgs=--cli cook --project "{project}" --output "{output}"`；失败诊断照常进入发布记录，worker 的超时整树终止对无超时的包装器同样有效。注意运行账户需要可写的用户配置目录（运行时解压位置）。
- 源码构建可直接得到独立 CLI（不经过 EVB 打包）：VS 开发者环境中执行 `vendor/premake/bin/premake5.exe --file=Tools/premake5.lua vs2022` 后 `msbuild Tools/Tools.sln -p:Configuration=Release -p:Platform=x64`，产物 `Tools/bin/Release-windows-x86_64/TomCatCLI/TomCatCLI.exe` 直接作为 `Cook__CliPath`。`vendor/premake/bin` 不在仓库内时，按 `Scripts/ReleaseToolVersions.json` 锁定的版本（5.0.0-beta7，含 SHA-256）下载释放即可。CLI 必须与 Managed 工具链构建自相近的提交：托管 ABI（ManagedApiV1）随引擎演进，旧 CLI 配新工具链会在编译校验时报 `TCSP0014: ManagedApiV1 table 不兼容`——遇到该错误先重编 CLI。本机重编时可顺带带上 `54697ebf` 之后的两个 scripting 修复。

CLI 与网页引擎应来自兼容的引擎提交：桌面 CLI 与 `engine.lock.json` 保持一致；容器内打包器用 `Dockerfile` 固定的提交，与 `engine.lock.json` 的 Managed API 版本必须相同。

## 验收

```powershell
dotnet build TomCat.Api -c Release --no-restore
node --test tests/api.test.mjs
node --test tests/publish.test.mjs
node --test tests/cook-worker.test.mjs
node --test tests/cook-managed-payload.test.mjs
```

脚本启动真实 Kestrel 和临时 SQLite 数据库，验证旧数据库迁移、注册登录、上传及去重、哈希和大小限制、项目归属、修订引用完整性、ETag 竞争、历史字节不可变、重启持久化与级联删除。`tests/publish.test.mjs` 以确定性假 CLI 走通完整发布链路：保存修订 → 发布 → 物化与打包 → 公开列表/详情/游戏包/304 → 失败报告与重试 → 取消发布与级联删除，并验证未配置 `Cook__CliPath` 时返回 503。只创建测试账户和临时数据，不使用开发数据库。测试退出后关闭子进程并清理它创建的临时目录。

`tests/cook-worker.test.mjs` 覆盖容器内打包 worker 的契约：位置参数与 `cook --project X --output Y` 两种形态的解析、缺少参数/缺少项目文件/缺少 Web Player 模块时非零退出并给出可读诊断。`tests/cook-managed-payload.test.mjs` 覆盖 C# 载荷构建的纯逻辑：`.tcmeta` Handle 解析、`ScriptAssets.json` 保留 uint64 精度、`Assets` 脚本枚举与排序、从程序集 UTF-16LE 字节中逐字取出内嵌脚本清单、生成的 `Assembly-CSharp.csproj` 关键契约，以及缺工具链/无脚本时的行为。两者都不需要 Emscripten 产物或 .NET SDK，可在纯 Node 环境运行；容器内的真实 `dotnet build` 与 cook 闭环需要在装有 `/app/player`、`/app/managed` 的镜像里验证。

## Redis 自动同步与定期落库

AI 任务检查点复用 `POST /v1/projects/{id}/revisions`，其 schemaVersion 2 清单可带 `aiCheckpoint: { runId, phase: "start" | "end", sceneVersion }`。元数据与场景、资源引用一同保存在不可变修订中，历史列表返回此字段；不需要新数据库迁移。Redis 模式也立即落库，`PUT /working-state` 拒绝带检查点的延迟保存。检查点沿用项目所有权及 If-Match/If-None-Match 检查，不代表自动回滚或 Agent 断点续跑。

配置 `Redis__ConnectionString` 后启用；留空继续使用原有手动保存。仅支持单个 API 实例和独立 Redis（不支持 Redis Cluster）；不同数据库必须使用不同的 `Redis__KeyPrefix`，且只能由一个 API 进程写入。

本地运行（Docker 环境）：

```powershell
docker compose -f compose.redis.yml up -d
$env:Redis__ConnectionString = '127.0.0.1:6379'
$env:Redis__FlushIntervalSeconds = '30'
$env:ASPNETCORE_ENVIRONMENT = 'Development'
dotnet run --project TomCat.Api --no-launch-profile -- --urls http://127.0.0.1:5080
```

线上设置同名环境变量，使用私有 Redis 地址及其认证/TLS 参数。不要把 Redis 端口暴露到公网。Redis 与 SQLite 各自需要持久化卷；示例 Compose 使用 AOF everysec 和 noeviction。AOF everysec 在故障时仍可能丢失最近约一秒的写入，不能视为零丢失保证。参考 [Redis persistence](https://redis.io/docs/latest/operate/oss_and_stack/management/persistence/)。

编辑器在编辑模式下，每次同步完成后约 2 秒捕获一次完整项目；内容未变不再上传。先将草稿和原 ETag 写入 IndexedDB，再检查资源 SHA-256，复用服务器已有文件，仅上传缺失的二进制文件。场景归档仍然全量同步，不是对象级操作增量，也未加入哈夫曼编码。资源二进制继续保存在 SQLite，Redis 保存完整场景归档及资源引用清单。

- `GET /v1/projects/sync-config`：是否开启自动同步。
- `PUT /v1/projects/{id}/working-state`：提交 schemaVersion 2 快照；要求原有 If-Match/If-None-Match 条件，成功返回 202、新 ETag 和 persisted=false。
- `GET /v1/projects/{id}/working-state`：优先读取 Redis；无工作快照时读取 SQLite 最新修订。
- `GET /v1/projects/{id}/sync-status`：当前 ETag 和是否已落库。
- `GET /v1/projects/{id}/uploads/by-hash/{hash}`：查询可复用文件，仅限项目所有者。
- 原 `POST /revisions` 在 Redis 模式下检查最新工作版本，并立即落库；历史修订接口只列已落库版本。

Redis key 为 `{prefix}project:{projectId}:state`，另有 `{prefix}dirty` 待落库集合；快照与 dirty 标记通过 Lua 一起写入，没有过期时间。后台默认每 30 秒将最新快照及文件引用在 SQLite 事务内保存，保留相同 ETag，提交成功后清理 Redis。数据库提交后、Redis 清理前发生崩溃，可根据同一修订 ID 重试而不重复创建版本。多次短间隔编辑合并成一个检查点，不会保留每个操作的历史。

自动同步仅在已关联云端的项目中开启。前端区分“已同步，等待定期落库”和“已自动保存到数据库”，确认落库且当前内容未变后才标记已保存。Redis 故障返回 503，保留本地草稿并重试；401/412 暂停上传但继续保存本地草稿，不会自动采用新 ETag 覆盖其他编辑者。请求已成功但应答丢失时，重试可能得到 412，需要恢复最新状态比较。

为防止清理尚被 Redis 引用的文件，开启 Redis 时暂时禁用原来的 24 小时孤立上传清理；项目及账号存储额度仍生效，删除项目会删除全部资源。资源回收尚未细化到待同步引用，长时间编辑或反复失败可能积累孤立文件。停用 Redis 或切换 namespace 之前须先排空 dirty 集合，避免忽略待落库快照。

验证：

```powershell
dotnet build TomCat.Api -c Release
node --test tests/api.test.mjs
$env:TEST_REDIS_SERVER = '你的 redis-server 可执行文件绝对路径'
node --test tests/redis.test.mjs
```

Redis 集成测试使用临时 Redis、临时 SQLite 和测试账号，覆盖权限、并发条件、Redis/API 重启、定时落库、提交后重复执行、立即保存、故障和删除。Redis 可执行文件旁需有 redis-cli；测试使用独立随机端口。

## 邮箱注册与旧账号绑定

新用户流程：邮箱和密码 → 六位邮件验证码 → 自动完成注册并登录。接口不接收展示用户名，用户资料仅返回 ID、邮箱及验证状态。验证码十分钟有效，最多五次错误尝试；同一邮箱重发间隔至少六十秒。验证码和注册完成令牌只保存 SHA-256 哈希，完成令牌十分钟有效且只可使用一次。邮箱验证前不创建账号、不签发登录 Cookie。

已有已验证邮箱的账号使用原邮箱、密码和 ID，项目归属不变。移除用户名登录与 bind-email 接口；无已验证邮箱的旧账号无法登录，旧会话也会被拒绝。数据库历史 username 列保留以避免重建用户及项目外键，新注册时填充随机用户 ID，仅作内部字段，不作为登录或展示信息。

Railway 发信变量（需自行申请 SMTP 服务）：

```text
Mail__Host=smtp.example.com
Mail__Port=587
Mail__From=your-verified-sender@example.com
Mail__Username=your-smtp-user
Mail__Password=your-smtp-password
```

使用 SMTP STARTTLS（通常为 587 端口），发件地址必须符合邮件服务商要求。密码仅配置在 Railway 变量中，不写入仓库。SMTP 未配置时注册和邮箱绑定返回明确的 503 提示，不绕过验证；原账号登录与已有项目不受影响。

开发环境可设置 `Mail__PickupDirectory` 为绝对路径，将实际验证邮件写入本地目录，不连接 SMTP。此模式只在 `ASPNETCORE_ENVIRONMENT=Development` 生效，禁止用于生产。生产不要配置此变量。无需配置验证链接地址，前端输入邮件验证码即可完成验证。

认证接口：
- `POST /v1/auth/register`：`{ email, password }` → `{ challengeId, expiresIn, resendAfter }`
- `POST /v1/auth/verify-email`：`{ challengeId, code }` → `{ token }`
- `POST /v1/auth/complete-registration`：`{ token }` → 用户资料及会话 Cookie
- `POST /v1/auth/login`：`{ email, password }`；仅接受已验证邮箱
- `GET /v1/auth/me`：`{ id, email, emailVerified }`

所有写请求仍需要 `X-TomCat-Request: 1`。完成注册使用一次性邮箱验证令牌。

验证：`dotnet build TomCat.Api -c Release`，然后 `node --test tests/*.test.mjs`。测试使用开发邮件目录读取真实邮件内容，不提供获取验证码的 HTTP 接口。

### Resend API（Railway 推荐）

Railway Free、Trial、Hobby 套餐禁止外连 SMTP，请使用 HTTPS API。新增 Resend 支持，后端服务 Variables 配置：

```text
Mail__Provider=Resend
Mail__ApiKey=填入新创建的仅发信API密钥
Mail__From=TomCat <noreply@你已验证的域名>
```

在 Resend 验证发件域名及 DNS 后再配置 `Mail__From`。密钥只保存到 Railway，禁止提交仓库或放入前端环境变量。使用 API 时不需要 SMTP Host、Port、Username、Password；生产不要设置开发用 PickupDirectory。未验证自己的域名时 Resend 默认试用发件地址有收件限制，不能直接用于向所有注册用户发送验证码。

### 找回密码与修改密码

登录表单提供“忘记密码”，通过已绑定且验证的邮箱接收六位验证码，再提交验证码和新密码。账号面板提供“修改密码”，必须验证当前密码。新密码须为 12–128 个字符，修改密码不能与当前密码相同。

新增接口（写请求仍需 `X-TomCat-Request: 1`）：
- `POST /v1/auth/forgot-password`：`{ email }` → `{ challengeId, expiresIn, resendAfter, message }`。未知邮箱与已绑定邮箱返回相同结构和提示；只有已验证账号会收到邮件。重发间隔至少 60 秒。
- `POST /v1/auth/reset-password`：`{ challengeId, code, newPassword }`。验证码十分钟有效，最多五次错误尝试；使用后即删除，不能复用注册验证码。
- `POST /v1/auth/change-password`（已登录）：`{ currentPassword, newPassword }`。

迁移 `005_password_recovery.sql` 保留原用户与项目，增加会话版本及独立找回密码挑战表。改密/重置密码后旧设备 Cookie 在后续请求时失效，需重新登录；已有会话没有版本标记时按版本 0 兼容。所有未完成的找回密码挑战和该账号的邮箱绑定挑战同时失效。无已验证邮箱的旧账号无法登录或使用邮箱找回。
