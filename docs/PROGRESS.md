# PROGRESS.md - 执行进度

> Codex 每完成一条任务在此追加 `- [x] 任务 N`

- [x] 任务 1（AppSettings 加 AI 配置属性）
- [x] 任务 2（新建 AI 模型与接口：ChatModels / IChatProvider / OpenAICompatibleProvider / ITextExplainService / LlmTextExplainService）
- [x] 任务 3（新建 ImmersiveSessionService 并接入 VoiceInputWindow）
- [x] 任务 4（SettingsWindow 加 AI 模型配置区）
- [x] 任务 5（QuickViewWindow 笔记编辑 + NoteService.UpdateNote 行级替换）
- [x] 任务 6（Release 构建 + 冒烟：构建/发布通过，测试连接真实 HTTP 待人工断网/错 Key 验证）

## QUEST-2（Phase 2：AI 核心）

- [x] 任务 1（新建 ChatSessionService：会话管理 + 20 条裁剪 + chat_history JSON 持久化；新建 PromptBuilder 动态提示词）
- [x] 任务 2（新建 AIDialogWindow 三板块对话框：翻译/搜索/问答，真流式 await foreach、连续对话、错误气泡、回填按钮）
- [x] 任务 3（速览面板条目右键菜单：AI 翻译/AI 搜索/复制/编辑/删除）
- [x] 任务 4（编辑态浮动工具条：400ms 延迟弹出，翻译/搜索/问答三按钮，超界靠边）
- [x] 任务 5（三处入口：标题栏 AI 问答按钮 + FloatBall 右键 AI 问答（AiAskRequested 事件）+ 托盘菜单 AI 问答，MainWindow 单例复用）
- [x] 任务 6（NoteService.AppendAiFill 追加回填（MD 只增不减、沉浸式锁定拦截）+ 对话框回填按钮联动）
- [x] Release 构建通过（0 警告 0 错误）+ 静态反作弊检查（Windows 层无 chat/completions、无 HttpClient；AppendAiFill 双处就位）
- [ ] 人工验收：配置 Key 后三板块对话/流式/连续追问/三入口/浮动工具条/回填（需真实 API Key，见 QUEST-2 §8）

## QUEST-3（Phase 3：面板完善）

- [x] 任务 1（NoteEntry 加 EditedContent 展示字段 + ParseNotes 识别【AI 释义】/【编辑】标记行，ref 精确关联、±60s 回退、独立成条 Tag 置空）
- [x] 任务 2（编辑语义升级为追加：AppendEdit 追加【编辑】行，移除 UpdateNote；QuickViewWindow/NoteEditWindow 保存走追加，展示层优先 EditedContent）
- [x] 任务 3（标题栏刷新按钮 + Window_Activated 焦点回归自动刷新）
- [x] 任务 4（CalendarWindow 日历热力图：单月翻页/今天/4 档热力色/点日期回选；NoteService.LoadNoteCounts 真实统计并排除软删除与标记行）
- [x] 任务 5（多选删除：NoteService.DeleteNote 行级物理删除+软删除记录+关联标记行一并删除；任一勾选即显示删除按钮）
- [x] 任务 6（标题栏布局改造：移除 DateText/DaySelector，新布局 标题/日历/刷新/AI 问答/导出/关闭）
- [x] 任务 7（输入面板语音输入：VoiceService + ImmersiveSessionService 互斥拦截，识别结果追加到光标处）
- [x] 任务 8（快捷键唤起修复：Activate + 延迟 Focus/Keyboard.Focus + IME 最佳努力；失焦立即隐藏主路径，_idleTimer 10s 兜底）
- [x] Release 构建通过（0 警告 0 错误）+ 静态反作弊检查（无 UpdateNote、无 DateText/DaySelector、删除走行级）
- [x] 存储层集成测试通过（26 项：追加/关联/统计/删除/沉浸块完整/旧格式兼容/锁定拦截；真实 deleted.json 备份还原哈希一致）
- [ ] 人工验收：面板操作/日历/语音/快捷键（见 QUEST-3 §8 手动验收清单，需真实 GUI 环境）

## QUEST-4（Phase 4：个性化收尾）

- [x] 任务 1（AppSettings 加 CustomIconPath 属性；外观字段集中分组）
- [x] 任务 2（AI 助手名称自定义：SettingsWindow MaxLength=10 输入框；QuickViewWindow 标题栏按钮 + FloatBall 右键菜单 + MainWindow 托盘菜单 三处入口同源读 AiAssistantName；UpdateAiName 公开方法支持即时同步；CreateTrayIcon 重建释放旧实例）
- [x] 任务 3（托盘图标自定义：SettingsWindow 外观区"选择图标"（png/jpg ≤1MB 校验 → 复制到 custom_icon.png → 保存路径）；MainWindow.CreateTrayIcon 优先加载自定义图标（System.Drawing.Image.FromFile → GetHicon），异常回退深灰方块；"恢复默认"按钮）
- [x] 任务 4（主题配色统一：ThemeColors 加 Heat0~Heat3 共 8 个热力色字段（Dark/Light 各一套）；CalendarWindow.CreateDayCell 4 档热力色 + 选中边框 + 今天边框全部走 ThemeService.GetColors()，硬编码色值全部移除）
- [x] 任务 5（正式 .ico：app_icon.svg SVG 设计 → Edge headless 转 256 PNG → Python struct 封装多尺寸 ICO（256/64/48/32/16，Vista+ PNG 压缩）；csproj 加 ApplicationIcon + PostBuildEvent 调用 embed_icon.py UpdateResource 嵌入 RT_GROUP_ICON + RT_ICON ×5；构建后 exe 资源验证含 5 尺寸图标组）
- [x] Release 构建通过（0 警告 0 错误）+ 静态反作弊检查（AiAssistantName 三处引用、CustomIconPath 三处出现、ApplicationIcon 配 Resources\app.ico、热力色无硬编码走 ThemeService）
