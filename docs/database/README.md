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

## 历史数据兼容

旧表允许 `book_id` 为空且没有词书外键。升级时：

1. 添加 `book_id IS NOT NULL` 的 `CHECK ... NOT VALID` 约束。
2. 添加词书外键 `ON DELETE RESTRICT NOT VALID`。
3. 新写入立即受约束，历史异常行不会被自动删除或改写。
4. 历史数据干净时自动验证约束并设置列级 `NOT NULL`。
5. 存在空 BookId 或孤儿 BookId 时继续启动，仅记录异常数量；公开查询必须通过有效且启用的词书关系取数。

服务启动诊断只记录以下计数，不记录具体词义内容：

- 空 BookId；
- 孤儿 BookId；
- 规范化后重复单词组；
- 同一单词、词书、规范化词性和释义的重复词义组。

## 写入一致性

- 新增词义必须引用存在且启用的词书。
- 更新携带单词、词书或词义 ID 时，对象不存在返回 404，不得转为新增。
- 更新词义时必须确认词义属于当前单词和词书。
- 单词规范值为 `word.Trim().ToLowerInvariant()`。
- 同一单词、词书、规范化词性和去首尾空格释义的重复导入是幂等操作。
- `Category` 的正式表示为 `vocabulary_book.category` 字符串，不再维护整数分类常量。
