# QUEST-1 — Phase 1：基础设施（Provider 抽象层 + 模型配置 + 笔记编辑）

> Codex 执行手册 | 预估 3-5 天 | 验收通过后再进 QUEST-2

## 1. 项目背景

**这是什么**：FocusCapture v0.1.0 是 WPF .NET 8 本地专注力工具（悬浮球 + 剪贴板捕捉 + 灵感速览 + 沉浸式语音输入 + 导出），笔记以**行式 MD 文件**存储（`%Documents%\FocusCapture\灵感_yyyy-MM-dd.md`，每行 `- [yyyy-MM-dd HH:mm] 内容 — 来源: xx`；带标签的进 `{tag}.md`；沉浸式长笔记进 `## 沉浸记录` 块）。

**为什么做**：v2.0 要给灵感速览注入 AI 能力（翻译/搜索/问答）。本阶段只做地基：LLM 调用抽象、模型配置、笔记编辑。做完了用户能配好 Key、发起一次 AI 对话、能双击编辑笔记——但这些还不是面向用户的完整功能，Phase 2 才长出真面目。

**不是什么**：不是做账号系统、不是做云同步、不是做本地词典、不是做联网搜索（本阶段及本阶段之后都不做）。不重构 v0.1 的笔记存储结构。

**技术栈拍板**：延续 WPF .NET 8 + WinForms（托盘），不改框架；LLM 走 OpenAI 兼容 Chat Completions 协议（Agnes 2.5 Flash 默认，国内节点 `https://apihub.agnes-ai.cn/v1`）；HTTP 用 `HttpClient`（.NET 内置，无额外依赖）。

**开发方式**：AI 代劳（Codex 执行，人类验收）。

## 2. 全局约束

### 必做
- 所有新代码放对应命名空间 `FocusCapture.Services.AI`（新目录）或现有对应目录，沿用项目风格（类不强制接口，但 AI 层必须接口）
- 配置一律进 `AppSettings`（`Models/AppSettings.cs`，序列化走 `AppJsonContext` source generator，新增属性会被自动序列化，无需改 AppJsonContext）
- 业务代码（窗口层）只依赖接口 `IChatProvider` / `ITextExplainService`，不得直接 new HttpClient 调 LLM
- 构建必须通过：`dotnet build -c Release`
- 项目 Release 用 `PublishTrimmed=false`（`FocusCapture.csproj` 已有），**不得改回裁剪**；新增包需评估单文件发布兼容性

### 禁止（违反就算失败）
- **不许**在业务代码里直接 `HttpClient.PostAsync` 调 LLM 端点绕过接口
- **不许**把 API Key / BaseUrl / 模型名硬编码进任何 `.cs` 文件（除 `AppSettings.cs` 里的默认值）
- **不许**用 `File.WriteAllText` 重写整个 MD 文件来更新一条笔记（会破坏其他行、标签文件、沉浸记录块）——更新单行必须用行级定位替换
- **不许**用固定时间戳字符串判断"沉浸式锁定"，必须走 `ImmersiveSessionService`
- **不许**"假装成功"：测试连接按钮必须真实发 HTTP 请求

## 3. 项目目录结构（本期新增文件标 ★）

```
FocusCapture/
├── TASK.md                    # 总规划（只读）
├── QUEST-1.md                 # 本文件
├── Models/
│   └── AppSettings.cs         # 改：加 AI 配置属性
├── Services/
│   ├── NoteService.cs         # 改：加 UpdateNote 方法
│   ├── ImmersiveSessionService.cs ★ 新建：沉浸式会话状态
│   └── AI/
│       ├── ChatModels.cs      ★ 新建：ChatMessage / ExplainMode
│       ├── IChatProvider.cs   ★ 新建：LLM 抽象接口
│       ├── OpenAICompatibleProvider.cs ★ 新建：OpenAI 兼容实现
│       ├── ITextExplainService.cs ★ 新建：查词/解释抽象
│       └── LlmTextExplainService.cs ★ 新建：LLM 实现
└── Windows/
    ├── SettingsWindow.xaml(.cs) # 改：加 AI 模型配置区
    └── QuickViewWindow.xaml(.cs) # 改：笔记编辑
```

## 4. 反作弊（点名具体偷懒姿势，用了就算失败）

1. **绕过接口直接调 API** — 验收只看「能对话」，最省事的写法是在窗口代码里 `HttpClient.PostAsync("https://apihub.agnes-ai.cn/v1/chat/completions", ...)`。
   验证方式：`grep -rn "chat/completions" Windows/ MainWindow.xaml.cs | grep -v "Services/AI"` → 必须无输出。

2. **Key 硬编码** — 在某个窗口里写 `new OpenAICompatibleProvider("sk-xxx", ...)` 或把 Key 写死在默认值里。
   验证方式：`grep -rn "sk-\|api_key\|ApiKey" --include="*.cs" . | grep -v "AppSettings.cs\|OpenAICompatibleProvider.cs"` → 必须无输出。

3. **UpdateNote 重写全文件** — `File.ReadAllText` 后 `Replace` 再 `WriteAllText` 整文件，会把 `## 沉浸记录` 块或标签文件其他内容弄坏。
   验证方式：手动在 `灵感_当天.md` 里造一条沉浸记录 + 一条普通笔记 → 编辑普通笔记 → 检查沉浸记录行仍在。

4. **沉浸锁定写死时间** — `if (entry.Timestamp.Hour == 15 && entry.Timestamp.Minute == 30)` 之类。
   验证方式：`grep -rn "ImmersiveSessionService" --include="*.cs" .` → QuickViewWindow 编辑保存路径必须引用它。

5. **测试连接假装成功** — 测试按钮直接弹"连接成功"不发请求。
   验证方式：断网状态下点测试 → 必须报错；配置错误 Key → 必须报错。

## 5. 取舍

可扩展性 > 功能正确 > 代码整洁 > UI 美观 > 性能优化

- 可扩展性排第一：LLM Provider 必须接口化，因为模型/协议随时可能换（Agnes 免费政策会变、Anthropic 协议待接入），写死 = 返工。
- 功能先跑通：编辑能保存、锁定能拦人，UI 丑一点没关系。
- 别搞过早优化：HttpClient 单例复用即可，不用上重试框架/连接池调优。

## 6. 未知处理

1. **NuGet 装包失败** → 本阶段不新增 NuGet 包（HttpClient 内置），如确需包：先 `nuget source` 确认国内源，装不上写 BLOCKED.md。
2. **Agnes 国内节点不通** → 换成国际节点 `https://apihub.agnes-ai.com/v1`（Key 不变）重试；仍不通，用 `https://api.hunyuan.cloud.tencent.com/v1` + Hunyuan-lite 验证协议，写 BLOCKED.md 备注网络状况。
3. **WPF 编译期 XAML 报错** → 检查 `InitializeComponent` 与事件签名（项目惯用 `_suppressEvents` 标志防御 InitializeComponent 期间触发的事件，见 SettingsWindow 先例）。
4. **一个任务卡住超过 30 分钟** → 写 BLOCKED.md（卡在哪、试了什么、什么错误），**跳过做下一个**。

## 7. 任务清单

### 第一步：AppSettings 加 AI 配置属性

> 操作位置：`Models/AppSettings.cs`

在类内新增（放在"── 导出 ──"之后）：

```csharp
// ── AI 模型 ──
public string AiBaseUrl { get; set; } = "https://apihub.agnes-ai.cn/v1";
public string AiApiKey { get; set; } = "";
public string AiModel { get; set; } = "agnes-2.5-flash";
public string AiAssistantName { get; set; } = "AI 问答";
```

注意：`AppJsonContext` 已声明 `[JsonSerializable(typeof(AppSettings))]`，新属性自动参与序列化，**不要改 AppJsonContext.cs**。

### 第二步：新建 AI 模型与接口

> 操作位置：`Services/AI/`（新建目录）

**`ChatModels.cs`**：

```csharp
namespace FocusCapture.Services.AI;

public sealed record ChatMessage(string Role, string Content);

public static class ChatRoles
{
    public const string System = "system";
    public const string User = "user";
    public const string Assistant = "assistant";
}

public enum ExplainMode { Translate, Search, Ask }
```

**`IChatProvider.cs`**：

```csharp
namespace FocusCapture.Services.AI;

public interface IChatProvider
{
    string Model { get; }
    string BaseUrl { get; }
    string ApiKey { get; }
    Task<string> CompleteAsync(IReadOnlyList<ChatMessage> messages, CancellationToken ct = default);
    IAsyncEnumerable<string> StreamAsync(IReadOnlyList<ChatMessage> messages, CancellationToken ct = default);
    Task<bool> TestConnectionAsync(CancellationToken ct = default);
}
```

**`OpenAICompatibleProvider.cs`**：实现 OpenAI 兼容协议。

要点（写死，不自由发挥）：
- 请求：`POST {BaseUrl}/chat/completions`，Header `Authorization: Bearer {ApiKey}`，Body：
  ```json
  { "model": "...", "messages": [ { "role": "...", "content": "..." } ], "stream": true/false }
  ```
- `CompleteAsync`：`stream=false`，解析 `choices[0].message.content`
- `StreamAsync`：`stream=true`，SSE 逐行解析 `data: {...}`，`choices[0].delta.content` 逐块 yield；`data: [DONE]` 结束
- `TestConnectionAsync`：发一个最小请求（`messages=[{role:"user",content:"hi"}]`，`max_tokens=1`），HTTP 2xx 且能解析出 content 即成功
- **HttpClient 单例**（`private static readonly HttpClient`），`BaseAddress` 构造时设
- 所有 API 错误：抛 `InvalidOperationException`，消息包含 HTTP 状态码 + 响应体前 200 字符
- 取消令牌：所有方法支持 `CancellationToken`
- 不实现重试（Phase 2 再说）

**`ITextExplainService.cs`**：

```csharp
namespace FocusCapture.Services.AI;

public interface ITextExplainService
{
    Task<string> ExplainAsync(string text, ExplainMode mode, CancellationToken ct = default);
}
```

**`LlmTextExplainService.cs`**：实现走 `IChatProvider`。system prompt 按模式组装：
- `Translate`：`你是一个翻译与释义助手。如果输入是单词或短语，输出：词性、释义、常见搭配、例句；如果输入是段落，输出整段中文翻译并简要解释。`
- `Search`：`你是一个笔记解释助手。阅读用户提供的笔记内容，解释其中的关键概念、术语或背景，帮助用户理解。`
- `Ask`：`你是一个 AI 助手，回答用户的问题。`
构造器注入 `IChatProvider`。

### 第三步：新建 ImmersiveSessionService

> 操作位置：`Services/ImmersiveSessionService.cs`

```csharp
namespace FocusCapture.Services;

/// <summary>沉浸式输入会话状态：会话激活时，对应笔记禁止编辑/回填</summary>
public static class ImmersiveSessionService
{
    public static bool IsActive { get; private set; }
    public static DateTime? ActiveTimestamp { get; private set; }

    public static void Start(DateTime timestamp) { IsActive = true; ActiveTimestamp = timestamp; }
    public static void Stop() { IsActive = false; ActiveTimestamp = null; }

    /// <summary>判断笔记是否处于沉浸式锁定中</summary>
    public static bool IsLocked(DateTime noteTimestamp)
        => IsActive && ActiveTimestamp.HasValue
           && Math.Abs((noteTimestamp - ActiveTimestamp.Value).TotalSeconds) < 60;
}
```

**注意**：`VoiceInputWindow.xaml.cs` 的 `SaveContent()` 里，首次保存时（`_currentNoteTimestamp ??= DateTime.Now` 处）调用 `ImmersiveSessionService.Start(_currentNoteTimestamp.Value)`；窗口关闭/新建会话时调用 `Stop()`。**这是本阶段唯一要改的 VoiceInputWindow 逻辑**，改动最小化。

### 第四步：SettingsWindow 加 AI 模型配置区

> 操作位置：`Windows/SettingsWindow.xaml` + `.cs`

在设置窗口加"AI 模型"分组（放热键区之后），控件：
- `AiBaseUrlInput`（TextBox，标签"API Base URL"）
- `AiApiKeyInput`（TextBox，标签"API Key（用户自备）"，可加 PasswordBox 风格但本项目无先例，用普通 TextBox 即可）
- `AiModelInput`（TextBox，标签"模型名称"，默认 agnes-2.5-flash）
- `BtnTestAi`（Button，"测试连接"）+ 结果提示（TextBlock，成功/失败文本）

`.cs` 逻辑（沿用现有 `LoadSettings()` / `_suppressEvents` 模式）：
- `LoadSettings()` 里回填三个输入框
- 输入框 `TextChanged` → 写回 `_settings`（沿用现有 `InputOpacity_Changed` 风格）→ `_settings.Save()`
- `BtnTestAi_Click`：`_suppressEvents=true` 期间禁用按钮 + 提示"连接中..."，用当前配置构造 `OpenAICompatibleProvider` 调 `TestConnectionAsync`，成功绿字"连接成功"，失败红字显示异常消息；**必须在 UI 线程 await，捕获异常**

### 第五步：QuickViewWindow 笔记编辑

> 操作位置：`Windows/QuickViewWindow.xaml` + `.cs`、`Services/NoteService.cs`

**NoteService 加方法**（`Services/NoteService.cs`，行级定位替换，**禁止全文件重写**）：

```csharp
/// <summary>更新一条笔记内容（Phase 1 临时语义：行内覆盖）。行级定位，不动其他行。</summary>
public bool UpdateNote(NoteEntry entry, string newContent)
```

实现要点：
- 复用 `ParseNotes` 的行格式正则，按 `entry.Timestamp` 生成行前缀 `- [{yyyy-MM-dd HH:mm}]`
- 在**所有相关文件**（当天灵感文件 + 标签文件）中定位该行（参考 `LongNoteService.InsertOrUpdate` 的 `FindLineStart` 行首匹配思路，注意日期完整格式）
- 只替换匹配行，其余内容原样写回（用 `File.ReadAllText` + 行级替换 + `File.WriteAllText` 可接受，但必须只改目标行，不得破坏 `## 沉浸记录` 块和其他条目）
- 找不到返回 false

**QuickViewWindow 编辑交互**：
- 双击笔记条目（`NoteItem_Click` 已有，识别双击）进入编辑态
- 编辑态：条目内容区变为 `TextBox`（绑定当前 `NoteEntryViewModel`，可在 XAML 用 DataTemplate 加编辑模板，或运行时切换）
- 保存：编辑框旁显示"保存"按钮 + 支持 `Ctrl+S` 保存、`Esc` 取消（参考 `InputWindow.InputBox_PreviewKeyDown` 先例）
- 保存前检查 `ImmersiveSessionService.IsLocked(entry.Timestamp)` → 锁定则 `MessageBox` 提示"沉浸式输入进行中，暂不可编辑"并取消
- 保存成功 → `Refresh()` 重新加载
- **本阶段编辑是"行内覆盖"临时语义**，Phase 3 会改为"追加记录行"（MD 只增不减），本阶段不用实现追加

### 第六步：构建与冒烟

> 操作位置：项目根目录

```bash
dotnet build -c Release
```

构建通过后，手动冒烟：设置页填 Agnes Key → 测试连接 → 成功。

## 8. 验收标准

> 按顺序执行，每条必须与期望一致。

### 构建验收

```bash
cd "项目根目录"
dotnet build -c Release
# 期望：Build succeeded，0 error
```

### 反作弊静态检查

```bash
grep -rn "chat/completions" Windows/ MainWindow.xaml.cs | grep -v "Services/AI"
# 期望：无输出（业务代码不直接调 LLM）

grep -rn "sk-" --include="*.cs" . | grep -v "AppSettings.cs\|OpenAICompatibleProvider.cs"
# 期望：无输出（无硬编码 Key）
```

### 手动验收（AI 配置）

1. 启动应用 → 设置 → 出现"AI 模型"区（Base URL / Key / 模型名 / 测试连接）
2. 填一个**错误的 Key** → 测试连接 → 期望：显示失败信息（红色），不崩溃
3. 填正确的 Agnes Key → 测试连接 → 期望：显示"连接成功"
4. 断网 → 测试连接 → 期望：报错，不崩溃

### 手动验收（笔记编辑）

5. 灵感速览面板 → 双击一条普通笔记 → 期望：进入编辑态，内容可改
6. 修改内容 → Ctrl+S → 期望：保存成功，面板刷新，对应 MD 文件该行已更新
7. 再次双击 → Esc → 期望：取消编辑，内容不变
8. 打开沉浸式输入窗口 → 保存一条内容 → 切到速览面板 → 双击该条 → 期望：弹窗提示"沉浸式输入进行中，暂不可编辑"
9. 关闭沉浸式输入 → 再双击该条 → 期望：可以编辑

### 回归验收（不破坏 v0.1）

10. 悬浮球显示/拖动正常；剪贴板自动捕获（若开启）正常；沉浸式语音输入能录音识别（模型加载正常）；导出功能正常
11. MD 文件里已有的 `## 沉浸记录` 块内容在编辑其他笔记后保持原样

### Release 发布验证

```bash
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
# 期望：成功，产出单文件 exe（任意 Win10/11 x64）
```

## 9. 交付

完成后执行：

```bash
git add -A
git commit -m "quest1: AI Provider 抽象层 + 模型配置 + 笔记编辑基础"
```

**不要合并到 main，不要删分支。** 验收通过后再进 QUEST-2。
