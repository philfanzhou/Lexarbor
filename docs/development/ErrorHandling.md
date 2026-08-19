# 错误处理规范

## HTTP 状态码使用规则

HTTP 服务中必须使用标准的 HTTP 状态码：

| 状态码 | 使用场景 | 示例 |
|---------|----------|------|
| `400 Bad Request` | 请求、JSON 或分页参数无效 | 必填字段为空、size 超过 100 |
| `401 Unauthorized` | 登录失败或 JWT 缺失/无效 | 匿名访问管理接口 |
| `403 Forbidden` | 已认证但没有管理员角色，或 Cookie 写请求缺少同源头 | 普通 Identity 用户访问管理接口 |
| `404 Not Found` | 请求的资源不存在 | 单词、词义或词书不存在 |
| `409 Conflict` | 数据唯一性、归属或删除冲突 | 删除已有词义的词书 |
| `422 Unprocessable Entity` | 业务前置条件不满足 | 词书禁用、题目候选不足 |
| `429 Too Many Requests` | 匿名端点超出该客户端地址的请求上限 | 登录爆破、公开 API 被单一地址刷取 |
| `500 Internal Server Error` | 服务内部错误 | 数据库异常、未预期的错误 |
| `502 Bad Gateway` | Identity 不可达或响应无效 | 管理员登录代理失败 |
| `503 Service Unavailable` | 生产登录配置缺失 | 未配置 AppId/AppSecret |

## 错误信息规范

1. 错误信息使用英文，便于国际化
2. 错误信息应简洁明确，不包含技术细节
3. 同一类错误在各服务中使用相同的措辞

## 响应信封格式

所有 HTTP 端点统一使用 `VocabularyHttpResponse` 助手类返回响应：

- 成功：`{ "success": true, "data": value }` 或 `{ "success": true }`
- 失败：`{ "success": false, "message": "..." }`

## 参数验证

参数验证应在 HTTP 端点入口完成。`page` 或 `size` 为 0 时使用兼容默认值 1 和 20；负数、`size>100` 或分页算术溢出返回 400。

统一异常中间件负责把领域异常、数据库冲突、JSON 错误和未预期异常映射到上述状态码。端点不得返回 `ex.Message`；500 响应固定使用通用消息。

## 日志规范

- 使用结构化日志占位符，不要使用字符串插值
- 异常对象必须传入：使用 `LogError(ex, ...)` 而非 `LogError(ex.Message, ...)`
- 预期内的 NotFound 使用 Warning 级别
- 不记录密码、JWT、Cookie、AppSecret、连接字符串、SQL 或完整 Identity 响应
