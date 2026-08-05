# Vocabulary 数据库

Vocabulary 使用 PostgreSQL 数据库 `ruoyu_study_vocabulary`，EF Core 模型、迁移和 `DatabaseInitializer` 建表 SQL 必须保持一致。

## 核心关系

```text
vocabulary_book (1) <-[RESTRICT]- vocabulary_meaning -[CASCADE]-> (1) vocabulary
```

| 表 | 主责 | 关键约束 |
|----|------|----------|
| `vocabulary` | 单词及音标 | `word` 唯一；新写入使用去首尾空格的小写规范值 |
| `vocabulary_book` | 词书元数据和启用状态 | `status=false` 表示禁用 |
| `vocabulary_meaning` | 单词在指定词书中的词义 | `vocabulary_id`、`book_id` 必填并分别关联单词和词书 |

删除单词时通过 `ON DELETE CASCADE` 清理词义。删除词书使用 `ON DELETE RESTRICT`；存在关联词义时业务层返回 409，管理员应将词书设置为禁用。

## 约束建立方式

`book_id` 的非空约束、指向词书的外键和 `(book_id, vocabulary_id)` 复合索引由迁移 `20260729090000_MeaningBookIntegrity` 建立；表缺失时由 `DatabaseInitializer` 的建表 SQL 直接以强形式建立。

启动时不执行存量数据修复，也不执行完整性诊断。该机制原为 PostgreSQL 存量脏数据设计，服务未上线因而不存在此类存量，相关代码与测试已移除，渐进式约束收紧逻辑仅保留在上述迁移内。存储改为 SQLite 后该迁移将一并重建，见 [ADR-003](../adr/ADR-003-sqlite-only-storage.md)。

公开查询仍必须通过有效且启用的词书关系取数。

## 写入一致性

- 新增词义必须引用存在且启用的词书。
- 更新携带单词、词书或词义 ID 时，对象不存在返回 404，不得转为新增。
- 更新词义时必须确认词义属于当前单词和词书。
- 单词规范值为 `word.Trim().ToLowerInvariant()`。
- 同一单词、词书、规范化词性和去首尾空格释义的重复导入是幂等操作。
- PostgreSQL 写入在同一事务内按上述逻辑键获取事务级 advisory lock，再重新查询并写入，避免多实例并发请求同时插入等价词义；锁键只使用本地 SHA-256 派生的数值，不把词义文本写入日志。
- `Category` 的正式表示为 `vocabulary_book.category` 字符串，不再维护整数分类常量。
