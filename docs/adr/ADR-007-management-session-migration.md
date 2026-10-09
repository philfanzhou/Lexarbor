# ADR-007：暂缓迁移至 ServiceMantle 管理会话

- **状态**：已决定，暂缓生产迁移。[ADR-008](ADR-008-servicemantle-first-and-hosted-login.md) 将本 ADR 的"暂缓"升级为"明确不采用，待 ServiceMantle 提供可撤销服务端会话与 OIDC 授权码回调签入后重评"；本 ADR 的契约对照与净收益估算继续有效。
- **日期**：2026-09-29
- **范围**：[Issue #145](https://github.com/philfanzhou/Lexarbor/issues/145) 的认证契约对照与仅在测试项目中接入该库的原型；比较对象为已发布的 `ServiceMantle.Web` v0.2.0

## 现状和决策

Lexarbor 的管理界面和 API 已把 JWT Cookie、Bearer、`/admin/auth/*` 和 `{username,roles}` 作为共同契约。ServiceMantle 的管理会话使用固定的版本化路由、Data Protection 票据 Cookie 和 `{authenticated,expiresAtUtc,permissions}`。其 `IManagementIdentityProvider` 无凭据参数，不能替代 Lexarbor 的 OIDC/Gateway 密码交换或 JWT 校验。若保持旧契约，需要另外写入口适配、会话 facade、双认证方案和业务路由授权；可删的代码有限。因此本轮**暂缓采用管理会话**，保留现有生产认证。此决定不影响 #143 的独立可信代理复用。

依据为当前 `main@a0dd411` 的 `AdminAuthEndpoints`、`AdminAccessTokenValidator`、`CookieCsrfMiddleware`、`AdminRoleHandler`、`Program.cs`、前端 `authApi.ts`、`authState.ts`、`api.ts`、`LoginView.vue`、`router/index.ts`，以及 [ServiceMantle v0.2.0 的 management session 契约](https://github.com/philfanzhou/ServiceMantle/blob/v0.2.0/docs/contracts/management-session.md)。

## 契约逐项对照

| 行为 | Lexarbor 当前契约 | ServiceMantle v0.2.0 | 若迁移所需决定 |
|---|---|---|---|
| 登录 | `POST /admin/auth/login`，JSON `username,password`；成功 200 `data:{username,roles}`，写 JWT Cookie | `POST /management/v1/session/login`；消费方 adapter 自定凭据 JSON；成功 204，写固定 DP 票据 Cookie | 保留旧路由和 JSON 需 facade；否则前端登录后再读会话并改变公开契约。凭据只能在服务端 adapter/作用域中流动。 |
| 登录失败 | 错误凭据 401、非管理员 403、上游失败或无效 token 502、未配置 503；均无有效 Cookie | 未认证 401、提供者或签入失败 503；仅在 Completed+Succeeded+Reachable 放行 | 非管理员 403 无直接映射，不能假称无兼容变化；错误签名、过期 token、上游故障必须在签入前拒绝。 |
| 读取会话 | `GET /admin/auth/session`，JWT Cookie 或 Bearer；成功 200 `data:{username,roles}`，无效 401 | `GET/HEAD /management/v1/session`；成功 200 `{authenticated,expiresAtUtc,permissions}`；不返回用户名、原始角色 | 旧前端需要服务端 facade 和经验证的身份来源；不能从 `permissions` 猜 `roles` 或用户名。Bearer 需独立保留。 |
| 登出 | `POST /admin/auth/logout`，200 成功 envelope，删除现有 JWT Cookie | `POST /management/v1/session/logout`，204，删除固定 Cookie，不撤销复制的票据 | 旧路径/响应需 facade；旧 JWT Cookie 与 DP Cookie 不互认，升级和回滚均需清除或限期并存，未知结果先重读会话。 |
| `/admin/*` 读写 | `VocabularyAdmin` 检查可配置 `RequiredRole`；Cookie 写需 `X-Requested-With: XMLHttpRequest`，Bearer 写免此头；分别返回 401/403 | ServiceMantle 管理入口检查固定 Cookie 身份和 `management.admin` 权限；写入口要求 `X-ServiceMantle-Request: 1` | 库不会自动保护 Lexarbor 业务路由。须显式组合 Cookie/Bearer scheme、角色映射和仅 Cookie 的 CSRF；不得因有管理权限就给非管理员业务写权限。 |
| Cookie | `lexarborAdmin` 可配置，HttpOnly、SameSite=Strict、`CookieSecure` 可配置，值为验证过的 JWT | `__Host-ServiceMantle.Management` 固定，HttpOnly、SameSite=Strict、Secure=Always，默认 8 小时并滑动续期，值为 DP ticket | Cookie 名、Secure、生命周期和旧会话续存都发生兼容变化。须确定 HTTPS、DP 密钥持久化和失效窗口。 |
| 登录限流 | `admin-login` 按可信客户端地址计数，429 带 Lexarbor envelope 与 `Retry-After` | 库登录使用 setup 限流与固定结果；不会继承 Lexarbor 原策略 | 需在新入口显式保留当前分区、阈值、429 契约，并核对双重限流顺序。 |

前端 `LoginView.vue` 提交登录后 `authState.ts` 立即使用成功响应设置 `currentUser`；路由守卫首次导航调用 `restoreSession()`；`App.vue` 显示当前用户名并提供登出；`api.ts` 固定加 `X-Requested-With`，解包 `{success,data}`，且处理 401/403。直接使用库的 204 状态码和固定的响应字段会破坏这几处调用和错误处理。`/api/*`、SQLite 和容器数据目录不因本 ADR 改动。

## 隔离原型与关键场景验证

`ServiceMantleManagementDecisionTests` 仅在测试项目引用 v0.2.0。原型的 `PrototypeLoginAdapterAsync` 从请求体读取凭据，放入测试作用域对象，再通过 `ManagementIdentityProviderInvoker` 调用实现 `IManagementIdentityProvider` 的 provider。该 provider 沿用 `IAdminCredentialAuthenticator` 和 `AdminAccessTokenValidator`，只从通过签名、issuer、audience、lifetime 验证的 JWT 读取角色与 subject，符合所需角色才创建 `ManagementPermission.Admin`。普通角色和错误凭据返回 `Unauthenticated`；坏签名、过期 token、身份服务不可用返回 `Failed`，均无管理身份。没有真实 OIDC/Gateway 请求或票据签入。原型代码不在生产入口注册。

| 场景 | 可复核测试与结果 | 迁移缺口 |
|---|---|---|
| 有效管理员、普通角色、错误凭据、坏签名、过期 token、上游不可用 | 新增的 6 个 `ServiceMantleManagementDecisionTests` 用假身份服务执行真实 Lexarbor JWT 验证，并调用库的身份提供者接口验证其三种返回结果；6/6 通过 | 尚未接入库固定 HTTP 会话端点和 DP Cookie。 |
| Cookie 写请求缺 CSRF；Bearer 写请求 | 现有 `AdminAuthenticationTests.CookieWrite_WithoutRequestedWithHeader_Returns403`、`BearerWrite_DoesNotRequireCookieCsrfHeader`；仍用当前业务路由实测 | 新 Cookie scheme 与旧 Bearer 并存时须重跑相同断言。 |
| 登出及失效后 401；无 CSRF 头的登出 | 现有 `AdminAuthenticationTests.Logout_DeletesCookieAndSubsequentAdminRequestReturns401`、`Logout_WithoutRequestedWithHeader_Returns403` | 库登出仅清除自身票据，不能清除旧 Cookie 或撤销复制票据。 |
| 登录按地址限流、429 envelope 与重试时间 | 现有 `RateLimitingTests.AdminLogin_BeyondPermitLimit_Returns429Envelope`、`AdminLogin_Rejection_CarriesRetryAfter`、`AdminLogin_ExhaustedByOneAddress_StillAdmitsAnother` | 新登录路由尚未注册限流并返回相应的 429 响应。 |

这些验证证明身份映射可行，也确认业务授权、CSRF、登出和限流不能因启用库的管理会话而删除。库 HTTP 端点、实际外部提供者、取消后的票据回滚、并发登录/登出和旧会话升级未由此原型验证；若未来重启迁移，必须在隔离 HTTP 宿主和部署环境补这些端到端测试，不能将本原型视为生产验收。

复现命令：`dotnet restore Lexarbor.sln`，然后 `dotnet test tests/Lexarbor.Service.Tests/Lexarbor.Service.Tests.csproj --configuration Release --no-restore --filter FullyQualifiedName~ServiceMantleManagementDecisionTests`。当前结果 6/6；现有认证与限流测试随完整解决方案测试运行。

## 运行成本、规模与回滚

管理会话要求独立的管理 API v1、管理入口、Cookie 认证、启动条件检查（条件未满足时拒绝管理入口请求，包括登录）和健康快照。Lexarbor 目前只有简单 `/health` 与 SQLite 初始化；消费方需提供 `IServiceHealthSnapshotSource` 的 Completed、迁移成功及数据库可达语义，避免登录请求在启动条件未满足时被错误拒绝。库的 Cookie 由 ASP.NET Data Protection 加密；单容器重建若密钥仅驻内存，所有管理会话失效。若持久化密钥，须明确单实例 `/app/data` 的密钥保护、备份、权限、轮换、恢复和升级；多实例还需共享密钥及一致的阶段/健康状态。本 ADR 不创建密钥环或目录。

用 `wc -l` 对上述 `main@a0dd411` 文件计数，估算如下；仅作为实施量级，不把注释/空行当作可删功能：

| 项 | 当前行数／未来估算 | 判断 |
|---|---:|---|
| `AdminAuthEndpoints.cs` | 137 行；真正可直接删约 40–70 行 | 登录、会话、登出路由虽可映射到库，旧路径/JSON facade 和 Cookie 清理仍需代码。 |
| `AdminAccessTokenValidator.cs`、两个凭据提供者 | 87 + 141 + 223 = 451 行 | 必须保留，上游协议和 JWT 信任不属于库。 |
| `CookieCsrfMiddleware.cs`、`AdminRoleAuthorization.cs` | 61 + 37 = 98 行 | 业务路由仍需同等守卫，不能直接删除。 |
| 新凭据 adapter、provider、旧响应 facade | 约 180–280 行 | 原型已有约 110 行核心映射，正式输入验证、错误映射与兼容 facade 还需增加。 |
| 新双认证方案/业务 CSRF、启动与健康状态检查、Data Protection 密钥配置 | 约 90–160 行 | 需覆盖部署密钥与旧会话切换。 |
| 前端和集成测试 | 约 40–80 行前端、150–250 行测试 | 当前前端 `authApi.ts` 18 行、`authState.ts` 50 行、`api.ts` 71 行均受影响。 |

预计删 40–70 行，新增后端约 270–440 行，另有前端与测试工作；且会引入 Cookie、路由、JSON、状态码和会话升级成本。当前净收益不足，暂缓迁移更符合现有兼容性要求。若未来服务间统一管理会话成为明确产品目标，应先创建后端凭据/票据与 DP 持久化任务，再创建业务双 scheme/CSRF 任务，最后创建前端契约迁移与停用旧会话机制的任务；每项单独定义验收和回滚，不能由本 ADR 直接授权。

本 PR 回滚仅删除 ADR、测试与测试专用包引用，无生产数据或会话需恢复。未来若启用新会话，回滚前应停用新登录并让客户端重新登录旧入口；保留旧 JWT 验证直至旧会话自然到期，清理新 Cookie 后再移除 Data Protection 密钥与启动条件检查的接入代码。不能假定两种票据互认，也不能把并发请求的迟到成功响应视为当前会话。
