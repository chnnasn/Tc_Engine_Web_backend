# TomCat ASP.NET Core / SQLite 后端

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

生产环境必须使用 HTTPS，Cookie 设置为 Secure / HttpOnly / SameSite=Strict。建议前端和 API 通过同一域名的 `/v1` 路由访问，不启用跨域 Cookie。所有写请求（包括注册、登录）必须发送 `X-TomCat-Request: 1`，配合禁止跨域 CORS 防止跨站请求。应用读取反向代理提供的 `X-Forwarded-For` 和 `X-Forwarded-Proto`，认证接口按客户端 IP 每分钟最多 20 次。当前密码使用 ASP.NET Core PasswordHasher 哈希。

## Docker

构建并在本机运行生产镜像：

```powershell
docker build -t tomcat-api .
docker run --rm -p 8080:8080 -v tomcat-data:/data tomcat-api
```

访问 `http://127.0.0.1:8080/health` 应返回 `{"status":"ok"}`。容器会监听 `PORT`（默认 `8080`），并把 SQLite 数据库、WAL 和 Cookie 加密密钥统一写入 `/data`。

## Railway 部署

1. 在 Railway 新建服务并连接 `Tc_Engine_Web_backend` GitHub 仓库。根目录的 `Dockerfile` 会被自动识别，`railway.json` 会配置 `/health` 健康检查和失败重启。
2. 给服务添加 Volume，挂载路径必须填写 `/data`。数据库和登录 Cookie 密钥都依赖此卷；没有卷时，重新部署会丢失数据并使现有登录失效。
3. 在 Networking 中生成 Railway 域名。Railway 自动注入 `PORT`，无需手工填写端口或启动命令。
4. 保持单副本运行。当前使用单文件 SQLite 和单个 Railway Volume，不支持多副本并发部署。
5. 前端应通过同源 `/v1` 反向代理访问该服务。当前认证有意不开放跨域 Cookie；若前端与 API 使用不同来源，浏览器登录请求不会工作。

首次部署完成后访问 `https://<你的域名>/health`。返回 HTTP 200 后，再备份 Volume，并在 Railway 中启用自动备份策略。

## 接口

除健康检查、注册和登录外，接口均要求登录；其他用户的项目返回 404。

| 方法 | 路径 | 功能 |
| --- | --- | --- |
| GET | `/health` | 进程健康检查 |
| POST | `/v1/auth/register` | `{ username, password }` 注册并登录 |
| POST | `/v1/auth/login` | `{ username, password }` 登录 |
| POST | `/v1/auth/logout` | 清除当前浏览器登录 Cookie |
| GET | `/v1/auth/me` | 当前用户 |
| GET / POST | `/v1/projects` | 列出自己的项目 / 创建项目 |
| GET / PUT / DELETE | `/v1/projects/{id}` | 读取 / 更新名称描述 / 删除项目及版本 |
| GET / POST | `/v1/projects/{id}/revisions` | 版本列表 / 保存不可变修订 |
| GET | `/v1/projects/{id}/revisions/{revisionId}` | 读取原始修订 JSON |

用户名允许 3–32 个字母、数字或下划线，不区分大小写；密码 12–128 字符。创建项目使用 `{ name, description, template }`，模板为 `2D` 或 `空白`。PUT 更新名称和描述，模板创建后固定。一般请求体上限 16 MiB，单个资源上传上限 8 MiB。

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

上例只展示文件项格式；实际必须包含 `Project.tcproj`、`ProjectSettings/BuildSettings.json`、`ProjectSettings/ProjectSettings.json`、`ProjectSettings/PlayerSettings.json`，以及全部 Assets 源文件。每张图片必须附带同路径 `.tcmeta`，meta 也必须有源文件。清单最多 512 个文件、总量 36 MiB，活动场景归档上限 4 MiB；拒绝路径穿越及大小写重复路径。所有 uint64 Handle 保持字符串。

修订事务持有写锁，校验 ETag、资源所属项目、实际哈希和长度，将修订、文件引用与项目指针一并提交。上传缺失、伪造或跨项目引用不会生成修订。文件为不可变 SQLite BLOB，历史修订始终引用原字节；新上传不会改变旧修订。每项目存储额度 256 MiB，每账号 1 GiB；后续上传时清理该项目超过 24 小时且无修订引用的孤立上传。已引用资源随历史版本保留，删除项目会级联删除全部资源。

旧 schemaVersion 1 修订保持可读；新写入 v1 仅允许空 assets，不能冒充完整资源备份。API 校验结构及完整性，场景/配置/图片的引擎语义由固定版本 WASM 在恢复时验证。尚未实现发布、密码重置和服务器端会话撤销；退出登录清除客户端 Cookie。

## 验收

```powershell
dotnet build TomCat.Api -c Release --no-restore
node --test tests/api.test.mjs
```

脚本启动真实 Kestrel 和临时 SQLite 数据库，验证旧数据库迁移、注册登录、上传及去重、哈希和大小限制、项目归属、修订引用完整性、ETag 竞争、历史字节不可变、重启持久化与级联删除。只创建测试账户和临时数据，不使用开发数据库。测试退出后关闭子进程并清理它创建的临时目录。
