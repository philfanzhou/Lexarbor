# Lexarbor documentation

Lexarbor 是自包含词汇管理与出题服务：后端、Vue 管理页面、静态文件与 Docker 镜像由同一仓库维护，并统一通过 HTTP 5008 端口提供。

## 文档入口

- [安全自包含服务设计](./overview/SecureSelfContainedServiceDesign.md)
- [总览文档](./overview/README.md)
- [ADR-001 管理员认证改为可插拔 Provider](./adr/ADR-001-pluggable-admin-authentication.md)
- [ADR-002 内置词库数据的分层与分发](./adr/ADR-002-bundled-vocabulary-data.md)
- [ADR-003 存储改为仅支持 SQLite](./adr/ADR-003-sqlite-only-storage.md)
- [ADR-004 从 monorepo 提取为 Lexarbor](./adr/ADR-004-standalone-lexarbor-repository.md)
- [数据库事实](./database/README.md)
- [开发文档](./development/README.md)
- [待决事项](./pending-decisions.md)
