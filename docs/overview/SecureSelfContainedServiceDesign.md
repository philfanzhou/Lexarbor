# Vocabulary 安全自包含服务设计

## 1. 背景

Vocabulary 服务位于 `src/services/ruoyu.vocabulary`，使用 .NET 8、ASP.NET Core Minimal API、EF Core 8、SQLite、Vue 3、TypeScript、Element Plus 和 Vite。后端托管前端静态文件，Docker 镜像在构建时同时产出前后端。

本设计实施前存在以下问题，现均已按后续章节处理：

- `/admin/*` 没有认证或角色校验。
- 前端没有登录状态，匿名访问者可以直接看到并调用管理功能。
- 词义的 `BookId` 可空且没有词书外键。
- 更新不存在的单词、词义或词书可能转为新增。
- 单词规范化和重复词义导入没有完整约束。
- 禁用词书仍可能出现在公开列表，删除策略没有保护已使用词书。
- 中译英干扰单词来自全词库，题目可能跨词书污染或出现重复选项。
- 多个端点直接把 `Exception.Message` 返回客户端。
- 词书搜索、筛选和分页会先加载全表。
- 仓库级 `PROJECT.md` 曾记录旧端口，与实际使用的 5008 不一致。
- Vocabulary 缺少完整的正式文档入口和 `frontend/docs/frontend-spec.md`。

本设计保持既有四个 `/api/*` 业务接口的路径和业务行为，并完善认证、数据边界、错误处理、查询性能、前端登录和验证体系；后续 ADR-003 另行批准了单音标到英美双音标的字段变更。

## 2. 设计目标

1. Vocabulary 继续以单服务、单镜像、单端口方式自包含部署。
2. 管理页面通过 Vocabulary 后端代理 Identity 管理员登录。
3. 只有 Identity JWT 中包含 `role=admin` 的用户可以访问管理接口。
4. 前端不接触 Identity 地址、AppSecret、access token 或 refresh token。
5. 保持既有 `/api/*` 路径和业务语义；音标字段按 ADR-003 变更为英美双字段。
6. 为单词、词义和词书建立可靠的 SQLite 约束和首次建库流程。
7. 所有 HTTP 结果使用统一 `VocabularyHttpResponse` 信封。
8. 筛选、计数、分页和随机候选尽量由 SQLite 完成。

## 3. 非目标

- 不修改 Identity 的管理员引导账户或 `role=admin` 注入逻辑。
- 不把 Vocabulary 管理页面迁移到 Admin Portal。
- 不为公开 `/api/*` 复用管理员 Cookie 作为服务间认证。
- 不引入前端状态管理库或新的 UI 框架。
- 不实现 refresh token 自动续期；管理员 JWT 过期后重新登录。
- 不提供 PostgreSQL 到 SQLite 的存量数据迁移；服务未上线，无存量数据库。

## 4. 总体架构

Vocabulary 继续监听容器端口 5008，并在同一 ASP.NET Core Host 内提供：

| 能力 | 路径 | 访问边界 |
|------|------|----------|
| Vue 静态文件与 SPA | `/`、静态资源、前端 hash 路由 | 匿名 |
| 健康检查 | `GET /health` | 匿名 |
| 管理登录 | `POST /admin/auth/login` | 匿名 |
| 登录状态 | `GET /admin/auth/session` | `role=admin` |
| 管理登出 | `POST /admin/auth/logout` | 匿名，可重复调用 |
| 公开业务 API | 既有 `/api/*` | 匿名，保持兼容 |
| 管理 API | 除认证入口外的既有 `/admin/*` | `role=admin` |

未知 `/api/*` 或 `/admin/*` 路径返回统一信封的 404，不进入 SPA fallback。SPA fallback 保持匿名可访问，避免出现未登录时无法加载登录页的死锁。

## 5. Identity 管理员认证

### 5.1 配置

Vocabulary 后端使用以下配置：

```json
{
  "IdentityService": {
    "Authority": "http://localhost:5002",
    "Issuer": "QuantumZhou.Identity",
    "Audience": "QuantumZhou.microservices"
  },
  "AdminAuthentication": {
    "CookieName": "ruoyuVocabularyAdmin",
    "CookieSecure": false,
    "Provider": "QuantumZhou",
    "RequiredRole": "admin",
    "QuantumZhou": {
      "TokenPath": "/api/auth/token"
    }
  }
}
```

管理员登录通过 `IAdminCredentialAuthenticator` 抽象，由 `AdminAuthentication:Provider` 选择实现，详见 [ADR-001](../adr/ADR-001-pluggable-admin-authentication.md)：

- `QuantumZhou`（默认）：Identity 私有的 `POST /api/auth/token` 契约，凭据取自 `AdminAuthentication:QuantumZhou:{AppId,AppSecret}`。
- `Oidc`：标准 OAuth2 password grant，配置在 `AdminAuthentication:Oidc:{TokenEndpoint,ClientId,ClientSecret,Scope}`；`TokenEndpoint` 留空时从 discovery 文档解析。

配置约定：

- `IdentityService:*` 描述信任哪个签发方，由 appsettings、标准 .NET 环境变量或其他标准配置 Provider 提供；容器部署通过 `VOCABULARY_IDENTITY_AUTHORITY` 映射 Authority。
- provider 凭据只从环境变量注入，不写入前端或仓库默认配置。
- `AdminAuthentication:QuantumZhou:Authority` 可选；留空时回落到 `IdentityService:Authority`，用于登录端点与 JWKS 端点不同源的部署。
- 服务在缺少 AppId/AppSecret 时仍可启动；生产登录请求返回统一信封的 503，并记录不含密钥的配置错误。
- `Issuer`、`Audience`、签名、公钥轮换和过期时间由 JWT Bearer 校验。
- JWT 关闭入站 claim 映射。角色 claim 同时接受短名 `role` 和完整的 `ClaimTypes.Role` URI：QuantumZhou.Identity 直接构造 `JwtPayload`，绕过 outbound claim 类型映射，因此签发的是 URI 形态；标准 OIDC provider 签发短名。管理员策略要求 `AdminAuthentication:RequiredRole`（默认 `admin`），由 `AdminRoleHandler` 在评估时读取。
- 本地 HTTP 开发环境允许 `CookieSecure=false`；TLS 部署必须配置为 `true`。

### 5.2 登录流程

1. 浏览器向 `POST /admin/auth/login` 提交 `{ username, password }`。
2. Vocabulary 校验必填字段，不记录请求体、密码或用户名密码组合。
3. Vocabulary 后端调用 `${IdentityService:Authority}/api/auth/token`。
4. 请求 JSON 使用：

   ```json
   {
     "grantType": "password",
     "username": "<submitted username>",
     "password": "<submitted password>"
   }
   ```

5. Vocabulary 后端在请求头加入服务端配置的 `X-Admin-AppId` 和 `X-Admin-AppSecret`。
6. Identity 返回 `success=false` 时，Vocabulary 返回 401 和通用的凭据错误，不透传 Identity 内部消息。
7. Identity 返回成功后，Vocabulary 使用与请求认证相同的 Issuer、Audience、签名密钥和 lifetime 参数验证 access token；无效或配置不匹配的令牌返回 502，且不设置 Cookie。
8. Vocabulary 从已验证 JWT 的 `role` claim 判断管理员身份；普通用户返回 403，且不设置 Cookie。
9. 管理员 JWT 写入 `ruoyuVocabularyAdmin` Cookie。Cookie 使用：
   - `HttpOnly=true`
   - `SameSite=Strict`
   - `Path=/`
   - `Secure` 由 `AdminAuthentication:CookieSecure` 控制
   - `Max-Age` 使用 Identity 返回的 `expiresIn`；响应无有效值时回退为一小时，JWT 本身仍独立校验过期时间
10. 登录响应只返回成功状态和已验证 JWT 中必要的非敏感用户展示信息，不返回 access token 或 refresh token。
11. Identity 返回的 refresh token 立即丢弃，前端和 Vocabulary 均不持久化。

Identity 超时、网络错误或无有效响应时返回 502；配置缺失返回 503。任何日志都不得包含密码、JWT、Cookie、AppSecret 或完整 Identity 响应。

### 5.3 请求认证

JWT Bearer 按以下顺序提取令牌：

1. `Authorization: Bearer <token>`
2. `ruoyuVocabularyAdmin` HttpOnly Cookie

Bearer 通道用于自动化测试和受控 API 调用；管理前端只使用 Cookie。所有管理端点使用名为 `VocabularyAdmin` 的授权策略，要求已认证且包含 `role=admin`。

认证挑战和禁止访问分别返回：

```json
{ "success": false, "message": "Authentication is required." }
```

```json
{ "success": false, "message": "Administrator access is required." }
```

状态码分别为 401 和 403。

### 5.4 登出和会话

- `GET /admin/auth/session` 由 `VocabularyAdmin` 策略保护，用于前端初始化登录状态。
- `POST /admin/auth/logout` 匿名可调用并始终删除同名 Cookie，使过期或损坏 Cookie 也能被清理。
- 登出后浏览器不再携带 JWT，再次访问管理接口返回 401。
- JWT 本身是无状态令牌，删除 Cookie 不构成服务端全局吊销；由于 refresh token 不保存，浏览器无法自动恢复会话。

### 5.5 Cookie 写请求的 CSRF 防护

`POST`、`PUT`、`PATCH`、`DELETE` 管理请求如果通过 Cookie 而非 Authorization Header 认证，必须包含：

```text
X-Requested-With: XMLHttpRequest
```

前端 Axios 实例统一添加该请求头。服务不开放允许任意来源携带凭据的 CORS 策略。该检查与 `SameSite=Strict` 共同降低跨站表单提交风险。

登录和登出入口不依赖该请求头；登录不使用现有认证 Cookie，登出只执行幂等 Cookie 删除。

## 6. 路由权限矩阵

### 6.1 公开业务接口

以下现有接口继续匿名可访问：

```text
GET  /api/vocabulary/{wordId}
GET  /api/vocabulary
POST /api/vocabulary/question
GET  /api/vocabulary-books/all
```

本次不为这些接口引入管理员 Cookie 或新的服务间认证。如果后续需要服务间认证，应先确认真实调用方并设计独立凭据。

### 6.2 管理接口

以下接口全部要求 `role=admin`，包括读取接口：

```text
POST   /admin/vocabulary
POST   /admin/vocabulary-books
PUT    /admin/vocabulary-books
GET    /admin/vocabulary-books/{id}
GET    /admin/vocabulary-books
GET    /admin/vocabulary-books/by-category
GET    /admin/vocabulary-books/categories
GET    /admin/vocabulary-books/education-levels
GET    /admin/vocabulary-books/grades
GET    /admin/vocabulary-books/grades-by-level
GET    /admin/vocabulary-books/{id}/words
DELETE /admin/vocabulary-books/{id}
```

## 7. 数据关系和 SQLite 初始迁移

### 7.1 正式关系

```text
VocabularyBook 1 ─── * VocabularyMeaning * ─── 1 Vocabulary
```

- `VocabularyMeaning.VocabularyId`：必填，外键到 `Vocabulary`，删除单词时级联删除词义。
- `VocabularyMeaning.BookId`：必填，外键到 `VocabularyBook`，删除词书时限制删除。
- 新增词义前必须确认词书存在且启用。
- 公开详情和题目查询必须确认词书存在且启用。

### 7.2 单一初始迁移

服务未上线且没有 PostgreSQL 存量数据，因此迁移历史重建为一个 SQLite `InitialCreate`。新数据库直接得到非空 `book_id`、双外键、删除行为、查询索引、英美音标列和等价词义唯一索引。EF Core 模型、迁移和模型快照必须保持一致。

启动时在迁移前判断数据库文件是否存在。文件不存在时，迁移后从内嵌 TSV 在一个事务中写入 300 词启动词书；已有文件只迁移，不重复写种子。

## 8. 单词和词义写入

### 8.1 单词规范化

所有写入和按名称查找使用同一规范化规则：

```text
normalizedWord = word.Trim().ToLowerInvariant()
```

- 空白规范化结果返回 400。
- 新单词以规范化值保存。
- 查找单词时使用等价的数据库表达式，避免 `Apple`、` apple ` 和 `APPLE` 被重复导入。
- 唯一索引保护规范化后的新写入单词。
- 单词 DTO 使用 `phoneticUk` 和 `phoneticUs` 两个可空字符串；旧 `phonetic` 字段不再接受或返回。

### 8.2 新增与更新语义

- 请求带有单词 ID 时，单词不存在返回 404，不创建新单词。
- 请求不带单词 ID 时，按规范化名称查找；不存在才创建。
- 请求带有词义 ID 时，词义不存在返回 404。
- 更新词义前必须确认其 `VocabularyId` 和 `BookId` 与当前单词、词书一致。
- 不允许通过词义 ID 把其他单词或其他词书的词义改写为当前数据。
- 新增词义时词书不存在返回 404；词书已禁用返回 422。
- 同一 `VocabularyId`、`BookId`、规范化词性和去除首尾空格后的释义视为同一词义。
- 重复导入返回成功并复用原词义；提供了新的英式音标、美式音标或例句时按既有更新语义更新，不新增重复行。
- SQLite 部署限制为单实例。进程级写事务锁串行化管理写入，stored generated columns 上的逻辑键唯一索引提供数据库兜底。
- 唯一约束冲突、并发重复或其他数据库一致性冲突返回 409。

`Category` 继续使用现有字符串字段作为正式表示。与其不一致且未被使用的整数 `BookCategories` 常量类型删除，不再维护双重表示。

## 9. 词书状态和删除

- 新建词书必须使用空 ID；服务生成新 ID。
- 更新词书必须携带 ID；目标不存在返回 404。
- `Status=true` 表示启用，`Status=false` 表示禁用。
- 管理列表包含启用和禁用词书。
- `GET /api/vocabulary-books/all` 只返回启用词书。
- 禁用词书后，公开词汇详情和题目接口不再返回该词书的词义。
- 管理员仍可查看禁用词书及其关联单词，以便恢复或维护。
- 删除不存在的词书返回 404。
- 没有关联词义的词书允许硬删除。
- 存在任何关联词义的词书拒绝硬删除并返回 409，管理员应改为禁用。
- 词书外键采用 `ON DELETE RESTRICT`，数据库作为最后一道防线阻止孤儿词义。

## 10. 题目生成

`POST /api/vocabulary/question` 保持原请求和响应 DTO，按以下规则生成四选一题目：

1. 目标词书必须存在且启用。
2. 目标单词必须存在，并在目标词书中至少有一个词义。
3. 中译英：
   - 题干为当前词义；
   - 正确答案为当前单词；
   - 三个干扰单词只从同一词书选择。
4. 英译中：
   - 题干为当前单词；
   - 正确答案为当前词义；
   - 三个干扰释义只从同一词书选择。
5. 候选按 `VocabularyId` 去重，每个干扰单词最多贡献一个选项。
6. 仓储查询先排除当前单词和正确答案文本，再按规范化后的选项文本去重，最后随机限量为三个干扰项。
7. 只有获得三个不同的有效干扰项时才生成题目，不得先限量再去重而误报候选不足。
8. 候选不足返回 422 和简洁业务错误。
9. 最终四个选项随机排序。

仓储使用 SQLite `random()`、分组和窗口函数在数据库侧按词书筛选、排除、去重和限量。该方案适用于单本词书数万不同单词以内；如果单本词书增长到更大规模，应改为持久化随机键或预生成候选池，避免随机排序全部候选。

## 11. 查询和分页

- `page` 缺省或为 0 时兼容为 1。
- `size` 缺省或为 0 时兼容为 20。
- `page<0`、`size<0`、`size>100` 或会造成分页算术溢出的值返回 400。
- 单词搜索在数据库侧完成筛选、排序、计数和分页。
- 词书搜索在数据库侧完成名称/描述筛选、排序、计数和分页。
- 分类、学段、年级和按学段查询年级在数据库侧完成筛选和去重。
- 词书内单词通过词义关系在数据库侧选取不同单词，避免加载全部词义后再查单词。
- 管理查询可以访问禁用词书；公开查询只能通过启用词书关系取数。

## 12. 统一错误处理

所有 HTTP 结果使用：

```json
{ "success": true, "data": {} }
```

```json
{ "success": false, "message": "A concise public message." }
```

状态码约定：

| 状态码 | 场景 |
|--------|------|
| 400 | 参数、JSON 或分页边界无效 |
| 401 | 凭据错误、JWT 缺失或无效 |
| 403 | 已认证但不是管理员 |
| 404 | 单词、词义或词书不存在 |
| 409 | 唯一约束、关联删除或其他数据冲突 |
| 422 | 词书禁用、题目候选不足等业务条件不满足 |
| 500 | 未预期异常 |
| 502 | Identity 不可达或返回无效响应 |
| 503 | 生产管理员登录配置缺失 |

端点不再捕获通用异常并返回 `ex.Message`。统一异常处理中间件负责：

- 把已知领域异常映射为 400、404、409 或 422。
- 把数据库唯一约束和外键约束映射为 409。
- 把 JSON 解析和 Minimal API `BadHttpRequestException` 映射为 400。
- 把未预期异常记录为带异常对象的 Error，并返回通用 500 消息。
- 对预期 NotFound 使用 Warning。
- 对认证挑战、禁止访问和未知 API 路径生成相同信封。

任何客户端响应不得包含 SQL、连接字符串、堆栈、数据库异常或内部实现细节。

## 13. 前端设计

前端继续使用 Vue 3、TypeScript、Element Plus、Axios、Vue Router 和 Vite，不新增 Pinia 或其他依赖。

### 13.1 路由和状态

- `/login`：用户名密码登录页。
- `/forbidden`：非管理员提示页。
- `/books`：教材管理。
- `/import`：单词导入。
- 应用启动时调用 `GET /admin/auth/session` 恢复登录状态。
- 路由守卫在未认证时跳转登录页。
- 管理导航和管理页面只在管理员状态下渲染。
- 登录页不包含默认用户名或密码。
- 页头增加登出按钮。

认证状态使用小型 TypeScript 模块或 composable 维护，不引入新的全局状态库。

### 13.2 Axios 行为

- 使用同源相对路径，不配置 Identity 地址。
- 使用 Cookie 认证，不读写 access token。
- 管理写请求添加 `X-Requested-With: XMLHttpRequest`。
- 统一类型化 `ApiError` 保存 HTTP 状态和公开消息。
- 401：清除本地认证状态并跳转登录页。
- 403：跳转无权限页。
- 400、404、409、422、500、502、503：由页面显示适合用户的错误消息。
- 组件 catch 参数使用 `unknown`，不使用 `any`。

教材管理和单词导入的既有功能、字段和 Vite 构建方式保持可用。

## 14. 部署

- Vocabulary 容器内 HTTP 端口固定为 5008。
- Vue 构建产物继续由 Vocabulary Dockerfile 复制到后端 `wwwroot`。
- `start.sh` 默认把部署变量映射为 .NET 配置：

  ```text
  VOCABULARY_ADMIN_AUTH_PROVIDER（默认 QuantumZhou）→ AdminAuthentication__Provider
  VOCABULARY_IDENTITY_APP_ID → AdminAuthentication__QuantumZhou__AppId
  VOCABULARY_IDENTITY_APP_SECRET → AdminAuthentication__QuantumZhou__AppSecret
  VOCABULARY_IDENTITY_AUTHORITY → IdentityService__Authority
  VOCABULARY_COOKIE_SECURE（默认 false）→ AdminAuthentication__CookieSecure
  VOCABULARY_DATA_DIR（默认服务目录 data/）→ /app/data 持久卷
  ```

- `IdentityService:Authority` 由普通配置提供；`start.sh` 默认使用 `http://ruoyu-identity:5002`，可通过 `VOCABULARY_IDENTITY_AUTHORITY` 覆盖。
- 容器把 `VOCABULARY_DATA_DIR` 挂载到 `/app/data`，数据库连接串固定为 `Data Source=/app/data/vocabulary.db`。
- AppId/AppSecret 由部署环境传入 Vocabulary，不打印到控制台。
- TLS 部署设置 `AdminAuthentication__CookieSecure=true`。
- `PROJECT.md` 中 Vocabulary 的协议端口、架构图和端口总表统一记录为 5008。
- `PROJECT.md` 服务间通信矩阵增加 Vocabulary → Identity 的登录代理和 JWKS 校验依赖。
- Identity 需要预先注册 Vocabulary 服务应用并提供 AppId/AppSecret；不修改 Identity 现有管理员引导逻辑。

## 15. 测试和验证

### 15.1 认证与权限

- 未登录访问每类管理接口返回 401 信封。
- 普通用户 JWT 访问管理接口返回 403 信封。
- fake Identity 返回 bootstrap admin 角色时登录成功并设置 HttpOnly Cookie。
- 错误用户名或密码返回 401，且不设置 Cookie。
- 普通用户登录返回 403，且不设置 Cookie。
- 管理员 Cookie 可以访问教材和单词管理接口。
- 登出删除 Cookie；之后访问管理接口返回 401。
- Authorization Bearer 管理员令牌也可访问管理接口。
- 既有 `/api/*` 匿名行为保持兼容。
- Cookie 写请求缺少 CSRF 防护头时被拒绝。

### 15.2 领域和数据

- 不存在或禁用词书不能新增词义。
- 更新不存在的单词、词义或词书返回 NotFound。
- 不属于当前单词或词书的词义不能更新。
- 规范化后的单词只创建一次。
- 重复词义导入不新增第二条数据。
- 已使用词书删除返回 Conflict。
- 空词书可以删除。
- 禁用词书不出现在公开列表，公开详情和题目接口不能读取其词义。
- EF 模型包含词义到单词和词书的双外键及正确删除行为。
- 真实 SQLite 验证缺失数据库首次建库、300 词种子、已有数据库只迁移和并发幂等写入。

### 15.3 题目生成

- 中译英干扰单词只来自当前词书。
- 英译中干扰释义只来自当前词书。
- 干扰项不包含正确答案。
- 多词义单词不会产生重复干扰选项。
- 不同词书的数据不会互相污染。
- 少于三个有效干扰单词时返回 422。

### 15.4 HTTP 和错误处理

- 验证 400、401、403、404、409、422、500、502、503 的状态和统一信封。
- fake Identity HTTP handler 验证请求字段及服务端 AppId/AppSecret 请求头。
- 模拟仓储异常，确认 500 响应不包含内部异常消息。
- 未知 `/api/*` 返回 404 信封；未知 `/admin/*` 对匿名请求返回 401，对管理员请求返回 404 信封。
- SPA、静态文件、登录页和健康检查保持匿名可访问。

### 15.5 交付验证命令

```bash
cd src/services/ruoyu.vocabulary/src
dotnet test Ruoyu.Study.Vocabulary.sln --configuration Release
dotnet build Ruoyu.Study.Vocabulary.sln --configuration Release
```

```bash
cd src/services/ruoyu.vocabulary/frontend
npm run test:types
npm run build
```

在 Identity 和 Vocabulary 可运行时，补充登录、Cookie、登出、公开 API、持久卷和迁移状态的 HTTP smoke test。无法获得真实部署凭据时，自动化集成测试使用 fake Identity，不编造真实联调结果。
