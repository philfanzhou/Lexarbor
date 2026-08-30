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

## 范围纪律

一个 PR 只关闭一个可实施的 task issue。开始前完整阅读 issue 指向的实现、测试、公开文档、配置和部署路径；发现的既有缺陷是邻近债务，
必须单独开 issue 并在目标 issue 的“已知邻近问题”中链接。

一个 issue 只有在以下条件都满足时才可标记 `status: ready`：

1. 写清 `## 范围`（含明确排除项）和可逐条验证的 `## 验收标准`。
2. 安全或健壮性任务写清保证、不保证和调用方责任；不适用时明确写“无”。
3. 将要改动的实现、测试和契约已经完整读过。
4. 邻近债务已经各自开成 issue 并完成链接；确认没有时明确写“无”。
5. 前置 issue 已关闭，GitHub 原生依赖关系与标签一致。

实施中以 issue 范围为约束：不改变明确排除的行为，不顺手重构、改名、升级依赖或修复相邻缺陷；先找不变量，再在路径汇合处修复并覆盖相关输入集合。
成功路径以及适用的失败、取消、安全、认证和并发行为必须与实现一起测试。若验收标准确实要求越过原范围，先更新或拆分 issue。

Review 意见先分类：本 PR 新增/实质修改的缺陷在本 PR 修复；既有或仅与 diff 相邻的问题单独开 issue 并链接，不能顺手修复。只有既有缺陷导致本 PR
某条验收标准无法验证时，才可作为越界例外，并明确指出该条标准。PR 进入第三轮 review 时暂停写代码，逐 commit 审计范围；不能追溯到验收标准的改动移出并改为 follow-up issue。

## 安全与变更纪律

- 不得提交 token、密码、连接字符串、OIDC client secret、数据库文件、个人数据或真实凭据，也不得把它们写入日志、截图或测试输出。
- 上传、认证、管理 API、限流和容器权限的行为变化必须有针对性测试和英文文档说明。
- 只读分析不得修改文件；保留用户已有且与任务无关的改动，不回退、覆盖、提交或推送它们。
- 提交前检查文档链接、模板格式、secret 和仓库状态。

## 验证

按改动风险运行最小充分验证，并在 PR 中记录实际命令、结果和跳过原因：

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

## 合并 PR 后

1. 确认远端 PR 已合并，目标分支包含结果；检查并按完成程度处理唯一关联 issue，无关联时明确说明。
2. 确认远端工作分支已删除；保留时说明原因。
3. 用 `git worktree list` 检查并安全清理不再需要的 worktree，然后运行 `git worktree prune`。
4. 用 `git switch main && git merge --ff-only origin/main` 更新目标分支；若被其他 worktree 占用，先处理占用者并说明。
5. 通过 PR 状态或实质 diff 确认改动已进入目标分支后，再清理本地工作分支和远端跟踪引用；不要把 `git branch -d` 失败当作未合并证据。
6. 汇报 PR、issue、远端/本地分支、worktree 和验证结果；未完成的清理必须说明原因与后续动作。
