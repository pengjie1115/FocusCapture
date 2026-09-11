# Changelog

本项目所有重要变更都记录在此文件。格式遵循 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/)，版本号遵循 [Semantic Versioning](https://semver.org/lang/zh-CN/)。

## [Unreleased]

### Added
- 开源准备：README、LICENSE (MIT)、CONTRIBUTING、Issue 模板、GitHub Actions 自动构建
- 自动化检查点体系：`tests/`（快层 13 条：加密 / 时间解析）+ `tests/sync/`（慢层 59 条：双向同步 / 桶拆分 / 删除传播 / 断网降级 / 游标保护 / 换码重传）。双击对应 `run-*.bat` 运行，退出码 0 = 全部通过；全程跑在临时沙箱，不触碰真实数据

### Fixed
- 云同步：更换授权码后未用新密钥重写云端数据，会导致换码后（持新钥匙）反而读不到自己的数据 —— 现改为检测到换码即自动全量重传
- 云同步：解密失败时推送环节仍会推进游标，使未拉取的数据被增量过滤永久跳过（即便密钥随后对齐也捞不回）—— 现改为解密失败时回退游标

## [0.1.0] - 2026-08-03

首个公开版本。以下变更按 commit 时间线整理。

### Added
- 灵感捕获器核心：桌面悬浮球 + 全局热键 + 托盘图标
- 剪贴板自动捕获：复制即存，400ms 防抖过滤瞬态写入，自动去重
- 沉浸式语音输入：纯 C# 本地语音识别（sherpa-onnx + FireRedASR2 CTC INT8），离线可用
- 灵感速览：最近 3 天笔记切换查看，支持长笔记折叠与回收站
- 一键导出：Markdown / JSON / TXT / Word（.docx 手写 OOXML，零额外依赖）
- 笔记来源窗口记录与标签

### Changed
- 语音识别从 Python 子进程（asr_server.py）迁移为纯 C# SDK，消除 Python 环境依赖
- 剪贴板捕获增加防抖与多段落完整捕捉
- 关闭 IL 裁剪，修复单文件 exe 启动时 TypeLoadException (RegisterDragDrop)

### Fixed
- 语音识别乱码问题
- 剪贴板瞬态写入（"选中即复制"类工具）导致的误捕获

[Unreleased]: https://github.com/pengjie1115/FocusCapture/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/pengjie1115/FocusCapture/releases/tag/v0.1.0
