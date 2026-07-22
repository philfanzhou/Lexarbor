# 部署与运维

## 构建与部署

- Dockerfile：`src/Host/Dockerfile`
- 部署脚本：`scripts/7.vocabulary/2.deploy/start.sh`

## 配置项

### 服务端口

| 端口 | 协议 | 用途 |
|------|------|------|
| 5008（容器内固定） | HTTP | 词汇服务唯一端口 |
| host 映射端口 | — | host 访问容器服务的映射端口（`start.sh` 的 `Port` 变量，`-p ${Port}:5008`） |

> **HTTP 监听端口固定为 5008**：硬编码在 `Program.cs` 的 `ConfigureKestrel` 中，不通过 `ASPNETCORE_URLS` 环境变量控制，Dockerfile 也不再设置 `ENV ASPNETCORE_URLS`。host 端口映射通过 `start.sh` 的 `Port` 变量控制。

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
