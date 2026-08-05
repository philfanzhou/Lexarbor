# 部署与运维

## 构建与部署

- Dockerfile：`src/Host/Dockerfile`
- 部署脚本：`start.sh`
- 前端构建目录：`frontend/`
- Vite 输出目录：服务根目录的 `wwwroot/`（构建产物，不提交）
- 发布后的静态文件目录：Host 发布内容中的 `wwwroot/`

Dockerfile 先构建 Vue 前端，再发布 .NET 后端，并把前端产物复制到 Host 的 `wwwroot`。运行时仍是一个镜像、一个容器、一个端口。

## 配置项

### 服务端口

| 端口 | 协议 | 用途 |
|------|------|------|
| 5008（容器内固定） | HTTP | 词汇服务唯一端口 |
| host 映射端口 | — | host 访问容器服务的映射端口（`start.sh` 的 `Port` 变量，`-p ${Port}:5008`） |

> **HTTP 监听端口固定为 5008**：硬编码在 `Program.cs` 的 `ConfigureKestrel` 中，不通过 `ASPNETCORE_URLS` 环境变量控制，Dockerfile 也不再设置 `ENV ASPNETCORE_URLS`。host 端口映射通过 `start.sh` 的 `Port` 变量控制。

### Identity 管理员认证

| 配置键 | 登录所需 | 默认值 | 生产来源 |
|--------|----------|--------|----------|
| `IdentityService:Authority` | 是 | `http://localhost:5002` | Consul KV `config/ruoyu/service-endpoints.json` |
| `IdentityService:Issuer` | 是 | `QuantumZhou.Identity` | 配置 |
| `IdentityService:Audience` | 是 | `QuantumZhou.microservices` | 配置 |
| `AdminAuthentication:Provider` | 是 | `QuantumZhou` | `VOCABULARY_ADMIN_AUTH_PROVIDER` |
| `AdminAuthentication:RequiredRole` | 是 | `admin` | 配置 |
| `AdminAuthentication:QuantumZhou:AppId` | 生产是 | 空 | `VOCABULARY_IDENTITY_APP_ID` |
| `AdminAuthentication:QuantumZhou:AppSecret` | 生产是 | 空 | `VOCABULARY_IDENTITY_APP_SECRET` |
| `AdminAuthentication:QuantumZhou:Authority` | 否 | 空（回落到 `IdentityService:Authority`） | 配置 |
| `AdminAuthentication:QuantumZhou:TokenPath` | 否 | `/api/auth/token` | 配置 |
| `AdminAuthentication:CookieName` | 是 | `ruoyuVocabularyAdmin` | 配置 |
| `AdminAuthentication:CookieSecure` | TLS 是 | `false` | `VOCABULARY_COOKIE_SECURE` |

`IdentityService:*` 描述信任哪个签发方，由共享 Consul KV 提供（生产 Authority = `http://ruoyu-identity:5002`），Consul 不可达时回退到 `appsettings.json` 的本地默认值，不通过 `start.sh` 注入。

管理员登录凭据归属于所选 provider，见 [ADR-001](../adr/ADR-001-pluggable-admin-authentication.md)。`start.sh` 将 `VOCABULARY_ADMIN_AUTH_PROVIDER`、`VOCABULARY_IDENTITY_APP_ID`、`VOCABULARY_IDENTITY_APP_SECRET` 和 `VOCABULARY_COOKIE_SECURE` 映射为对应的 .NET 配置键。AppId/AppSecret 只由服务端环境注入，不进入前端、默认配置或日志。

改用标准 OIDC provider 时设置 `VOCABULARY_ADMIN_AUTH_PROVIDER=Oidc`，并配置 `AdminAuthentication:Oidc:{ClientId,ClientSecret,Scope}`；`TokenEndpoint` 留空则从 `IdentityService:Authority` 的 discovery 文档解析。

服务缺少 provider 凭据时仍可启动；生产管理员登录返回 503，直到部署人员为 Vocabulary 注册 Identity 应用并配置凭据。TLS 部署必须设置 `VOCABULARY_COOKIE_SECURE=true`。

### 认证入口

| 路径 | 匿名 | 说明 |
|------|------|------|
| `POST /admin/auth/login` | 是 | 代理 Identity password grant，成功后设置 HttpOnly Cookie |
| `GET /admin/auth/session` | 否 | 要求 `role=admin`，供前端恢复状态 |
| `POST /admin/auth/logout` | 是 | 幂等删除 Cookie |
| `GET /health` | 是 | 服务存活检查 |

除认证入口外的全部 `/admin/*` 要求 `role=admin`。四个既有 `/api/*` 业务接口保持匿名。

### 数据库

- PostgreSQL（Npgsql 提供程序）
- 连接串由 `SharedPostgreSqlConnectionStringFactory.BuildOrFallback` 构建：优先从 Consul 共享配置（`PostgreSql:Host`/`Port`/`Username`/`Password` + `Database:Name`）合成生产连接串，无法合成时回退到本地 `ConnectionStrings:Default`
- `appsettings.json` 配置：
  ```json
  {
    "Database": { "Name": "ruoyu_study_vocabulary" },
    "ConnectionStrings": {
      "Default": "Host=localhost;Port=5432;Database=ruoyu_study_vocabulary;Username=phil"
    }
  }
  ```
- 生产环境的 PostgreSQL 主机/端口/账号/密码由 Consul 的 `PostgreSql:*` 键覆盖，无需写入 `appsettings.json`

启动时执行迁移和缺表修复，不再执行存量数据修复或完整性诊断，详见[数据库事实](../database/README.md)。

## 部署后检查

```bash
curl http://localhost:5008/health
curl -i http://localhost:5008/admin/vocabulary-books
curl http://localhost:5008/api/vocabulary-books/all
```

预期：健康检查为 200；匿名管理请求为 401；公开词书请求不因管理员认证返回 401 或 403。
