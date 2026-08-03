# 贡献指南

感谢你对 FocusCapture 感兴趣！任何形式的贡献都欢迎：修 bug、加功能、写文档、做测试、提建议。

## 开发环境

- Windows 10/11
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- 推荐 IDE：Visual Studio 2022 或 JetBrains Rider

## 本地构建

```bash
dotnet restore
dotnet build -c Debug
dotnet run
```

发布单文件 exe：

```bash
dotnet publish -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

> 注意：Release 构建**禁用 IL 裁剪**（`PublishTrimmed=false`），这是刻意的——WPF/WinForms 的 COM 互操作类型无法被静态裁剪分析，裁剪会导致启动时 TypeLoadException。请勿开启裁剪。

## 提 Issue

- **Bug 报告**：请使用 [Bug 报告模板](.github/ISSUE_TEMPLATE/bug_report.md)，务必包含：复现步骤、期望行为、实际行为、系统环境、崩溃日志（`%LocalAppData%\FocusCapture\startup-error.log`）
- **功能建议**：请使用 [功能建议模板](.github/ISSUE_TEMPLATE/feature_request.md)

## 提 PR

1. Fork 本仓库并创建功能分支：`git checkout -b feat/my-feature`
2. 提交信息遵循 [Conventional Commits](https://www.conventionalcommits.org/zh-hans/)：
   - `feat: 新功能`
   - `fix: 修复问题`
   - `docs: 文档变更`
   - `refactor: 重构（不改变行为）`
   - `chore: 杂项（构建、依赖等）`
3. 保持变更聚焦：一个 PR 解决一个问题
4. 描述清楚改动内容和测试方式

## 代码规范

- 遵循项目现有风格（命名、注释、错误处理模式）
- 关键逻辑添加 XML 注释（参考 `Services/` 现有注释风格）
- 涉及剪贴板/热键/COM 互操作的部分，务必在真实 Windows 环境手动验证

## 隐私约定

本项目数据全部存储在本机。任何涉及网络传输的改动，必须先在 Issue 中讨论并征得同意。
