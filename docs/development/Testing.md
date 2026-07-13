# 单元测试规范

本文件描述 `ruoyu.vocabulary` 服务的单元测试项目结构、覆盖范围与约定。

## 测试项目结构

| 项目 | 路径 | 覆盖范围 |
|------|------|---------|
| `Ruoyu.Study.Vocabulary.Domain.Tests` | `src/Tests/Ruoyu.Study.Vocabulary.Domain.Tests/` | Domain 层服务 |
| `Ruoyu.Study.Vocabulary.Service.Tests` | `src/Tests/Ruoyu.Study.Vocabulary.Service.Tests/` | Service 层 Convertor (Mapster DTO↔Model) |

## 测试框架与依赖

- xUnit 2.6.2
- Moq 4.20.70
- FluentAssertions 6.12.0
- Microsoft.AspNetCore.Mvc.Testing 8.0.0（WebApplicationFactory 集成测试）
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
| AddOrUpdateAsync 新增 | 生成新 Id，调用 AddAsync |
| AddOrUpdateAsync 更新 | 查找已有实体，调用 UpdateAsync |
| GetDistractorWordsAsync | 返回随机干扰项 |
| GetDistractorMeaningsAsync | 返回随机干扰项 |

### VocabularyBookDomainService

| 场景 | 期望 |
|------|------|
| GetAllAsync | 返回所有书籍 |
| GetByCategoryAsync 带 grade | 过滤正确 |
| SearchAsync 分页 | 返回正确分页结果 |
| AddOrUpdateAsync | 新增/更新正确 |
| DeleteAsync | 删除正确 |
| GetAllCategoriesAsync | 返回去重分类列表 |
| GetAllEducationLevelsAsync | 返回去重教育阶段列表 |
| GetAllGradesAsync | 返回去重年级列表 |
| GetGradesByEducationLevelAsync | 按阶段过滤年级 |
| GetWordsAsync | 返回词汇列表（HR-01：已知返回空列表） |

## 运行方式

```bash
cd src/services/ruoyu.vocabulary/src
dotnet test Ruoyu.Study.Vocabulary.sln --configuration Release
```

## 约定

- Convertor 测试采用直接调用 Mapster `Adapt<>` 方法验证字段映射正确性。
- Domain 层测试采用「真实 DomainService + Mock 仓储接口」方式。
- Convertor 测试需要 `InternalsVisibleTo`，已在 Service 项目 .csproj 中配置。
- 断言使用 FluentAssertions。
- 不修改被测代码以适配测试；如需可测试性改进，先更新本文档再改代码。
