# Changelog

本项目所有重要变更都记录在此文件。格式遵循 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/)，版本号遵循 [Semantic Versioning](https://semver.org/lang/zh-CN/)。

## [Unreleased]

### Added
- 开源准备：README、LICENSE (MIT)、CONTRIBUTING、Issue 模板、GitHub Actions 自动构建

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
