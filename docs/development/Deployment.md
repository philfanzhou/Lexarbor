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

- PostgreSQL（生产）或 SQLite（本地开发）
- PostgreSQL 连接字符串：`Host=ruoyu-postgres;Port=5432;Database=ruoyu_study_vocabulary;Username=postgres;Password=postgres`
- SQLite 数据文件：`data/sqlite/ruoyu_study_vocabulary.db`
