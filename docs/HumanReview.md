# HumanReview — ruoyu.vocabulary

> 代码审查发现项，按优先级排列。已解决项必须移除。

## P0 — 必须修复

| ID | 问题 | 方案 A | 方案 B | 方案 C | 批复 |
|----|------|--------|--------|--------|------|
| HR-01 | GetWordsAsync 返回空列表，逻辑未实现（VocabularyServiceImpl.cs） | (推荐) 实现完整查询逻辑：分页 + 过滤 + 排序 | 先返回 mock 数据，后续迭代实现 | 暂不处理，等需求明确 |
| HR-02 | UnitOfWork PostgreSQL 特定异常处理在 SQLite 下失效（UnitOfWork.cs） | (推荐) 使用异常检测抽象层，根据 DB 类型匹配对应异常码 | 统一使用通用异常处理，不区分 DB 类型 | 暂不处理，生产环境固定 PostgreSQL |
| HR-03 | gRPC 服务层吞没异常细节，统一包装为 Internal（VocabularyServiceImpl.cs） | (推荐) 区分异常类型：验证错误→InvalidArgument，未找到→NotFound，其他→Internal | 添加结构化错误码到 gRPC Status Details | 暂不处理，当前错误信息足够 |

## P1 — 应尽快修复

| ID | 问题 | 方案 A | 方案 B | 方案 C | 批复 |
|----|------|--------|--------|--------|------|
| HR-04 | 中文异常消息 | (推荐) 统一改为英文，中文仅保留用户可见的 DisplayNames | 保持中文，在规范中允许 gRPC 错误消息使用中文 | 暂不处理，不影响功能 |
| HR-05 | 单文件包含多个类型 | (推荐) 每个类型拆分到独立文件，遵循 C# 约定 | 按功能分组，每组一个文件 | 暂不处理，不影响功能 |
| HR-06 | GetRandomExceptAsync 使用 OrderBy(Guid.NewGuid())，全表排序性能差（VocabularyRepository.cs） | (推荐) 改用数据库原生 RANDOM() / random() 函数排序 | 预计算随机池，从池中排除后取随机 | 暂不处理，当前数据量小 |
| HR-07 | 依赖具体类而非接口 | (推荐) 提取接口，DI 注册时绑定接口到实现 | 仅在需要 Mock 测试的类提取接口 | 暂不处理，当前耦合度可接受 |
