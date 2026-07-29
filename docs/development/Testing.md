# 单元测试规范

本文件描述 `ruoyu.vocabulary` 服务的单元测试项目结构、覆盖范围与约定。

## 测试项目结构

| 项目 | 路径 | 覆盖范围 |
|------|------|---------|
| `Ruoyu.Study.Vocabulary.Domain.Tests` | `src/Tests/Ruoyu.Study.Vocabulary.Domain.Tests/` | Domain 层服务 |
| `Ruoyu.Study.Vocabulary.Service.Tests` | `src/Tests/Ruoyu.Study.Vocabulary.Service.Tests/` | DTO 转换、异常中间件、认证和 HTTP 集成 |

## 测试框架与依赖

- xUnit 2.6.2
- Moq 4.20.70
- FluentAssertions 6.12.0
- Microsoft.AspNetCore.Mvc.Testing 8.0.0（WebApplicationFactory 集成测试）
- Microsoft.EntityFrameworkCore.InMemory 8.0.11（隔离 HTTP 测试数据库）
- Mapster 10.0.7（Convertor 依赖）
- coverlet.collector 6.0.0

## Service.Tests 覆盖范围

### Convertor 测试场景（Mapster DTO ↔ Model）

| 场景 | 期望 |
|------|------|
| VocabularyDto → VocabularyModel | 字段正确映射 |
| VocabularyModel → VocabularyDto | 字段正确映射 |
| VocabularyMeaningDto → VocabularyMeaningModel | 字段正确映射 |
| VocabularyMeaningModel → VocabularyMeaningDto | 字段正确映射 |
| VocabularyBookDto → VocabularyBookModel | 字段正确映射 |
| VocabularyBookModel → VocabularyBookDto | 字段正确映射 |

## Domain.Tests 覆盖范围

### VocabularyDomainService

| 场景 | 期望 |
|------|------|
| GetDetailAsync 成功 | 返回 (word, meanings) |
| SearchAsync 分页 | 返回正确分页结果 |
| 单词规范化 | 去首尾空格并统一小写 |
| 重复导入 | 复用单词和等价词义 |
| 词书不存在或禁用 | 分别返回 NotFound 或业务条件错误 |
| 更新不存在 ID | 返回 NotFound，不创建新对象 |
| 词义归属不匹配 | 返回 Conflict |
| 题目生成 | 干扰项只来自同一词书，按单词去重 |
| 题目候选不足 | 返回 BusinessRuleException，由 HTTP 映射为 422 |

### VocabularyBookDomainService

| 场景 | 期望 |
|------|------|
| GetAllAsync | 公共列表只返回启用书籍 |
| GetByCategoryAsync 带 grade | 过滤正确 |
| SearchAsync 分页 | 管理列表包含禁用书籍且查询在数据库侧完成 |
| AddOrUpdateAsync | 新增正确；更新不存在 ID 返回 NotFound |
| DeleteAsync | 空词书可删除；已使用词书返回 Conflict |
| GetAllCategoriesAsync | 返回去重分类列表 |
| GetAllEducationLevelsAsync | 返回去重教育阶段列表 |
| GetAllGradesAsync | 返回去重年级列表 |
| GetGradesByEducationLevelAsync | 按阶段过滤年级 |
| GetWordsAsync | 根据 bookId 返回去重后的词汇列表，并按单词排序 |

### 数据库模型和迁移

| 场景 | 期望 |
|------|------|
| 词义到单词关系 | 必填外键，删除单词级联 |
| 词义到词书关系 | 必填外键，删除词书 Restrict |
| 历史异常数据 | `NOT VALID` 约束保护新写入，启动记录计数但不删除 |
| 干净数据库 | 自动验证约束并设置 `book_id NOT NULL` |

### HTTP 认证和信封

| 场景 | 期望 |
|------|------|
| 匿名管理请求 | 401 信封 |
| 普通用户 JWT | 403 信封 |
| 管理员 fake Identity 登录 | 设置 HttpOnly Cookie |
| 错误凭据 | 401，不设置 Cookie |
| 管理员 Cookie/Bearer | 可访问管理端点 |
| 登出 | 响应过期 Cookie，后续管理请求 401 |
| Identity 不可达/配置缺失 | 502/503 |
| Cookie 管理写请求缺少同源头 | 403 |
| 公开 `/api/*` | 不要求管理员登录 |
| 未知路由 | `/api/*` 为 404；匿名 `/admin/*` 为 401；管理员 `/admin/*` 为 404 |
| 未预期异常 | 500 通用消息，不泄露内部异常 |

## 运行方式

```bash
cd src/services/ruoyu.vocabulary/src
dotnet test Ruoyu.Study.Vocabulary.sln --configuration Release
```

```bash
cd src/services/ruoyu.vocabulary/frontend
npm run test:types
npm run build
```

## 约定

- Convertor 测试采用直接调用 Mapster `Adapt<>` 方法验证字段映射正确性。
- Domain 层测试采用「真实 DomainService + Mock 仓储接口」方式。
- Convertor 测试需要 `InternalsVisibleTo`，已在 Service 项目 .csproj 中配置。
- 断言使用 FluentAssertions。
- 不修改被测代码以适配测试；如需可测试性改进，先更新本文档再改代码。
- Identity 使用完整契约的 fake HTTP handler；JWT 使用测试签名密钥，不依赖真实管理员密码。
- 真实 Identity 凭据不可用时，不得把 fake Identity 结果表述为真实联调成功。
