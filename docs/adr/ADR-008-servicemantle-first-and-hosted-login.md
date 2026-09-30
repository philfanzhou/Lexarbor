# ADR-008：ServiceMantle 能力优先与托管登录唯一化

- **状态**：已接受（取代 [ADR-001](ADR-001-pluggable-admin-authentication.md) 的默认方向；细化 [ADR-007](ADR-007-management-session-migration.md) 的暂缓结论）
- **日期**：2026-09-30
- **范围**：Lexarbor 与 ServiceMantle、SignaCore 的能力边界划分；管理员登录方式收敛。本 ADR 只记录决策与理由，不授权任何兼容性变化，具体变化仍由各实施 issue 明确批准。
- **对照基线**：Lexarbor `main@e12a572`；ServiceMantle [`v0.2.1`](https://github.com/philfanzhou/ServiceMantle/tree/v0.2.1)；SignaCore `main@bcac0ea`。

## 背景与目标

Lexarbor 的方向调整为：**ServiceMantle 已有的通用能力不在本仓库重复实现，已有的重复或近似实现替换为 ServiceMantle；管理员登录只使用 SignaCore 托管登录页。**

ADR-001 记录了可插拔密码代理登录（`Oidc` 密码授权与 `Gateway` 两种提供者），其前提是 Lexarbor 自己渲染登录表单并经手管理员密码。ADR-007 暂缓了 ServiceMantle 管理会话的迁移，但只比较了会话契约本身，没有回答"哪些通用能力应该反过来主动收敛到 ServiceMantle"。托管登录（[HostedAdminLogin.cs](../../src/Lexarbor.Host/Authentication/HostedAdminLogin.cs)、[#164](https://github.com/philfanzhou/Lexarbor/issues/164)、[#155](https://github.com/philfanzhou/Lexarbor/issues/155)）落地后，密码代理模式已成冗余路径。本 ADR 为上述方向提供统一依据。

对照的上游文档：

- ServiceMantle [`README.md`](https://github.com/philfanzhou/ServiceMantle/blob/v0.2.1/README.md) 与 [`docs/contracts/*`](https://github.com/philfanzhou/ServiceMantle/tree/v0.2.1/docs/contracts)；
- SignaCore [`docs/integrations/HostedLogin.md`](https://github.com/philfanzhou/SignaCore/blob/bcac0ea/docs/integrations/HostedLogin.md)。

## 决策一：能力映射

逐项结论如下。"替换/新增采用"仅表示方向已定，各自的兼容性变化、实施与验收由对应 issue 批准和验证。

| # | Lexarbor 现有实现 | ServiceMantle 对应能力 | 结论 | 对应 issue |
| --- | --- | --- | --- | --- |
| 1 | 可信代理转发头（`Program.cs` 中 `Network:*` 已配置时的 `AddForwardedHeaders`，原 `RateLimitingExtensions` 内联实现已删） | [Explicit forwarded-header trust](https://github.com/philfanzhou/ServiceMantle/blob/v0.2.1/README.md#explicit-forwarded-header-trust) | 已替换 | [#143](https://github.com/philfanzhou/Lexarbor/issues/143)（已完成） |
| 2 | `VocabularyExceptionMiddleware`（`src/Lexarbor.Service/VocabularyExceptionMiddleware.cs`）手写 `{success,message}` envelope 与异常分级 | [Safe Problem Details and exception mapping](https://github.com/philfanzhou/ServiceMantle/blob/v0.2.1/README.md#safe-problem-details-and-exception-mapping)（含 0.2.1 的条件映射与 `Retry-After`） | 替换（错误契约变化须 issue 批准） | [#144](https://github.com/philfanzhou/Lexarbor/issues/144) |
| 3 | 无请求级关联 ID；日志与响应无 Correlation 字段 | [Request Correlation ID](https://github.com/philfanzhou/ServiceMantle/blob/v0.2.1/README.md#request-correlation-id) | 新增采用（随 #144 一体交付） | [#144](https://github.com/philfanzhou/Lexarbor/issues/144) |
| 4 | `UseSystemVersionNoStore` 与 `AdminHostedLoginSafety`（`src/Lexarbor.Host/SystemVersionEndpoints.cs`、`HostedAdminLogin.cs`）逐响应头手写 | [Mandatory security response headers](https://github.com/philfanzhou/ServiceMantle/blob/v0.2.1/README.md#mandatory-security-response-headers)（六头基线、不可配置） | 替换 | [#177](https://github.com/philfanzhou/Lexarbor/issues/177) |
| 5 | 默认 Console 日志 + 手写过滤；敏感值防泄漏依赖各调用点自觉 | [Structured logging security](https://github.com/philfanzhou/ServiceMantle/blob/v0.2.1/README.md#structured-logging-security)（`ServiceMantle.Logging` 脱敏管线、`AddSensitiveHeaders` 敏感头注册） | 替换（保留少量产品规则） | [#178](https://github.com/philfanzhou/Lexarbor/issues/178) |
| 6 | `PersistentAdminKeyRing`（`src/Lexarbor.Host/PersistentAdminKeyRing.cs`）明文 XML 文件密钥环 | [`PersistKeysToServiceMantleEfCore`](https://github.com/philfanzhou/ServiceMantle/blob/v0.2.1/README.md#typed-setting-snapshots)（`service_data_protection_keys` 表、`sm:v1:` 认证信封加密） | 替换 | [#179](https://github.com/philfanzhou/Lexarbor/issues/179) |
| 7 | `/health` 恒为 healthy（`Program.cs` 直接映射） | [Live and readiness endpoints](https://github.com/philfanzhou/ServiceMantle/blob/v0.2.1/README.md#live-and-readiness-endpoints)（`/health/live`、`/health/ready`、`IServiceHealthSnapshotSource`） | 替换（响应格式变化须 issue 批准） | [#180](https://github.com/philfanzhou/Lexarbor/issues/180) |
| 8 | `DatabaseInitializer`（`src/Lexarbor.Database/DatabaseInitializer.cs`）建库 + 迁移 | [Database target preparation](https://github.com/philfanzhou/ServiceMantle/blob/v0.2.1/README.md#database-target-preparation)（SQLite 目标准备、部署校验）与 [Database migration orchestration](https://github.com/philfanzhou/ServiceMantle/blob/v0.2.1/README.md#database-migration-orchestration)（单实例迁移编排） | 替换 | [#181](https://github.com/philfanzhou/Lexarbor/issues/181) |
| 9 | `VocabularyAdmin` 策略 + `AdminRoleHandler`（`Program.cs`、`src/Lexarbor.Host/Authentication/`） | [Management identity and authorization](https://github.com/philfanzhou/ServiceMantle/blob/v0.2.1/README.md#management-identity-and-authorization)（`servicemantle.*` 声明契约、`ServiceMantle.ManagementAdmin` 策略） | 替换（保留"管理员身份来自 SignaCore 角色声明"的产品规则） | [#182](https://github.com/philfanzhou/Lexarbor/issues/182) |
| 10 | 无持久管理审计 | [Management audit persistence](https://github.com/philfanzhou/ServiceMantle/blob/v0.2.1/README.md#management-audit-persistence)（`service_audit_logs`、写入脱敏、keyset 分页查询） | 新增采用 | [#183](https://github.com/philfanzhou/Lexarbor/issues/183) |
| 11 | 无遥测 | [Core OpenTelemetry instrumentation](https://github.com/philfanzhou/ServiceMantle/blob/v0.2.1/README.md#core-opentelemetry-instrumentation)（`ServiceMantle.Diagnostics`，默认关闭、无导出器） | 新增采用（低优先级） | [#184](https://github.com/philfanzhou/Lexarbor/issues/184) |
| 12 | 仅在配置可信代理时注册 `AddServiceMantle`，且每次启动随机 `InstanceId` | [Structured logging identity context](https://github.com/philfanzhou/ServiceMantle/blob/v0.2.1/README.md#structured-logging-identity-context)（宿主身份应始终注册；#2、#3、#4、#6、#7 的前置） | 基础 | [#174](https://github.com/philfanzhou/Lexarbor/issues/174) |
| 13 | `Oidc` 密码授权、`Gateway` 提供者、密码表单（`OidcPasswordAuthenticator.cs`、`GatewayCredentialAuthenticator.cs`、旧 `LoginView`） | SignaCore 托管登录（已接入的 `OidcCode` 流程） | 删除，托管登录唯一化 | [#175](https://github.com/philfanzhou/Lexarbor/issues/175)、[#176](https://github.com/philfanzhou/Lexarbor/issues/176) |
| 14 | `AdminSessionStore` 等服务端会话（`src/Lexarbor.Host/Authentication/AdminSessionStore.cs` 等） | [management session](https://github.com/philfanzhou/ServiceMantle/blob/v0.2.1/docs/contracts/management-session.md) | 保留（上游缺口，见"明确不采用"） | 本 ADR |
| 15 | `admin-login`/`public-api` 限流（`AddLexarborRateLimiting`） | [Isolated setup and management rate limiting](https://github.com/philfanzhou/ServiceMantle/blob/v0.2.1/README.md#isolated-setup-and-management-rate-limiting) | 保留（契约不同，见下） | 本 ADR |
| 16 | `HealthCheckCommand`、`ApplicationVersion`（revision/channel） | 无对应能力 | 保留 | — |

第 2、3 项的关系：ServiceMantle 的 Problem Details 响应体固定携带 `correlationId` 并复用 `x-correlation-id` 响应头（见其 README 的 Problem Details 章节），因此 Correlation ID 中间件与异常映射作为同一替换切片交付，不再自造关联字段。

## 决策二：托管登录唯一化

管理员登录只保留已接入的 `OidcCode` 托管登录流程（SignaCore [HostedLogin](https://github.com/philfanzhou/SignaCore/blob/bcac0ea/docs/integrations/HostedLogin.md)：授权码 + PKCE S256 + 服务端兑换，`HostedAdminLogin.cs` 已实现，协同登出按 [#154](https://github.com/philfanzhou/Lexarbor/issues/154) 交付）。移除：

- `OidcPasswordAuthenticator`（OAuth2 resource owner password credentials 授权，`src/Lexarbor.Host/Authentication/Providers/OidcPasswordAuthenticator.cs`）；
- `GatewayCredentialAuthenticator`（JSON/header 密码代理契约，`src/Lexarbor.Host/Authentication/Providers/GatewayCredentialAuthenticator.cs`）；
- 承载它们的 `IAdminCredentialAuthenticator` 选择机制（ADR-001 的 `AdminAuthentication:Provider`）与密码登录表单路径。

安全收益：

- 管理员密码不再以任何形式经过 Lexarbor——不进入请求体、内存、日志或上游转发路径，Lexarbor 不再是密码的经手方与潜在的泄漏点；
- 凭据输入只发生在 SignaCore 托管页，Lexarbor 只接收一次性授权码并按 `iss`+`sub` 建立本地会话；client secret 与 token 全部留在服务端；
- 删除密码代理后，ADR-001 定义的 `AdminAuthentication:Oidc:*`、`AdminAuthentication:Gateway:*` 配置面随之消失，机密配置面缩小为 SignaCore 客户端注册项。

部署影响（由 #175/#176 各自的验收与英文部署文档承载）：部署方必须在 SignaCore 为 Lexarbor 注册 Confidential 应用、登记 HTTPS 回调与 post-logout URI 并启用授权码流程；已按 ADR-001 部署密码授权的环境需迁移到托管登录，旧密码代理配置不再被读取。这是登录入口的兼容性变化，须按上述 issue 批准。

## 明确不采用及理由

各项附重新评估条件；条件满足前不重复立项。

1. **ServiceMantle 管理会话**（[management-session.md](https://github.com/philfanzhou/ServiceMantle/blob/v0.2.1/docs/contracts/management-session.md)）。登录入口只接受 `POST` 凭据体（适配器自定 JSON），无法承接托管登录的 GET 回调——回调在授权码兑换之前，浏览器到达时没有任何凭据体可读；其票据是无状态 Data Protection Cookie，登出只删本客户端 Cookie、不撤销已复制票据，不能满足 Lexarbor 的服务端原子撤销要求（[#157](https://github.com/philfanzhou/Lexarbor/issues/157) 已交付的 `AdminSessionStore` 语义）；prepared logout（[#154](https://github.com/philfanzhou/Lexarbor/issues/154)）要求服务端在会话存续期保存该用户的 ID token，无状态票据没有服务端存储位置。ADR-007 的逐项契约对照与净收益估算继续有效。
   *重新评估条件*：ServiceMantle 提供可撤销的服务端管理会话存储与 OIDC 授权码回调签入（见"上游缺口"）。
2. **Bootstrap / Setup Code / 安装阶段 / 阶段门 / 共享设置**（[bootstrap](https://github.com/philfanzhou/ServiceMantle/blob/v0.2.1/README.md#instance-local-bootstrap)、[One-time Setup Code](https://github.com/philfanzhou/ServiceMantle/blob/v0.2.1/README.md#one-time-setup-code)、[phase gate](https://github.com/philfanzhou/ServiceMantle/blob/v0.2.1/README.md#management-paths-and-startup-phase-gate)、[Transactional setting batches](https://github.com/philfanzhou/ServiceMantle/blob/v0.2.1/README.md#transactional-setting-batches)）。Lexarbor 没有首次安装流程：配置来自镜像默认 + 持久 `appsettings.json` + 环境变量（`PersistentConfigurationBootstrapper`），管理员身份来自 SignaCore 角色声明而非本地管理员引导。
   *重新评估条件*：Lexarbor 出现真实的首次安装/集中配置产品需求。
3. **ServiceMantle 限流**（[Isolated setup and management rate limiting](https://github.com/philfanzhou/ServiceMantle/blob/v0.2.1/README.md#isolated-setup-and-management-rate-limiting)）。只提供 `servicemantle.setup`/`servicemantle.management` 两个命名策略，拒绝体是 `application/problem+json` 的 `rate_limit.exceeded`；Lexarbor 的 `admin-login`（按可信客户端地址分区、`Retry-After`）与 `public-api`（匿名公共读）429 契约是已发布的兼容性契约，两者分区与响应语义均不同。
   *重新评估条件*：#144 将错误契约统一切换到 Problem Details 后，若 429 拒绝体可合并且分区规则可表达，再单独评估。
4. **`UseServiceMantlePipeline`**（[Composed HTTP pipeline](https://github.com/philfanzhou/ServiceMantle/blob/v0.2.1/README.md#composed-http-pipeline)）。该组合强制要求第 2、3 项能力（安全响应头注册与阶段门）并固定"限流在认证之后"的中间件顺序；在 Lexarbor 的公共 API 限流与托管登录回调布局下不可直接套用。各中间件仍按第 2—9 项逐个显式启用，保持 Lexarbor 自己的管线顺序与匿名公共路由可达性。
   *重新评估条件*：上述能力全部落地且 Lexarbor 管线与其固定顺序一致时，作为纯重构单独评估。
5. **Consul 服务发现**（[Optional Consul client boundary](https://github.com/philfanzhou/ServiceMantle/blob/v0.2.1/README.md#optional-consul-client-boundary)）。单容器部署无服务注册与发现需求。
   *重新评估条件*：Lexarbor 出现多实例/注册中心部署形态。

## 上游缺口

以下能力 ServiceMantle v0.2.1 未提供；若后续版本提供，按对应"重新评估条件"重启对第 14 项（及 ADR-007）的评估，替换 `AdminSessionStore` 等实现：

1. **可撤销的服务端管理会话存储**：登录签入后的会话状态保存在服务端，支持原子撤销（登出即全局失效），而非仅删除客户端 Cookie 的无状态票据。
2. **OIDC 授权码回调签入**：管理会话登录入口原生接受 OIDC 授权码回调（GET + `code`/`state`），由库完成 state 校验、PKCE 兑换与票据签发，而不是只暴露 POST 凭据体适配器。

## 对既有 ADR 的修订

- **ADR-001**：其"可插拔密码代理"方向已被本 ADR 取代——`Oidc`/`Gateway` 密码提供者与密码表单将随 #175/#176 删除，登录只保留 SignaCore 托管登录。取代不改变该 ADR 已交付历史的记录价值；其安全属性（不回传 provider profile、机密只在服务端）由 `OidcCode` 流程继承。
- **ADR-007**：暂缓迁移 ServiceMantle 管理会话的结论保持，理由由本 ADR 从"暂缓"升级为"明确不采用，待上游缺口关闭后重评"。

## 影响

- 后续实施 issue（#144、#174–#184）引用同一份能力映射与取舍理由，不再各自论证边界。
- 本 ADR 不交付任何能力迁移，不改变任何运行时行为；每项兼容性变化由对应 issue 明确批准、验证并同步英文使用者文档。
