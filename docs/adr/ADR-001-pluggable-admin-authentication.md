# ADR-001 管理员认证改为可插拔 Provider

- **状态**：已接受
- **日期**：2026-08-05
- **范围**：Vocabulary 管理员登录与授权；不改变公开 `/api/*`

## 背景

Vocabulary 的管理员登录直接内联了 QuantumZhou.Identity 的私有契约：`POST /api/auth/token`、camelCase 请求体、`X-Admin-AppId` / `X-Admin-AppSecret` 请求头、`success` 响应信封。这些细节散落在 `IdentityTokenClient`、`IdentityServiceOptions`、`Program.cs` 的 DI 和 `AdminAuthEndpoints` 四处。

Identity 本身位于独立仓库 `philfanzhou/QuantumZhou.Identity`，其契约变更对本仓库不可见，只能在运行时暴露。`docs/pending-decisions.md` 的 PD-004 正在讨论把该网关协议换成 HMAC 签名或 mTLS，届时改动面会跨越上述四处。

同时发现一处既有缺陷（见下）说明"手写一套认证接线"的成本已经实际发生。

## 决定

引入 `IAdminCredentialAuthenticator` 作为唯一知晓 provider 线上协议的接缝：

```
AuthenticateAsync(username, password) -> { Status, AccessToken?, ExpiresIn? }
```

- `QuantumZhouIdentityAuthenticator`：默认实现，保持既有私有契约。
- `OidcPasswordAuthenticator`：标准 OAuth2 password grant（RFC 6749 §4.3），token endpoint 优先取配置，缺省时从 JWT Bearer 已缓存的 discovery 文档解析。
- 由 `AdminAuthentication:Provider` 选择，在 DI 解析时决定，不做运行时切换。

接缝下游（令牌校验、角色判定、Cookie 签发、CSRF、错误信封）全部 provider 无关。

### 结果对象只带令牌

`AdminCredentialResult` 不携带 provider 自报的用户信息。登录响应的 `username` 和 `roles` 一律从**已验证的 JWT** 派生。此前 username 存在一条 `?? tokenResponse.UserInfo.Username` 兜底，会把未经验证的 provider 自报字符串回显给客户端，已移除。

### 配置按语义拆分

| 配置 | 归属 | 理由 |
|------|------|------|
| `IdentityService:{Authority,Issuer,Audience}` | 保持不变 | 语义是"信任哪个签发方"，由共享 Consul KV `config/ruoyu/service-endpoints.json` 发布给所有服务，是平台级契约，非本服务可改名 |
| `AdminAuthentication:Provider` / `RequiredRole` | 新增 | 服务本地 |
| `AdminAuthentication:QuantumZhou:{Authority,TokenPath,AppId,AppSecret}` | 由 `IdentityService:*` 迁出 | 语义是"怎么换令牌"，且 AppId/AppSecret 本就不经 Consul，只由部署环境注入 |
| `AdminAuthentication:Oidc:{TokenEndpoint,ClientId,ClientSecret,Scope}` | 新增 | 同上 |

`QuantumZhou:Authority` 可选，缺省回落到 `IdentityService:Authority`，使登录端点与 JWKS 端点可以不同源。

## 一并修复的缺陷

Identity 通过 `new JwtPayload(...)` 直接构造载荷，绕过了 outbound claim 类型映射，因此角色以完整的 `ClaimTypes.Role` URI 进入 JWT，而非短名 `role`（`Domain/ClaimsResolver.cs`、`Domain/Services/TokenService.cs`，其单测 `JwtTokenServiceTests` 断言了这一点）。`CallbackService` 的自定义 claim 白名单不含 `role`，门户回调也无法注入短名。

Vocabulary 此前配置 `RoleClaimType = "role"` 并使用 `RequireRole("admin")`，因此对真实 Identity 签发的令牌，`IsInRole("admin")` 恒为 false，**全部管理接口恒返回 403**。该缺陷未被发现，是因为测试替身自行签发短名 `role` 的令牌，实现了 Identity 并不实现的契约。

修复：

- `VocabularyClaims` 同时接受短名与 URI 两种形态，姓名解析链 `ClaimTypes.Name → preferred_username → unique_name → nickname`，回落到 subject（`NameIdentifier → sub → nameid`，与 Identity CI 的 `verify_jwt.py` 对齐）。
- 授权改用 `AdminRoleRequirement` + `AdminRoleHandler`，从 options 读取 `RequiredRole`。
- 测试工厂改为签发 Identity 真实形态的令牌，且不再整体替换 `TokenValidationParameters`，只替换签名密钥，claim 类型继承 `Program.cs`，防止替身再次漂移。

## 备选方案

- **改用共享的 `AddRuoyuJwtBearer`**：被否决。它设置 `FallbackPolicy = RequireAuthenticatedUser()`，会使 Vocabulary 的匿名 `/api/*` 与 SPA fallback 全部返回 401，且不支持从 Cookie 提取令牌。
- **只修 claim 类型，不做 provider 抽象**：可解决当前缺陷，但 PD-004 落地时仍需改动四处。
- **保留 `IIdentityTokenClient`**：其返回类型直接暴露私有响应信封，是接口壳而非抽象。

## 影响

- 部署需改用 `AdminAuthentication__QuantumZhou__AppId` / `__AppSecret` 环境变量（`start.sh` 已更新）。服务尚未上线，无存量部署需要迁移。
- Consul KV 无需改动。
- PD-004 落地时，改动收敛到 `QuantumZhouIdentityAuthenticator` 单个类。
- 公开 `/api/*` 的路径、请求字段和响应结构不变。
