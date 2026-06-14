# HumanReview — ruoyu.vocabulary

> 代码审查发现项，按优先级排列。已解决项必须移除。

## P0 — 必须修复

| ID | 问题 | 位置 | 状态 | 批复 |
|----|------|------|------|------|
| HR-01 | GetWordsAsync 返回空列表，逻辑未实现 | VocabularyServiceImpl.cs | 待修复 | |
| HR-02 | UnitOfWork PostgreSQL 特定异常处理在 SQLite 下失效 | UnitOfWork.cs | 待修复 | |
| HR-03 | gRPC 服务层吞没异常细节，统一包装为 Internal | VocabularyServiceImpl.cs | 待修复 | |

## P1 — 应尽快修复

| ID | 问题 | 位置 | 状态 | 批复 |
|----|------|------|------|------|
| HR-04 | 中文异常消息 | 多处 | 待修复 | |
| HR-05 | 单文件包含多个类型 | 多处 | 待修复 | |
| HR-06 | GetRandomExceptAsync 使用 OrderBy(Guid.NewGuid()) | VocabularyRepository.cs | 待修复 | |
| HR-07 | 依赖具体类而非接口 | 多处 | 待修复 | |
