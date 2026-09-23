# Lexarbor 协作规范

Lexarbor 是自托管的词汇目录与测验服务：.NET 10 API、Vue 3 管理端、SQLite 存储和单容器部署共同组成一个产品。

## 维护方式

- 本文件是 AI 协作流程、review 政策和项目边界的统一入口。
- Codex 直接读取本文件；Claude Code 通过根目录 `CLAUDE.md` 导入本文件。
- `CONTRIBUTING.md` 保留面向所有贡献者的仓库入口和命令；若工程政策变化，同时更新它与本文件，避免两处描述冲突。
- `.agent` 或其他工具目录可以补充工具专属规则，但不得覆盖本文件的范围、测试、安全和 review 约束。

## 文档与沟通语言

- 流程与约束文档、GitHub issue/PR 正文和 review 全程使用中文；Issue 标题使用中文。
- PR 标题使用英文 conventional commit 格式（`feat:` / `fix:` / `docs:` / `test:` / `refactor:` / `chore:` 等）。
- 面向使用者的 `README.md`、`docs/`、API 错误消息和公开契约文字保持英文；代码标识符和 commit message 保持英文。
- 引用代码、命令、路径、JSON 字段和诊断码时保持原样。

## 项目边界与架构

- `src/Lexarbor.Domain` 是不依赖宿主和数据库的领域层；不要把 ASP.NET Core、EF Core 或 SQLite 细节引入其中。
- `src/Lexarbor.Database` 负责 EF Core/SQLite 持久化，`src/Lexarbor.Service` 负责应用服务，`src/Lexarbor.Host`
  负责 HTTP、认证、配置和宿主组合；新增引用必须保持从领域到基础设施/宿主的单向依赖。
- SQLite 是当前唯一的业务数据库；修改实体、迁移、初始化种子、持久化一致性或备份语义时，必须同步测试和部署文档。
- 公共 API 路由、JSON 字段、认证/管理员角色、限流、种子词汇和容器持久化目录都属于兼容性契约，除非 issue 明确批准不得静默改变。
- 管理端通过宿主提供的 HTTP API 工作；不得把服务端 token、client secret 或管理凭据暴露给浏览器。

## 安全与变更纪律

- 不得提交 token、密码、连接字符串、OIDC client secret、数据库文件、个人数据或真实凭据，也不得把它们写入日志、截图或测试输出。
- 上传、认证、管理 API、限流和容器权限的行为变化必须有针对性测试和英文文档说明。
- 提交前检查文档链接、模板格式、secret 和仓库状态。

## 验证

按改动风险运行最小充分验证：

```bash
dotnet restore Lexarbor.sln
dotnet build Lexarbor.sln --configuration Release --no-restore
dotnet test Lexarbor.sln --configuration Release --no-build --no-restore

cd frontend
npm ci
npm run test:types
npx playwright install chromium
npm run test:e2e
```

触及容器、持久化或启动脚本时，还需运行 `docker build -t lexarbor:ci .` 和 `bash .github/scripts/test-container.sh lexarbor:ci`。
