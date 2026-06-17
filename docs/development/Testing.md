# 单元测试规范

本文件描述 `ruoyu.vocabulary` 服务的单元测试项目结构、覆盖范围与约定。

## 测试项目结构

| 项目 | 路径 | 覆盖范围 |
|------|------|---------|
| `Ruoyu.Study.Vocabulary.Domain.Tests` | `src/Tests/Ruoyu.Study.Vocabulary.Domain.Tests/` | Domain 层服务 |
| `Ruoyu.Study.Vocabulary.Service.Tests` | `src/Tests/Ruoyu.Study.Vocabulary.Service.Tests/` | Service 层 ServiceImpl + Convertor |

## 测试框架与依赖

- xUnit 2.6.2
- Moq 4.20.70
- FluentAssertions 6.12.0
- Grpc.AspNetCore 2.62.0（提供 `ServerCallContext`）
- Mapster 10.0.7（Convertor 依赖）
- coverlet.collector 6.0.0

## Service.Tests 覆盖范围

### 测试辅助类

- `TestServerCallContextImpl`：继承 `Grpc.Core.ServerCallContext`，提供 gRPC 2.62.0 兼容的最小测试上下文实现。

### VocabularyServiceImpl 测试场景（4 个 RPC 方法）

#### Get

| 场景 | 期望 |
|------|------|
| WordId 为空 | `RpcException(InvalidArgument)` |
| BookId 为空 | `RpcException(InvalidArgument)` |
| 成功获取 | 返回 `VocabularyDto`，含 meanings |
| Domain 抛异常 | `RpcException(Internal)` |

> **注意**：测试数据中 `VocabularyModel.Phonetic` 等 nullable 字段必须设为非 null 值，否则 Mapster `Adapt<VocabularyDto>()` 会因 protobuf string setter 拒绝 null 而抛 `ArgumentNullException`。

#### Search

| 场景 | 期望 |
|------|------|
| Keyword 为空 | `RpcException(InvalidArgument)` |
| Page/Size 默认值 | 使用 1/20 |
| 成功搜索 | 返回分页结果，TotalPage 正确 |
| Domain 抛异常 | `RpcException(Internal)` |

#### AddOrUpdate

| 场景 | 期望 |
|------|------|
| Word 为 null | `RpcException(InvalidArgument)` |
| Meaning 为 null | `RpcException(InvalidArgument)` |
| 成功添加 | `Success=true` |
| Domain 抛异常 | `RpcException(Internal)` |

#### GetQuestion

| 场景 | 期望 |
|------|------|
| WordId/BookId 为空 | `RpcException(InvalidArgument)` |
| 无 meanings | `RpcException(Internal)`（HR-03：NotFound 被 try-catch 吞为 Internal） |
| ChineseToEnglish=true | 中文题，选项为英文单词 |
| ChineseToEnglish=false | 随机方向（需同时 mock 两种 distractor） |
| Domain 抛异常 | `RpcException(Internal)` |

### VocabularyBookServiceImpl 测试场景（11 个 RPC 方法）

#### Add / Update / Delete

| 场景 | 期望 |
|------|------|
| Add: BookName 为空 | `RpcException(InvalidArgument)` |
| Add: 成功 | `Success=true` |
| Update: Id 为空 | `RpcException(InvalidArgument)` |
| Update: 成功 | `Success=true` |
| Delete: Id 为空 | `RpcException(InvalidArgument)` |
| Delete: 成功 | `Success=true` |

#### Get / Search / GetByCategory / GetAll

| 场景 | 期望 |
|------|------|
| Get: Id 为空 | `RpcException(InvalidArgument)` |
| Get: 不存在 | `RpcException(Internal)`（HR-03：NotFound 被 try-catch 吞为 Internal） |
| Get: 成功 | 返回 `VocabularyBookDto` |
| Search: 默认分页 | 使用 1/20 |
| Search: 成功 | 返回分页结果 |
| GetByCategory: 带 grade | 过滤正确 |
| GetAll: 成功 | 返回列表 |

> **注意**：测试数据中 `VocabularyBookModel` 的 nullable 字段（`Description`、`Publisher`、`EducationLevel`、`Grade`、`Category`、`IconUrl`）必须设为非 null 值，否则 Mapster 映射到 protobuf DTO 时会抛 `ArgumentNullException`。

#### GetBookWords

| 场景 | 期望 |
|------|------|
| Id 为空 | `RpcException(InvalidArgument)` |
| 成功 | 返回 `VocabularyDtoList`（当前实现返回空列表） |

#### GetAllCategories / GetAllEducationLevels / GetAllGrades / GetGradesByEducationLevel

| 场景 | 期望 |
|------|------|
| 成功 | 返回 `StringList` |

### Convertor 测试场景

| 场景 | 期望 |
|------|------|
| VocabularyDto → VocabularyModel | 字段正确映射 |
| VocabularyModel → VocabularyDto | 字段正确映射 |
| VocabularyMeaningDto → VocabularyMeaningModel | 字段正确映射 |
| VocabularyMeaningModel → VocabularyMeaningDto | 字段正确映射 |
| VocabularyBookDto → VocabularyBookModel | 字段正确映射 |
| VocabularyBookModel → VocabularyBookDto | 字段正确映射 |

## 运行方式

```bash
cd src/services/ruoyu.vocabulary/src
dotnet test Ruoyu.Study.Grpc.Vocabulary.sln --configuration Release
```

## 约定

- ServiceImpl 测试采用「真实 DomainService + Mock 仓储接口」方式。
- Convertor 测试需要 `InternalsVisibleTo`，已在 Service 项目 .csproj 中配置。
- 断言使用 FluentAssertions。
- 不修改被测代码以适配测试；如需可测试性改进，先更新本文档再改代码。
