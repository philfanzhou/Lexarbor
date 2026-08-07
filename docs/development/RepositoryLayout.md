# 仓库结构与文件归属

本仓库采用单仓库、前后端分离开发、单镜像交付的结构。目录按“生产源码、测试、文档、运维与 GitHub 治理”划分，避免构建产物和工具文件混入业务代码。

```text
Lexarbor/
├── .config/                 .NET 本地工具清单
├── .github/                 Actions、Dependabot、Issue/PR 模板及 CI 专用脚本
├── docs/                    架构、ADR、前端规格、开发和部署文档
├── frontend/                Vue 管理前端及其类型测试、浏览器测试
├── scripts/                 使用者或运维人员主动执行的脚本
├── src/Lexarbor.Domain/     领域模型、规则与仓储抽象
├── src/Lexarbor.Database/   EF Core、SQLite、迁移、仓储实现与种子数据
├── src/Lexarbor.Service/    HTTP 契约、DTO、转换和异常映射
├── src/Lexarbor.Host/       组合根、认证、持久化配置与应用入口
├── tests/                   与生产项目对应的 .NET 测试项目
├── Directory.Build.props    所有 .NET 项目共享的编译设置
├── Directory.Packages.props NuGet 版本的唯一事实来源
├── Dockerfile               前后端生产镜像构建入口
└── Lexarbor.sln             根目录解决方案入口
```

## 放置规则

- 可部署或可被生产项目引用的 C# 代码放在 `src/`，目录名与项目名一致。
- .NET 单元和集成测试放在根目录 `tests/`；前端专属测试保留在 `frontend/` 内。
- 所有长期维护的说明集中到 `docs/`。根目录只保留 GitHub 能识别的社区文件和构建入口。
- `.github/scripts/` 只服务于 GitHub Actions；用户主动执行的脚本放在 `scripts/`。
- 前端构建输出只进入 `frontend/dist/`，容器构建时再复制到 Host 的 `wwwroot`，不在仓库根目录产生静态构建目录。
- NuGet 包版本只在 `Directory.Packages.props` 声明；各 `.csproj` 只表达依赖关系和项目特有元数据。
- 新增架构决策使用 `docs/adr/ADR-NNN-title.md`，不要把临时设计笔记放在根目录。

## 不应提交的内容

数据库、运行时配置、密钥、`bin/`、`obj/`、`TestResults/`、`frontend/dist/`、Playwright 报告和本地 IDE 配置均由忽略规则排除。需要分发的示例配置应去除秘密，并采用明确的示例文件名。
