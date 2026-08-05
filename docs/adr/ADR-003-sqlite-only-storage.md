# ADR-003 存储改为仅支持 SQLite

- **状态**：已接受，已实施
- **日期**：2026-08-05
- **范围**：Vocabulary 的数据库提供程序、迁移、并发写入策略与音标字段；路径不变，单词 DTO 的音标字段发生契约变化

## 背景

实施前服务使用 PostgreSQL。连接串由 `旧 monorepo 的共享 Consul 组件` 的 `SharedPostgreSqlConnectionStringFactory` 从 Consul 共享配置合成，建表修复则依赖 `旧 monorepo 的共享公共组件` 的数据库助手。这套依赖对平台内部部署合适，但对将服务作为公开仓库分发是负担：使用者必须先准备 PostgreSQL 实例并理解共享配置约定，才能启动。

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

- 连接串退化为本地文件路径，不再依赖 Consul、Common 或共享连接串工厂。
- 删除 `IsRelational()` 分流，全部代码路径使用同一提供程序；测试与生产运行相同的数据库实现。
- 重新生成迁移。服务未上线，不保留既有 PostgreSQL 迁移历史。

### 并发写入

`pg_advisory_xact_lock` 用于避免多实例并发插入等价词义。SQLite 部署限定为单实例，应用使用进程级 `SemaphoreSlim` 串行化写事务，使第二个等价请求在第一个提交后重新查询并复用记录。数据库同时用两个 stored generated columns 保存规范化词性和释义，并在完整逻辑键上建立唯一索引作为最后防线。

### 历史数据兼容代码一并移除

`NOT VALID` 渐进式约束收紧是为 PostgreSQL 存量脏数据设计的。全新 SQLite 库不存在该类存量，旧迁移、诊断和修复代码均已移除，约束直接以强形式建立。

### 音标拆分为英美两列

`vocabulary.phonetic` 单列拆分为 `phonetic_uk` 与 `phonetic_us`，均可空。对应 JSON 字段为 `phoneticUk` 与 `phoneticUs`，管理导入页面分别采集两项。英美双音标是面向中文学习者的常规配置。该变更与提供程序更换落在同一次迁移重建中，无额外迁移成本。

外部词典若只提供单一音标，落到哪一列由导入路径显式定义，不得默认写入其中一列。该约束记入 ADR-002 第二层的选型考量。

## 备选方案

- **SQLite 默认 + PostgreSQL 可选**：被否决。日常在 PostgreSQL 上开发、向使用者分发 SQLite，等于测试路径与分发路径不同。ADR-001 记录的角色 claim 缺陷正是此形态：测试替身实现了真实实现并不实现的契约，缺陷长期不可见。双提供程序还会使迁移、查询与冲突语义长期需要双份维护，并使公开仓库继续携带 `旧 monorepo 的共享公共组件` 与 Consul 假设。
- **保持 PostgreSQL**：被否决。使用者启动前必须自备数据库实例，与公开分发目标冲突。

## 影响

- **失去多实例横向扩展**。SQLite 单写者，部署必须单实例并挂载持久卷。以本服务读多写少、数据可由启动词书或导入重建的性质，判断为可接受。
- **备份方式改变**，由平台共享 PostgreSQL 方案变为文件级备份。
- 代码和 Dockerfile 已不再引用 `旧 monorepo 的共享公共组件` 或 `旧 monorepo 的共享 Consul 组件`。Identity 地址通过普通配置和环境变量提供。
- ADR-002 第二层可简化：词典是只读参考数据，可作为预构建的 SQLite 文件以只读方式附加，不必再设计批量导入路径。具体形态在实施第二层时设计，EF Core 不原生支持跨库查询。该做法不与 ADR-002 第一层"不预置数据库文件"冲突——受约束的是服务自身可写的那个数据库文件，它必须在运行时于配置路径上创建，以便落在宿主挂载卷上。
- 公开与管理接口路径不变；涉及单词的请求和响应以 `phoneticUk`、`phoneticUs` 取代旧 `phonetic`，这是本次有意的公共契约变更。
- Domain 与 HTTP 测试均运行真实 SQLite；首次建库、种子数量、已有文件跳过种子和并发幂等均有自动化覆盖。
