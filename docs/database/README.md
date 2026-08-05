# Vocabulary 数据库

Vocabulary 仅支持 SQLite。默认数据库文件为 `data/vocabulary.db`，EF Core 模型与单一 `InitialCreate` 迁移描述完整结构；服务未上线，不保留旧 PostgreSQL 迁移链。

## 核心关系

```text
vocabulary_book (1) <-[RESTRICT]- vocabulary_meaning -[CASCADE]-> (1) vocabulary
```

| 表 | 主责 | 关键约束 |
|----|------|----------|
| `vocabulary` | 单词及英美音标 | `word` 唯一；`phonetic_uk`、`phonetic_us` 可空 |
| `vocabulary_book` | 词书元数据和启用状态 | `status=false` 表示禁用 |
| `vocabulary_meaning` | 单词在指定词书中的词义 | 双外键必填；规范化逻辑键唯一 |

删除单词时通过 `ON DELETE CASCADE` 清理词义。删除词书使用 `ON DELETE RESTRICT`；存在关联词义时业务层返回 409，管理员应将词书设置为禁用。

等价词义的数据库逻辑键为：

```text
(vocabulary_id, book_id, lower(trim(coalesce(part_of_speech, ''))), trim(meaning))
```

后两项由 SQLite stored generated columns 保存并建立唯一索引，防止绕过应用层产生重复数据。

## 首次建库与启动词书

连接串默认是 `Data Source=data/vocabulary.db`。启动顺序固定为：

1. 在迁移前判断配置的数据文件是否存在；
2. 创建父目录并执行 SQLite 迁移；
3. 仅当文件原先不存在时，读取程序集内嵌的 `SeedData/starter-vocabulary.tsv`；
4. 在一个事务中写入 `Starter English 300`、300 个唯一单词及其词义。

每个启动词条都包含英式音标、美式音标、词性和中文释义。已有文件只执行迁移，绝不重新加载种子，因此使用者后续添加的数据不会被启动逻辑覆盖或重复。

数据库文件必须位于持久卷。Docker 默认挂载宿主 `data/` 到 `/app/data`；不得把预构建 `.db` 放进镜像。

## 写入一致性

- 新增词义必须引用存在且启用的词书。
- 更新携带单词、词书或词义 ID 时，对象不存在返回 404，不得转为新增。
- 更新词义时必须确认词义属于当前单词和词书。
- 单词规范值为 `word.Trim().ToLowerInvariant()`。
- 同一单词、词书、规范化词性和释义的重复导入是幂等操作。
- SQLite 部署限定单实例；进程级写事务锁串行化管理写入，数据库唯一索引是最后防线。
- SQLite 约束错误由 `UnitOfWork` 映射为 409，不向客户端暴露内部错误。
