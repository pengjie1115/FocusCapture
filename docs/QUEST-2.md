# QUEST-2 — Phase 2：AI 核心（三板块对话框 + 连续对话 + 入口 + 回填）

> Codex 执行手册 | 预估 4-6 天 | 验收通过后再进 QUEST-3

## 0. 衔接到 QUEST-1

**前提**：QUEST-1 已完成且验收通过。你接手时：
- `Services/AI/` 已有：`IChatProvider`（CompleteAsync/StreamAsync/TestConnectionAsync）、`OpenAICompatibleProvider`、`ChatModels`（ChatMessage/ChatRoles/ExplainMode）、`ITextExplainService`（Translate/Search/Ask 三模式）、`LlmTextExplainService`
- `AppSettings` 已有：`AiBaseUrl`（默认 https://apihub.agnes-ai.cn/v1）、`AiApiKey`、`AiModel`（默认 agnes-2.5-flash）、`AiAssistantName`
- 设置页已有"AI 模型"区（BaseUrl/Key/模型名/测试连接）
- 速览面板笔记可双击编辑（行内覆盖临时语义）、沉浸式锁定生效
- `ImmersiveSessionService` 已存在（IsActive/ActiveTimestamp/IsLocked）

**本阶段不要**：改动 QUEST-1 已验收的接口签名（IChatProvider/ITextExplainService 保持不变）；不改笔记编辑的现有实现。

## 1. 项目背景

**这是什么**：在速览面板长出三个 AI 入口——**AI 翻译**（选中/整条笔记翻译释义）、**AI 搜索**（解释笔记内容，纯 LLM 无联网）、**AI 问答**（全局入口，问任何问题）。三者都是对话框 + 连续对话。

**为什么做**：用户捕捉笔记常遇到陌生单词/外文段落，要就地理解。核心体验是"快"——选中即问，流式回答。

**不是什么**：不是做联网搜索（本阶段不做 tool calling）；不是做向量检索；AI 问答不是"针对某条笔记"的，是全局入口；不做多用户/账号。

## 2. 全局约束

### 必做
- 全部走 QUEST-1 的 `IChatProvider` / `ITextExplainService` 接口，**禁止**在窗口代码里 new HttpClient
- 流式输出必须真流式（`StreamAsync`），不得 await 完再一次性显示
- 连续对话 = 会话内消息列表逐轮累加；**上下文裁剪**：保留最近 20 条消息（10 轮）+ 系统提示词
- 三个板块**各自独立上下文**（互不串味）
- 对话框历史持久化到 `%AppData%\FocusCapture\chat_history\{yyyyMMdd_HHmmss}.json`
  - **实现层决策说明**：PRD 原写 SQLite，但项目无 SQLite 依赖，为保持单文件发布简单，本阶段用 JSON 文件存储对话历史（数据量小，JSON 足够）。如需 SQLite 后续单独升级。
- 回填 = **追加**新行（`AppendAiFill`），禁止覆盖原行；回填受 `ImmersiveSessionService.IsLocked` 约束
- 构建通过：`dotnet build -c Release`

### 禁止（违反就算失败）
- **不许**在窗口层直接 new `OpenAICompatibleProvider` 之外的 HTTP 调用
- **不许**把提示词写死成"翻译这个单词"（必须动态适配：短词→词典式释义，长文→整段翻译，判定阈值见任务清单）
- **不许**用 `UpdateNote`（覆盖）实现回填——回填必须 `AppendAiFill`（追加）
- **不许**假装流式（先攒完再显示）
- **不许**对话框无历史（每次请求只发当前一条消息 = 无连续对话）
- **不许**让浮动工具条选中即弹（必须延迟 300-500ms）

## 3. 项目目录结构（本期新增文件标 ★）

```
FocusCapture/
├── QUEST-2.md                 # 本文件
├── Services/
│   ├── NoteService.cs         # 改：加 AppendAiFill
│   ├── AI/
│   │   └── (已有接口，不改)
│   └── ChatSessionService.cs  ★ 新建：会话管理 + 裁剪 + 持久化
└── Windows/
    ├── AIDialogWindow.xaml(.cs) ★ 新建：三板块对话框
    ├── QuickViewWindow.xaml(.cs) # 改：右键菜单 + 浮动工具条 + 标题栏按钮
    ├── FloatBall.xaml.cs      # 改：右键菜单加 AI 问答（新事件 AiAskRequested）
    └── MainWindow.xaml.cs     # 改：订阅 AiAskRequested + 托盘菜单加 AI 问答
```

## 4. 反作弊（点名具体偷懒姿势，用了就算失败）

1. **窗口层直接调 HTTP** — 在 AIDialogWindow 里 `HttpClient.PostAsync("...chat/completions", ...)`。
   验证方式：`grep -rn "chat/completions" Windows/ | grep -v "Services/AI"` → 必须无输出。

2. **提示词写死单词** — `"你是一个翻译助手，翻译这个单词：{text}"` 单模板走天下。
   验证方式：手动测段落翻译 → 若输出"单词释义"而非整段翻译 = 失败。判定阈值必须走 `PromptBuilder` 逻辑。

3. **回填用覆盖** — 对话框里调 `UpdateNote(entry, entry.Content + result)` 或直接改行。
   验证方式：`grep -n "AppendAiFill" Services/NoteService.cs Windows/AIDialogWindow.xaml.cs` → 回填路径必须调用它；手动验证 MD 文件是**新增一行**而非替换原行。

4. **假流式** — `var full = await provider.CompleteAsync(...); AppendToUI(full);`。
   验证方式：UI 上回答是逐字出现的（打字机效果）；代码审查 `AIDialogWindow` 发送逻辑必须 `await foreach` 消费 `StreamAsync`。

5. **无连续对话** — 每次请求只发 `[当前消息]`，不带历史。
   验证方式：对话中问"那第二点呢？"→ 若模型不知道上文 = 失败。代码审查：请求必须带 `_messages` 全量（裁剪后）。

6. **浮动工具条即时弹出** — 选中文字瞬间弹工具条，打断复制操作。
   验证方式：手动选中一段文字 → 工具条**延迟约 400ms** 才出现；快速选中+复制时不弹。

## 5. 取舍

功能正确 > 体验流畅 > 代码整洁 > 性能优化 > UI 美观

- 功能正确排第一：连续对话必须真的连贯、回填必须真的追加、锁定必须真的拦住。
- 体验流畅次之：流式是刚需，宁可代码丑一点也要逐字输出。
- UI 先有再美：对话框用默认控件样式，别花时间调像素。

## 6. 未知处理

1. **流式解析卡住**（SSE 分块不完整）→ 缓冲行内内容，遇到完整 `\n` 再解析；`[DONE]` 兜底超时 30s 结束。
2. **Agnes 限流/超时** → 给用户可见错误提示（"模型响应超时，请重试"），保留已收到的内容；不自动重试。
3. **上下文超过模型窗口** → 裁剪逻辑已写死 20 条，若仍超长（单条超长笔记），把最老的 user 消息截断到 2000 字符。
4. **一个任务卡住超过 30 分钟** → 写 BLOCKED.md，**跳过做下一个**。

## 7. 任务清单

### 第一步：ChatSessionService（会话管理）

> 操作位置：`Services/ChatSessionService.cs`

```csharp
namespace FocusCapture.Services;

/// <summary>AI 对话会话：消息列表 + 裁剪 + 持久化（JSON 文件）</summary>
public class ChatSessionService
{
    private const int MaxMessages = 20; // 保留最近 20 条（约 10 轮）
    private readonly List<ChatMessage> _messages;
    private readonly string _sessionFile;
    private readonly string _systemPrompt;

    public ChatSessionService(ExplainMode mode, string? noteContext = null, string? noteContent = null);
    public IReadOnlyList<ChatMessage> Messages => _messages;
    public void AddUser(string content);       // 追加 user 消息，裁剪
    public void AddAssistant(string content);  // 追加 assistant 消息，裁剪
    public void Save();                        // 持久化到 chat_history/
    public static ChatSessionService? Load(string filePath); // 读取历史（Phase 2 可选：新建即可，读历史留接口）
}
```

**system prompt 组装**（写死）：
- `Translate`：`你是翻译与释义助手。输入为单词/短语时，输出：词性、释义、常见搭配、例句；输入为段落时，输出整段中文翻译并简要解释。`（+ 若 noteContext 存在，追加 `当前笔记内容：{noteContent}`）
- `Search`：`你是笔记解释助手。解释用户笔记中的关键概念、术语或背景。`（+ 当前笔记上下文）
- `Ask`：`你是 AI 助手，回答用户的问题。`

**裁剪规则**：`_messages` 超过 MaxMessages 时，丢弃最早的非 system 消息（system 永远保留在第一位）。

**持久化**：`Save()` 写 JSON（含 mode、systemPrompt、messages、时间戳）到 `chat_history/{sessionId}.json`；`sessionId` 构造时生成（`DateTime.Now:yyyyMMdd_HHmmss`）。

**PromptBuilder（动态适配）**：`Services/AI/PromptBuilder.cs` ★ 新建，静态方法：

```csharp
public static string BuildTranslatePrompt(string text)
{
    // 短词（≤30 字符 且 无空格）→ 词典式释义
    // 否则 → 整段翻译
}
```

### 第二步：AIDialogWindow（三板块对话框）

> 操作位置：`Windows/AIDialogWindow.xaml` + `.cs`

**一个窗口，三个模式**（构造器参数 `ExplainMode mode` + 可选 `NoteEntry? targetNote` + 可选 `string? selectedText`）：

- 窗口标题：`AI 翻译` / `AI 搜索` / `AI 问答`（**注意：标题显示用 `AppSettings.AiAssistantName` 仅对问答模式生效？不——三个板块名字是功能名，保持"AI 翻译/AI 搜索/AI 问答"；`AiAssistantName` 自定义只改入口文案，Phase 4 处理。本阶段标题写死功能名即可，但入口按钮文案读 `AiAssistantName`）**
  - 简化决策：本阶段标题栏写功能名（AI 翻译/AI 搜索/AI 问答），`AiAssistantName` 的绑定放 Phase 4。
- 布局（自上而下）：消息列表（`ListBox`/`ItemsControl`，自动滚动到底）→ 输入框（TextBox）→ 发送按钮
- 打开时：若 `selectedText` 非空 → 自动以"选中内容"发起第一轮（Translate/Search 模式）；`targetNote` 存在时记录为回填目标
- 发送逻辑（写死）：
  1. 用户消息入 `_session.AddUser(text)`，UI 追加气泡
  2. `await foreach` 消费 `provider.StreamAsync(_session.Messages)`，逐块追加到当前 assistant 气泡（流式）
  3. 完成后 `_session.AddAssistant(完整文本)` + `_session.Save()`
  4. Translate/Search 模式下，assistant 气泡底部附"回填到笔记"按钮（仅当 targetNote 非空）
  5. Ask 模式无回填按钮
- 异常：捕获 → 气泡内红字错误提示，不崩溃
- 会话历史：每次 `Save()`；窗口关闭时最后 `Save()` 一次

**Provider 获取**：`AIDialogWindow` 构造时从 `AppSettings` 构造 `OpenAICompatibleProvider`（`AiBaseUrl`/`AiApiKey`/`AiModel`），**Key 为空 → 打开时弹提示"请先在设置中配置 AI 模型"并关闭**。

### 第三步：入口一 — 速览面板条目右键菜单

> 操作位置：`Windows/QuickViewWindow.xaml` + `.cs`

- 为笔记条目（`NotesList` 的 ItemContainerStyle 或条目模板）加 `ContextMenu`：
  - `AI 翻译` → 打开 AIDialogWindow(Translate, targetNote=该条目)
  - `AI 搜索` → 打开 AIDialogWindow(Search, targetNote=该条目)
  - 分隔线
  - `复制`（复制内容到剪贴板）
  - `编辑`（触发现有双击编辑逻辑）
  - `删除`（触发现有删除逻辑——本阶段不动删除，只挂已有逻辑）
- **注意**：条目右键菜单不含"AI 问答"（全局入口在标题栏/托盘）

### 第四步：入口二 — 编辑态浮动工具条

> 操作位置：`Windows/QuickViewWindow.xaml` + `.cs`

- 编辑态（笔记 TextBox 获得焦点）时，监听 `SelectionChanged`：
  - 选中文字非空 → 启动 `DispatcherTimer`（400ms）
  - 400ms 内用户继续操作（重新选中/取消）→ 重置计时器
  - 计时器触发 → 在选中位置附近显示浮动工具条（3 个按钮：翻译/搜索/问答）
  - 点击按钮 → 打开 AIDialogWindow(对应模式, targetNote=当前条目, selectedText=选中文字)，关闭工具条
  - 文字取消选中/失去焦点 → 关闭工具条
- 工具条位置：基于选中文字的屏幕坐标（`TextBox.GetRectFromCharacterIndex` + `PointToScreen`），超界时靠边

### 第五步：入口三 — 标题栏 + 悬浮球右键 + 托盘菜单（三处）

> 操作位置：`Windows/QuickViewWindow.xaml`（标题栏）、`Windows/FloatBall.xaml.cs`（悬浮球右键）、`MainWindow.xaml.cs`（托盘）

- 速览面板标题栏右侧加"AI 问答"按钮（在导出按钮旁）→ `AIDialogWindow(Ask)`（无 targetNote）
- **桌面悬浮球右键菜单**（`FloatBall.xaml.cs` 的 `Ball_MouseRightButtonDown` 中 `ContextMenu`，现有项：灵感速览/沉浸记录/设置）：加一项"AI 问答"→ 触发 `FloatBall` 新事件 `public event Action? AiAskRequested;`，由 `MainWindow` 订阅（`CreateFloatBall` 处，同 `QuickViewRequested` 模式）打开 `AIDialogWindow(Ask)`
- **托盘图标右键菜单**（`MainWindow.CreateTrayIcon` 的 `cm.Items`）：在"灵感速览"下加一项"AI 问答"→ 打开 `AIDialogWindow(Ask)`
- **注意**：`MainWindow` 持有打开的对话框引用（同 `_inputWindow` 模式），单例复用

### 第六步：回填机制

> 操作位置：`Services/NoteService.cs` + `Windows/AIDialogWindow.xaml.cs`

**NoteService 加方法**（追加语义，禁止覆盖）：

```csharp
/// <summary>AI 回填：向笔记文件追加一条带标记的新行（MD 只增不减）</summary>
public bool AppendAiFill(NoteEntry entry, string fillText)
```

实现要点：
- 新行格式：`- [{entry.Timestamp:yyyy-MM-dd HH:mm}] {原文内容}\n【{来源标记}】{fillText}` —— 不，**新行只含回填内容**，格式：
  `- [{DateTime.Now:yyyy-MM-dd HH:mm}] 【AI 释义】{fillText} — 来源: AI 回填`
- 写入位置：与 entry 同文件（当天灵感文件或标签文件，按 entry 定位），**追加到文件末尾**
- 若 `ImmersiveSessionService.IsLocked(entry.Timestamp)` → 返回 false（调用方弹提示"沉浸式输入进行中，暂不可回填"）
- 来源标记：翻译/搜索统一 `【AI 释义】`（Phase 3 再细化标记体系）

**AIDialogWindow 回填按钮**：点击 → `AppendAiFill(targetNote, 该条 assistant 完整文本)` → 成功按钮变"已回填"禁用；失败弹提示。

## 8. 验收标准

> 按顺序执行，每条必须与期望一致。

### 构建与静态检查

```bash
cd "项目根目录"
dotnet build -c Release
# 期望：Build succeeded

grep -rn "chat/completions" Windows/ | grep -v "Services/AI"
# 期望：无输出

grep -n "AppendAiFill" Services/NoteService.cs Windows/AIDialogWindow.xaml.cs
# 期望：两处都出现
```

### 手动验收（三板块对话）

1. 未配置 Key 时打开任一 AI 对话框 → 期望：提示"请先在设置中配置 AI 模型"
2. 速览面板右键一条**英文单词**笔记 → AI 翻译 → 期望：对话框出现，第一轮自动发起，**流式逐字输出**，结果含词性/释义/例句（词典式）
3. 右键一条**英文段落**笔记 → AI 翻译 → 期望：输出整段中文翻译（非单词释义）
4. 右键一条技术笔记 → AI 搜索 → 期望：解释笔记中的关键概念
5. 连续追问（"再详细点""第一点什么意思"）→ 期望：模型记得上文（连续对话生效）
6. 标题栏"AI 问答"按钮 → 对话框 → 随便问 → 期望：正常回答，**无笔记绑定**，回复无回填按钮
7. **悬浮球右键菜单"AI 问答"** → 期望：打开问答对话框（FloatBall 悬浮球窗口右键，非托盘）
8. **托盘菜单"AI 问答"** → 期望：打开问答对话框

### 手动验收（编辑态浮动工具条）

8. 双击笔记进编辑 → 选中一段文字 → 约 400ms 后浮动工具条出现
9. 快速选中后立刻点别处（取消选中）→ 期望：工具条不出现
10. 工具条点"翻译" → 期望：对话框打开，翻译的是**选中的文字**而非整条笔记

### 手动验收（回填）

11. 翻译完成后点"回填到笔记" → 期望：按钮变"已回填"；MD 文件**新增一行**（`【AI 释义】...`），**原行未变**
12. 沉浸式输入进行中回填 → 期望：弹提示拦截
13. 对话历史：`%AppData%\FocusCapture\chat_history\` 出现 JSON 文件，内容含全部消息

### 回归验收

14. QUEST-1 的功能仍正常：测试连接、双击编辑、沉浸式锁定

## 9. 交付

完成后执行：

```bash
git add -A
git commit -m "quest2: AI 三板块对话框 + 连续对话 + 入口 + 回填机制"
```

**不要合并到 main。** 验收通过后再进 QUEST-3。
