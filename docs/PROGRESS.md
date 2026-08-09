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
