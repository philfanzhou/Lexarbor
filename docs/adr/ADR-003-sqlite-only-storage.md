# ADR-003 存储改为仅支持 SQLite

- **状态**：已接受，尚未实施
- **日期**：2026-08-05
- **范围**：Vocabulary 的数据库提供程序、迁移、并发写入策略与音标字段；不改变公开 `/api/*` 与 `/admin/*` 的路径与响应结构

## 背景

服务当前使用 PostgreSQL，连接串由 `Ruoyu.Study.Common` 的 `SharedPostgreSqlConnectionStringFactory` 从 Consul 共享配置合成（`Host/Program.cs`）。这套依赖对平台内部部署合适，但对将服务作为公开仓库分发是负担：使用者必须先准备 PostgreSQL 实例并理解一套与自己无关的共享配置约定，才能启动。

服务尚未上线（见 ADR-001），没有存量数据需要迁移，因此更换提供程序的成本目前处于最低点。

代码中已存在按 `_context.Database.IsRelational()` 分流的双路径（`Database/Repositories/Repositories.cs`），但该分支的非关系分支服务于测试用的内存提供程序，**SQLite 同样满足 `IsRelational()`**，会落入 PostgreSQL 分支。以下位置为 PostgreSQL 专属：

| 位置 | 专属特性 |
|------|----------|
| `Repositories.cs` 干扰项查询 | `DISTINCT ON`、`btrim` |
| `Repositories.cs` `AcquireEquivalentMeaningLockAsync` | `pg_advisory_xact_lock` |
| `DatabaseInitializer.cs` | `CHECK` / `FOREIGN KEY ... NOT VALID` |
| `UnitOfWork.cs` | `PostgresException` 与 `PostgresErrorCodes` 异常映射 |

## 决定

仅支持 SQLite，移除 PostgreSQL 提供程序，不保留双提供程序能力。

- 连接串退化为本地文件路径，不再依赖 Consul 与共享连接串工厂。
- 删除 `IsRelational()` 分流，全部代码路径使用同一提供程序；测试与生产运行相同的数据库实现。
- 重新生成迁移。服务未上线，不保留既有 PostgreSQL 迁移历史。

### 并发写入

`pg_advisory_xact_lock` 用于避免多实例并发插入等价词义。SQLite 串行化写入，该机制不再需要，改由逻辑键唯一索引配合冲突时忽略实现幂等。

### 历史数据兼容代码一并移除

`NOT VALID` 渐进式约束收紧是为 PostgreSQL 存量脏数据设计的。全新 SQLite 库不存在该类存量，约束直接以强形式建立。

### 音标拆分为英美两列

`vocabulary.phonetic` 单列拆分为 `phonetic_uk` 与 `phonetic_us`，均可空。英美双音标是面向中文学习者的常规配置。该变更与提供程序更换落在同一次迁移重建中，无额外迁移成本。

外部词典若只提供单一音标，落到哪一列由导入路径显式定义，不得默认写入其中一列。该约束记入 ADR-002 第二层的选型考量。

## 备选方案

- **SQLite 默认 + PostgreSQL 可选**：被否决。日常在 PostgreSQL 上开发、向使用者分发 SQLite，等于测试路径与分发路径不同。ADR-001 记录的角色 claim 缺陷正是此形态：测试替身实现了真实实现并不实现的契约，缺陷长期不可见。双提供程序还会使迁移、查询与冲突语义长期需要双份维护，并使公开仓库继续携带 `Ruoyu.Study.Common` 与 Consul 假设。
- **保持 PostgreSQL**：被否决。使用者启动前必须自备数据库实例，与公开分发目标冲突。

## 影响

- **失去多实例横向扩展**。SQLite 单写者，部署必须单实例并挂载持久卷。以本服务读多写少、数据可重新导入的性质，判断为可接受。
- **备份方式改变**，由平台共享 PostgreSQL 方案变为文件级备份。
- 默认路径不再依赖 `Ruoyu.Study.Common`；`Database.csproj` 的项目引用、`Program.cs` 的连接串工厂调用和 `DatabaseInitializer.cs` 的共享建表助手需一并处理。
- ADR-002 第二层可简化：词典可作为预构建的 SQLite 文件附带或附加，不必再设计批量导入路径。具体形态在实施第二层时设计，EF Core 不原生支持跨库查询。
- 公开与管理接口的路径、请求字段和响应结构不变。
