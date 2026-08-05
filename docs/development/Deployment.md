# 部署与运维

## 构建与部署

- Dockerfile：`src/Host/Dockerfile`
- 部署脚本：`start.sh`
- 前端构建目录：`frontend/`
- Vite 输出目录：服务根目录的 `wwwroot/`（构建产物，不提交）
- 发布后的静态文件目录：Host 发布内容中的 `wwwroot/`

Dockerfile 先构建 Vue 前端，再发布 .NET 后端，并把前端产物复制到 Host 的 `wwwroot`。运行时是一个镜像、一个容器、一个端口和一个持久化 SQLite 文件，不依赖 PostgreSQL、Consul 或 `ruoyu.common`。

## 配置项

### 服务端口

容器内 HTTP 端口固定为 5008。`start.sh` 通过 `-p ${Port}:5008` 映射 host 端口。

### SQLite 与持久卷

| 配置 | 默认值 | 说明 |
|------|--------|------|
| `ConnectionStrings:Default` | `Data Source=data/vocabulary.db` | 相对路径按应用 content root 解析 |
| `Database:InitializeOnStartup` | `true` | 启动时执行迁移和首次建库种子 |
| `VOCABULARY_DATA_DIR` | `<service>/data` | `start.sh` 挂载到容器 `/app/data` 的宿主目录 |

容器连接串固定注入为 `Data Source=/app/data/vocabulary.db`。首次运行会在挂载卷中创建文件并写入 300 词启动词书；已有文件只迁移，不重复写种子。

备份前应停止写入，然后复制 `vocabulary.db`，或使用 SQLite 在线备份工具。部署必须保持单实例，不得让多个容器同时写同一个数据库文件。

### Identity 管理员认证

| 配置键 | 默认值 | 部署来源 |
|--------|--------|----------|
| `IdentityService:Authority` | 本地运行 `http://localhost:5002` | `VOCABULARY_IDENTITY_AUTHORITY` |
| `IdentityService:Issuer` | `QuantumZhou.Identity` | appsettings 或标准 .NET 环境变量 |
| `IdentityService:Audience` | `QuantumZhou.microservices` | appsettings 或标准 .NET 环境变量 |
| `AdminAuthentication:Provider` | `QuantumZhou` | `VOCABULARY_ADMIN_AUTH_PROVIDER` |
| `AdminAuthentication:RequiredRole` | `admin` | appsettings 或标准 .NET 环境变量 |
| `AdminAuthentication:QuantumZhou:AppId` | 空 | `VOCABULARY_IDENTITY_APP_ID` |
| `AdminAuthentication:QuantumZhou:AppSecret` | 空 | `VOCABULARY_IDENTITY_APP_SECRET` |
| `AdminAuthentication:CookieSecure` | `false` | `VOCABULARY_COOKIE_SECURE` |

`start.sh` 中容器默认 Authority 为 `http://ruoyu-identity:5002`。Identity 位于其他主机时，显式设置 `VOCABULARY_IDENTITY_AUTHORITY`；无需重新构建镜像。AppId/AppSecret 只由服务端环境注入，不进入前端、默认配置或日志。

改用标准 OIDC provider 时设置 `VOCABULARY_ADMIN_AUTH_PROVIDER=Oidc`，并通过标准 .NET 配置键提供 `AdminAuthentication__Oidc__ClientId`、`ClientSecret`、`Scope` 和可选的 `TokenEndpoint`。

TLS 部署必须设置 `VOCABULARY_COOKIE_SECURE=true`。缺少 provider 凭据时服务仍可启动，但管理员登录返回 503。

## 认证入口

| 路径 | 匿名 | 说明 |
|------|------|------|
| `POST /admin/auth/login` | 是 | 代理 Identity password grant，成功后设置 HttpOnly Cookie |
| `GET /admin/auth/session` | 否 | 要求管理员角色，供前端恢复状态 |
| `POST /admin/auth/logout` | 是 | 幂等删除 Cookie |
| `GET /health` | 是 | 服务存活检查 |

除认证入口外的全部 `/admin/*` 要求管理员角色。四个既有 `/api/*` 业务接口保持匿名。

## 部署后检查

```bash
curl http://localhost:5008/health
curl -i http://localhost:5008/admin/vocabulary-books
curl http://localhost:5008/api/vocabulary-books/all
```

预期：健康检查为 200；匿名管理请求为 401；公开词书请求返回含 `Starter English 300` 的成功信封。
