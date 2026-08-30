# Changelog

本项目所有重要变更都记录在此文件。格式遵循 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/)，版本号遵循 [Semantic Versioning](https://semver.org/lang/zh-CN/)。

## [Unreleased]

## [0.2.0] - 2026-08-30

### Added
- 云端同步（WebDAV + 端到端加密）：AES-256-GCM 加密、授权码即钥匙、30 秒自动合并窗口、失败退避自愈、设置窗同步配置、灵感速览一键上传/下载
- 待办提醒：待办数据模型 + 自然语言时间识别 + 定时提醒弹窗 + 每日汇总 + 分组汇总窗 + 悬浮球角标 + 已办徽标/撤销 + 来源筛选
- 设置面板大改版：左侧板块导航 + 全局搜索 + 可拖拽占比 + 唤出热键（Ctrl+Alt+S）
- 输入面板 v3.6：自动隐藏计时重启 + 记住拖动位置（不再吸回默认位置）
- 回收站增强：CheckBox 多选 + 全选/反选 + 批量恢复
- 灵感速览增强：笔记导入 + 区间筛选 + 全局查找 + 重复笔记红色标记
- AI 助手名称 / 托盘图标自定义 + 主题配色统一 + 正式应用图标
- 回填升级：气泡可拖选部分文字回填，支持追加到原笔记或存为新笔记
- 开源准备：README、LICENSE (MIT)、CONTRIBUTING、Issue 模板、GitHub Actions 自动构建

### Changed
- 日期选择弃用系统 DatePicker，改为内置日历（适配深色主题）
- 输入面板语音入口移除，语音唤起方案调整

### Fixed
- 回收站闪退根治（Run.Text 显式 OneWay 绑定 + 全局兜底不再强制 Shutdown）
- 删除笔记先写回收站成功再删行（写失败中止，防数据永久丢失）
- 全屏按钮点不到（命中测试根因修复）
- AI 对话框打不开（三处加固：已关窗口重建 / 选区丢失兜底 / 流式状态复位）
- 日历弹窗点击外部自动收起 + 今天按钮跳转
- 待办角标点击崩溃（彻底杜绝）
- 云同步正确性：回声死循环 / 软删增量遗漏 / 密钥盐冲突 / 删除分钟精度误判
- 构建：禁用 SourceLink git 查询 + 锁定 RID win-x64 瘦身（修 Microsoft.Build.Tasks.Git 兼容）

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

[Unreleased]: https://github.com/pengjie1115/FocusCapture/compare/v0.2.0...HEAD
[0.2.0]: https://github.com/pengjie1115/FocusCapture/compare/v0.1.0...v0.2.0
[0.1.0]: https://github.com/pengjie1115/FocusCapture/releases/tag/v0.1.0
