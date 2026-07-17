# 部署与运维

## 构建与部署

- Dockerfile：`src/Host/Dockerfile`
- 部署脚本：`scripts/7.vocabulary/2.deploy/start.sh`

## 配置项

### 服务端口

| 端口 | 协议 | 用途 |
|------|------|------|
| 5008 | HTTP | 词汇服务（ASPNETCORE_URLS 默认） |

### 数据库

- PostgreSQL（生产）或 SQLite（本地开发回退）
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
- 连接串包含 `Host=`/`Server=` 时走 PostgreSQL（`UseNpgsql`），否则走 SQLite（`Data Source=data/sqlite/ruoyu_study_vocabulary.db`）
- 生产环境的 PostgreSQL 主机/端口/账号/密码由 Consul 的 `PostgreSql:*` 键覆盖，无需写入 `appsettings.json`
