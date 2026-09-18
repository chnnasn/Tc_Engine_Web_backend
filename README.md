# TomCat ASP.NET Core / SQLite 后端

本仓库由 `Tc_Engine_Web_Front` 的 `codex/aspnet-sqlite` 分支迁移而来。使用 .NET 10 和 Microsoft.Data.Sqlite，SQL 参数化，启动时在事务中按 `PRAGMA user_version` 应用 `Migrations/001_initial.sql`。SQLite 使用 WAL 和外键约束。

原生 SQLite 使用独立的 `SQLitePCLRaw.bundle_e_sqlite3` 3.0.5 依赖，避免 Microsoft.Data.Sqlite 10.0.3 默认传递引入的旧版原生库。日志写入控制台，便于本地终端和容器收集。

## 运行

需要 .NET 10 SDK 和 Node.js（HTTP 验收脚本）。在仓库根目录运行：

```powershell
dotnet restore TomCat.Api/TomCat.Api.csproj --configfile NuGet.Config
$env:ASPNETCORE_ENVIRONMENT = 'Development'
dotnet run --project TomCat.Api --no-launch-profile -- --urls http://127.0.0.1:5080
```

默认数据库为 `TomCat.Api/App_Data/tomcat.db`。通过环境变量 `Storage__Directory` 指定持久化目录，数据库和 Cookie 加密密钥都会保存在该目录。该目录已被 Git 忽略。部署时挂载持久化卷，备份应包含数据库和密钥，运行中的 SQLite 应通过备份 API 或在服务停止后备份，不能只拷贝活跃的 `.db` 文件而漏掉 WAL。

生产环境必须使用 HTTPS，Cookie 设置为 Secure / HttpOnly / SameSite=Strict；将 `AllowedHosts` 改为实际域名。建议前端和 API 通过同一域名的 `/v1` 路由访问，不启用跨域 Cookie。所有写请求（包括注册、登录）必须发送 `X-TomCat-Request: 1`，配合禁止跨域 CORS 防止跨站请求。认证接口每个来源 IP 每分钟最多 20 次；代理部署时需另行配置可信转发代理及限流策略。当前密码使用 ASP.NET Core PasswordHasher 哈希。

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

用户名允许 3–32 个字母、数字或下划线，不区分大小写；密码 12–128 字符。创建项目使用 `{ name, description, template }`，模板为 `2D` 或 `空白`。PUT 更新名称和描述，模板创建后固定。请求体上限 2 MiB。

保存修订契约与前端仓库的 `src/platform/cloud-client.ts` 一致。首次保存要求 `If-None-Match: *`，随后必须发送上次返回的 `If-Match: "revisionId"`。缺少条件返回 428，过期条件返回 412 和当前 ETag。版本插入和项目指针更新在同一个持有写锁的事务中完成，防止并发覆盖。

```json
{
  "schemaVersion": 1,
  "project": "Project.tcproj 的文本内容",
  "settings": { "BuildSettings.json": "{}" },
  "scenes": { "18446744073709551615": "场景文本" },
  "assets": []
}
```

资产字段当前只保存元数据，尚未校验上传文件、内容哈希与引擎语义。场景及 Handle 保持字符串。本版本尚未实现资源上传、发布任务、管理员、密码重置或账号级会话撤销；`publish/release` 客户端方法仍预留，服务端没有对应接口。退出登录清除客户端 Cookie，未建立服务器端会话撤销表。

Vue 页面仍使用本地原型数据，没有自动改为云端模式。后续需接入登录界面、项目 API，并把场景转换成实际引擎协议，再替换 localStorage 流程。

## 验收

```powershell
dotnet build TomCat.Api -c Release --no-restore
node --test tests/api.test.mjs
```

脚本启动真实 Kestrel 和临时 SQLite 数据库，验证注册登录、项目归属、版本不可变、ETag 竞争、重启持久化与删除。只创建测试账户和临时数据，不使用开发数据库。测试退出后关闭子进程并清理它创建的临时目录。
