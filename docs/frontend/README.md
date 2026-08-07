# Lexarbor 管理前端规格

## 技术栈与边界

- Vue 3.5、TypeScript、Vue Router、Element Plus、Axios、Vite。
- 不新增状态管理或认证依赖。
- 前端由 Vocabulary 后端托管，生产环境与管理 API 同源。
- 前端只调用相对 `/admin/*` 路径，不知道 Identity 地址。
- 前端不保存或读取 access token、refresh token、AppSecret、管理员默认账号或密码。

## 路由

| 路径 | 访问条件 | 页面 |
|------|----------|------|
| `/login` | 匿名 | Identity 管理员用户名密码登录 |
| `/forbidden` | 匿名 | 非管理员提示 |
| `/books` | 管理员 | 教材管理 |
| `/import` | 管理员 | 单词导入 |

应用使用 hash history。首次进入保护页面时调用 `GET /admin/auth/session` 恢复 Cookie 会话；未登录跳转 `/login`，403 跳转 `/forbidden`。未认证时不渲染管理导航或可操作页面。

## 认证 API

| 方法与路径 | 请求/响应 |
|------------|-----------|
| `POST /admin/auth/login` | `{ username, password }`；成功只返回非敏感会话信息 |
| `GET /admin/auth/session` | 返回当前管理员会话 |
| `POST /admin/auth/logout` | 删除服务端 Cookie，前端始终清空本地状态 |

Axios 使用 `withCredentials=true`。管理写请求发送：

```text
X-Requested-With: XMLHttpRequest
```

不得添加 Authorization token、localStorage token 或 sessionStorage token。

## 状态与错误

认证状态使用项目内轻量 TypeScript 模块或 composable，维护：

```text
isAuthenticated
currentUser
login(username, password)
restoreSession()
logout()
clearSession()
```

统一 `ApiError` 保存公开消息和可选 HTTP 状态：

| 状态 | 前端行为 |
|------|----------|
| 400 | 显示参数或表单错误 |
| 401 | 清空会话并跳转登录页 |
| 403 | 跳转无权限页 |
| 404 | 显示资源不存在 |
| 409 | 显示数据冲突；词书删除提示改为禁用 |
| 422 | 显示业务条件不满足 |
| 500/502/503 | 显示通用服务错误 |

组件使用 `catch (error: unknown)` 和统一转换函数，不使用 `any`。

## 既有页面

- 教材管理保留搜索、分页、新增、编辑、状态切换和删除。
- 管理列表包含启用和禁用词书。
- 删除已有词义的词书遇到 409 时提示管理员禁用。
- 单词导入保留词书、单词、英式音标、美式音标、词性、释义和例句字段。
- 登录成功后两项既有功能必须保持可用。

## 构建

```bash
npm ci
npm run test:types
npm run build
```

Vite 输出到前端自己的 `dist/`，根目录 Dockerfile 在镜像构建阶段把该目录复制进 .NET Host 的 `wwwroot` 发布内容。
