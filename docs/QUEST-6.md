# QUEST-6 — v3.5 待办与提醒系统（4 Phase 一份文档走完）

> 依据：`docs/TASK-v35.md`（总规划，只读）| 执行者：Codex / WorkBuddy Agent | 2026-08-26

---

## 衔接到 QUEST-5

**前提**：QUEST-5（云端同步）已完成并合 main（b82cbbf）。你接手时：

- 后端：NoteService（MD 行级存储，`- [yyyy-MM-dd HH:mm] 内容 — 来源: xxx`）、SyncEngine（WebDAV 行级同步）、AI 服务（翻译/搜索/问答，IChatProvider + OpenAI 兼容协议）、回收站、日历统计、导入/区间/搜索
- 前端：MainWindow（装配）+ FloatBall（悬浮球）+ InputWindow（输入框）+ QuickViewWindow（灵感速览）+ SettingsWindow + CalendarWindow + 沉浸语音
- 数据：`%USERPROFILE%\Documents\FocusCapture\*.md`（灵感_日期.md + 标签.md），settings.json 在 %AppData%\FocusCapture

**回归基线（必须全绿才开工）**：

- [ ] `dotnet build -c Debug` → Build succeeded
- [ ] 启动应用，Alt+Space 唤出输入框，输入"回归测试"回车 → `灵感_当天.md` 出现该行
- [ ] Ctrl+Alt+V 打开灵感速览 → 显示该笔记；右键 → 编辑/删除可用
- [ ] 设置窗口打开正常，热键、AI 配置可见

---

## 项目背景

**这是什么**：给输入框加「笔记/待办」类型选择（UI 两入口、代码三态），待办支持提醒时间自动识别、单条到点弹窗、每日汇总、悬浮球角标。  
**为什么做**：用户要"快速记待办 + 能提醒"。核心诉求三件套：记待办零成本（选一下类型）、不打扰（弹窗 10 秒自动收起 + 每日 18:00 汇总）、不遗忘（悬浮球角标 + 汇总兜底）。  
**不是什么**：

- 不是完整任务管理软件：不做优先级、子任务、标签树、闹钟式重复提醒（N 次后停止）
- 不做系统级后台提醒：应用没开提醒不响（用户明确砍掉）
- 普通笔记 = 纯记录，不参与任何待办逻辑（类型在输入框定死）
- 不做「自动判断类型」：类型永远用户手动选，自动识别只做「待办 → 待办+提醒」升级  
  **用户画像**：彭杰，Windows，零编程背景（验收要现象级），深色 UI 全项目一致。  
  **技术栈拍板**：.NET 8 WPF，无新增依赖。时间解析用本地规则（TimeParser）优先，模糊表达才调现有 IChatProvider（agnes-2.5-flash）。  
  **开发方式**：AI 代劳（本文档逐条执行）。

---

## 体验规格

- **输入框**：类型按钮放输入框底部左侧（「笔记」「待办」两个小按钮）；Ctrl+T 切换类型（**全局热键**，走 HotkeyService 注册，设置可改；输入框内不再单独处理 Ctrl+T 防双触发）；placeholder 保持"记录你的灵感…（回车保存，Esc 取消）"
- **面板（灵感速览）**：待办条目显示「待办」徽标（绿色小标签）；已办 = 灰字 + 删除线 + 沉底到当天最后；已读 = 橙字「已读」小标；**编辑待办 = 原地改行（MD 只增不减的唯一例外，红线 2）**，保存后若识别到时间，行内浮建议条「检测到明天上午9点，设为提醒？\[设为提醒\]\[忽略]」，10 秒不点自动消失；弹窗/角标/汇总显示内容统一用 `EditedContent ?? Content`
- **单条提醒弹窗**：悬浮球上方弹出，显示内容 + 提醒时间，10 秒自动收起；三按钮「已完成 / 稍后提醒 / 已知悉」
- **每日汇总弹窗**：默认 18:00 弹出（设置可配/可关），列出所有未办未读待办；按钮按类型：纯待办 \[已知悉\]\[稍后查看]；有时间待办 \[已完成\]\[顺延到明天]
- **悬浮球角标**：右上角小圆标显示**未办待办总数（Open+Read）**；存在已读（暂缓）任务时变红，否则默认绿；**无待办（Open 与 Read 均为 0）时隐藏**；点击角标打开汇总窗（点击必须吞掉事件，防冒泡触发悬浮球拖拽）
- **汇总窗**：分两组「待处理」（Open，未读未办）/「已暂缓」（Read）；待处理条目按钮补全：纯待办 \[已完成\]\[已知悉\]\[稍后查看]、有时间待办 \[已完成\]\[顺延到明天\]\[已知悉]；已暂缓条目可 \[恢复提醒\]\[标记完成]
- **空态**：汇总窗无任务时显示"今天没有待处理事项"
- **边界**：提醒时间已过的输入不设提醒（保持纯待办）；编辑后无时间不自动清除原提醒

---

## 全局约束

### 必做

- 技术：.NET 8 / C# 12 / WPF，编译零警告新增；构建命令 `dotnet build -c Debug`
- 存储：MD 行级，状态变更原地改行（见红线 2），行内属性顺序固定：`【待办】正文 (提醒: yyyy-MM-dd HH:mm, 状态: 已办)`；**待办行内容编辑同样原地改行**（普通笔记编辑仍走【编辑】行追加）
- 代码：沿用现有命名（Services/Windows/Models 分层）、注释风格（/// 中文）、深色配色（#1E1E1E 背景 / #3A3A3A 边框 / #E0E0E0 文字 / #4CAF50 主色 / #E24B4A 警示红）
- 纪律：Phase 顺序推进，每 Phase 完成跑该 Phase 验收 + git commit（message 带 "quest 6 phase N"）；全部完成后合 main + 推送双远程

### 禁止（违反就算失败）

- 不追加标记行模拟待办状态变更（已办/已读/提醒变化必须原地改写原行）
- 不在输入框场景调 LLM 做时间识别（规则未命中 = 保持纯待办）
- 不引入除 System.* 外的新 NuGet 包
- 不新建独立 JSON 存储文件承载待办状态（全部行内标记）
- 不改 `NoteLineRegex` 正则本体（解析兼容靠内容区剥离，不动正则）
- 不把已办/已读待办从面板隐藏掉（已办沉底可见、已读带标可见）

---

## 项目目录结构（本期新增/改动文件标 ★）

```
FocusCapture/
├── Models/
│   ├── NoteEntry.cs                ★ 扩展：NoteType/TodoStatus 枚举 + Type/DueTime/TodoStatus 属性 + ToMarkdownLine 待办分支
│   └── AppSettings.cs              ★ 扩展：6 个新设置项（见 Phase 1 任务 4）
├── Services/
│   ├── NoteService.cs              ★ 扩展：SaveNote 类型参数、UpdateTodo（原地改行）、ParseNotes 待办解析、行重建
│   ├── TimeParser.cs               ★ 新建：本地规则时间解析 + FormatNaturalTime 自然语言格式化（Phase 1）
│   ├── ReminderService.cs          ★ 新建：定时器 + 弹窗调度 + 角标（Phase 3）
│   └── AI/PromptBuilder.cs         ★ 扩展：BuildTimeParsePrompt（Phase 2）
├── Windows/
│   ├── InputWindow.xaml/.cs        ★ 扩展：类型按钮 + Ctrl+T + 保存升级（Phase 1）
│   ├── SettingsWindow.xaml/.cs     ★ 扩展：新设置项 UI（Phase 1）
│   ├── QuickViewWindow.xaml/.cs    ★ 扩展：徽标/已办/编辑识别/右键/筛选；构造注入 IChatProvider（Phase 2）
│   ├── FloatBall.xaml/.cs          ★ 扩展：角标（Phase 3）
│   ├── ReminderPopupWindow.xaml/.cs    ★ 新建：单条提醒弹窗（Phase 3）
│   ├── DailySummaryWindow.xaml/.cs     ★ 新建：每日汇总弹窗（Phase 3）
│   └── TodoSummaryWindow.xaml/.cs      ★ 新建：分组汇总窗（Phase 3）
├── MainWindow.xaml.cs              ★ 扩展：ReminderService 装配 + TodoSwitchHotkey 全局热键 case（Phase 1/3）
└── docs/
    ├── TASK-v35.md                 # 只读规划（禁止改动）
    └── QUEST-6.md                  # 本文件
```

## 禁区（碰了算失败）

- `docs/TASK-v35.md` — 规划文档，只读
- 验收清单（本文件）中出现的命令/期望输出 — 执行者不许改文档凑验收，发现命令有问题写 BLOCKED.md 汇报

---

## 反作弊（点名具体偷懒姿势，用了就算失败）

1. **状态变更用追加行** — 点已办/已读/改提醒时往文件追加一条带状态的新行，而不是改写原行。  
   验证方式：`Select-String` 定位原行，确认原行文本被改写（含 `(状态: 已办)`），且文件中该内容无重复行。
2. **时间解析糊弄** — 正则只写 `\d+:\d+` 或只处理"明天"，其余表达全部漏掉。  
   验证方式：Phase 1 验收第 5 步的 10 个用例逐条过，每个用例的存储行必须带正确的 `(提醒:)`。
3. **已办只改内存不落盘** — 面板点标签只改 ViewModel/内存列表，不写文件。  
   验证方式：Phase 2 验收第 2 步重启应用，状态保留。
4. **面板编辑识别不调 LLM** — 模糊表达（"周五下班前交"）也硬用正则，或直接静默跳过。  
   验证方式：Phase 2 验收第 4 步，规则未命中的输入必须触发 LLM（Debug.WriteLine 有日志），未配置 AI Key 时优雅降级不弹建议、不崩溃。
5. **筛选走前端 filter 且刷新即失效** — 筛选只是当前列表临时过滤，切日期/刷新后筛选丢失。  
   验证方式：Phase 2 验收第 5 步，刷新后筛选条件仍生效。
6. **提醒用 Thread.Sleep 循环** — 后台线程轮询阻塞。  
   验证方式：代码审查，ReminderService 必须用 DispatcherTimer，grep 无 Thread.Sleep。
7. **编辑待办仍走【编辑】行追加** — 待办内容编辑用 AppendEdit 追加标记行，不原地改行（违反红线 2 例外）。  
   验证方式：Phase 2 验收 3：编辑待办后 grep 原行已被改写为新内容，且无新增【编辑】行。
8. **UpdateTodo 先改 entry 再定位** — 先套用状态/内容变更再匹配原行，导致 IsEntryLine 永远不相等、UpdateTodo 恒 false。  
   验证方式：Phase 2 验收 3 中 UpdateTodo 必须返回 true 且落盘正确；代码审查定位用变更前字段。

---

## 取舍

数据安全 > 功能正确 > 代码可读 > UI 美观 > 性能优化

- 数据安全排第一：待办状态变更先写回收站安全路径的删除逻辑不得破坏；行改写失败必须返回 false 且不产生半行
- 旧数据兼容优先于新功能花哨：解析器永远把无标记行当普通笔记，宁可新功能保守不能炸老数据
- 规则优先于大模型：能本地规则解决的时间解析绝不花钱调 LLM，输入框场景直接禁止 LLM
- UI 先融入再谈好看：新控件用现有配色系，不引入新设计语言

---

## 未知处理

1. `dotnet build` 报错 → 读错误信息定位修复；同一错误连续 3 次 → 写 BLOCKED.md 并跳过该任务
2. XAML 绑定/编译错（标记行、资源找不到）→ 看构建输出逐条修，注意 .g.cs 由构建生成不要手改
3. LLM 调用失败（面板编辑识别）→ 捕获异常，降级为不弹建议条，编辑保存照常完成
4. 文件被占用/锁定（杀毒或编辑器）→ 等待 1 秒重试一次，仍失败写 BLOCKED.md
5. 单个任务卡住超 30 分钟 → 写 BLOCKED.md（卡哪、试了什么、错误），跳过做下一个

## 整期止损

- 这一期最多烧 1 小时，满线即停，如实汇报卡在哪、还剩哪些 Phase
- 结果比开工时更差 → git 回滚到上一 Phase commit，如实报告；「没做完但说清了」合格，「做了但更糟」不合格

---

## 任务清单（4 个 Phase，按顺序执行）

### Phase 1：底座（数据模型 + 输入框 + 设置 + 时间规则）

#### 任务 1-1：NoteEntry 扩展（Models/NoteEntry.cs）

> 操作位置：Models/NoteEntry.cs

在文件顶部加枚举，NoteEntry 加 3 个属性：

```csharp
public enum NoteType { Note, Todo }
public enum TodoStatus { Open, Done, Read }

// NoteEntry 内新增：
public NoteType Type { get; set; } = NoteType.Note;
public DateTime? DueTime { get; set; }        // 提醒时间（仅 Type=Todo 时有值）
public TodoStatus TodoStatus { get; set; } = TodoStatus.Open;
```

**ToMarkdownLine() 扩展**：Type=Todo 时输出待办行格式（属性都在内容区，顺序固定）：

```
- [yyyy-MM-dd HH:mm] 【待办】正文 (提醒: yyyy-MM-dd HH:mm, 状态: 已办) — 来源: xxx
```

规则：

- 正文 = Content 转义后（\u23CE 逻辑不变）
- `(提醒: ...)` 仅 DueTime 有值时输出，格式 `yyyy-MM-dd HH:mm`
- `(状态: 已办/已读)` 仅非 Open 时输出
- 无提醒且 Open：`【待办】正文`（无括号后缀）
- 笔记类型完全走现有逻辑，一行不改

#### 任务 1-2：ParseNotes 待办解析（Services/NoteService.cs）

> 操作位置：Services/NoteService.cs 的 ParseNotes 方法（普通行分支）

普通行解析时，Content 以 `【待办】` 开头 → Type=Todo，并剥离标记。**插入位置必须在 ParseMarkerLine 判定之后、普通行分支内（`NoteService.cs` 的 `entries.Add` 之前）**——不能放在 rawContent 赋值后（那会先于标记行判定，待办正文若以「【编辑】/【AI 释义】」开头会被误判成标记行）：

```csharp
// 在 ParseMarkerLine 判定之后、普通行 entries.Add 之前：
if (rawContent.StartsWith("【待办】", StringComparison.Ordinal))
{
    entry.Type = NoteType.Todo;
    var body = rawContent["【待办】".Length..].Trim();
    // 剥离 (提醒: yyyy-MM-dd HH:mm) 与 (状态: 已办|已读) 属性
    var dueMatch = Regex.Match(body, @"\(提醒: (\d{4}-\d{2}-\d{2} \d{2}:\d{2})\)");
    if (dueMatch.Success) { entry.DueTime = DateTime.Parse(dueMatch.Groups[1].Value); body = body.Remove(dueMatch.Index, dueMatch.Length).Trim(); }
    var stMatch = Regex.Match(body, @"\(状态: (已办|已读)\)");
    if (stMatch.Success)
    {
        entry.TodoStatus = stMatch.Groups[1].Value == "已办" ? TodoStatus.Done : TodoStatus.Read;
        body = body.Remove(stMatch.Index, stMatch.Length).Trim();
    }
    rawContent = body;
}
```

注意：`【编辑】`/`【AI 释义】` 标记行解析（ParseMarkerLine）**不动**——编辑行里若含"【待办】"字样走原逻辑，不做类型推断。

#### 任务 1-3：SaveNote 类型参数（Services/NoteService.cs）

> 操作位置：SaveNote 方法签名 + 入口逻辑

```csharp
public NoteEntry? SaveNote(string content, string? sourceWindow = null, NoteType type = NoteType.Note)
```

入口处（Tag 提取之后）：

```csharp
entry.Type = type;
if (type == NoteType.Todo)
{
    // 规则解析：命中且是未来时间 → 设 DueTime；命中但已过 / 未命中 → 不设
    if (TimeParser.TryParse(entry.Content, out var due) && due > DateTime.Now)
        entry.DueTime = due;
}
```

调用点兼容：现有调用（SaveClipboard/SaveAiNote/ImportNotes）不传 type 保持 Note 行为，不用改。

#### 任务 1-4：TimeParser 新建（Services/TimeParser.cs）

> 操作位置：新建 Services/TimeParser.cs

```csharp
namespace FocusCapture.Services;

/// <summary>本地规则时间解析：能规则解决的绝不调 LLM。解析出的时间若已过当前时刻 → 返回 false（不设提醒）。</summary>
public static class TimeParser
{
    /// <summary>从文本中解析绝对/相对时间表达。成功 true + 绝对时间；失败 false。</summary>
    public static bool TryParse(string text, out DateTime time);
}
```

规则（text 先 Trim），按顺序尝试，**多个规则都能命中时取文本中位置最靠前的时间表达**（扫描全部规则、比较命中 index，取最小者；不是按规则优先级取）：

1. 绝对日期时间：`yyyy-MM-dd HH:mm`、`yyyy-M-d H:mm`（"2026-08-27 09:00"）
2. 日期+时间词：`8月27日 9点`、`8月27日9:30`、`8月27日上午9点`
3. 相对日+时间：`今天|明天|后天 [上午|下午|早上|晚上|中午] X点|X点半|X:XX`（如"明天上午9点"）
4. 星期+时间：`周X|星期X [时段] X点` → 最近一个该星期：**今天已是该星期且目标时刻未过 → 今天；否则下周**
5. 时段+数字：`上午|下午|早上|晚上|中午 X点|X点半|X:XX`（"下午3点"）
6. 数字时钟：`X点`、`X点Y分`、`X:XX`（**24 小时制**：`3点`=03:00，若已过当前时刻 → false 不设提醒；12 小时制时段依赖规则 5）
7. 相对时长：`N分钟后|N小时后|N天后`（"30分钟后""2小时后""3天后"，N 为整数）
8. 无时间词不解析：文本含 `明天|后天` 但无时间词 → 返回 false（不设提醒，不做 09:00 默认）

硬性边界：

- 规则 1-7 解析出的时间 `<= DateTime.Now` → 返回 false（识别到已过时间不设提醒；例：下午输"3点 开会"按 24 小时制=03:00 已过 → 不设提醒，想设下午必须写"下午3点"——此反直觉行为属有意设计，验收有对应用例）
- 内容里同时出现多个时间 → 取文本位置第一个
- "提醒""截止"等词本身不算时间

另提供展示辅助方法：

```csharp
/// <summary>DateTime → 自然语言（"今天 09:00" / "明天 上午9点" / "8月27日 20:00"），供建议条/弹窗文案用</summary>
public static string FormatNaturalTime(DateTime time);
```

#### 任务 1-5：AppSettings 新设置项（Models/AppSettings.cs）

> 操作位置：Models/AppSettings.cs（追加在 Sync 属性后）

```csharp
// ── v3.5 待办与提醒 ──
public string InputDefaultType { get; set; } = "Note";                       // "Note" / "Todo"
public HotkeyBinding TodoSwitchHotkey { get; set; } = new() { Modifiers = 2, Key = 0x54 }; // Ctrl+T，全局热键（RegisterHotKey），可能与其他应用冲突，设置可改
public bool DailySummaryEnabled { get; set; } = true;
public string DailySummaryTime { get; set; } = "18:00";                       // "HH:mm"
public int SnoozeMinutes { get; set; } = 10;
public int PopupAutoCloseSeconds { get; set; } = 10;
```

AppJsonContext 无需改（AppSettings/HotkeyBinding 已注册，源生成按属性自动带出）。

#### 任务 1-6：InputWindow 类型选择 UI（Windows/InputWindow.xaml + .cs）

> 操作位置：Windows/InputWindow.xaml（Grid 内加类型栏）+ InputWindow.xaml.cs

XAML：输入框 Grid 下加一行（高度 26），左侧两个 ToggleButton 风格按钮（按钮独立一行放左下，不与占位文字"记录你的灵感…"重叠）：

```xml
<StackPanel Orientation="Horizontal" HorizontalAlignment="Left" VerticalAlignment="Bottom" Margin="0,0,0,2">
    <Button x:Name="BtnNoteType" Content="笔记" Tag="Note" Click="TypeButton_Click" ... 样式同现有小按钮/>
    <Button x:Name="BtnTodoType" Content="待办" Tag="Todo" Click="TypeButton_Click" Margin="6,0,0,0" .../>
</StackPanel>
```

逻辑（InputWindow.xaml.cs）：

- 构造函数补字段 `private readonly AppSettings _settings;`（现构造函数只用了 `settings.InputOpacity`，需把 settings 存下来）
- 字段 `private string _currentType;`，Show() 时从 `_settings.InputDefaultType` 初始化（仅首次/草稿为空时）
- `ToggleType()` 公共方法：切 `_currentType`，刷新两按钮高亮（选中 = 边框 #4CAF50 + 文字亮，未选中 = 灰色）；窗口可见时立即刷新，不可见时仅切字段（下次打开生效）
- **Ctrl+T 走全局热键，不挂在输入框按键上**：`HotkeyService` 新增 `ID_TODO_SWITCH = 1005`，`RegisterAll`/`UnregisterAll` 注册 `TodoSwitchHotkey`；MainWindow `OnHotkeyPressed` 加 `case ID_TODO_SWITCH: _inputWindow?.ToggleType(); break;`。输入框内**不再**在 PreviewKeyDown 里处理 Ctrl+T，避免与全局热键双触发
- `TypeButton_Click`：切 `_currentType`，刷新高亮，**末尾 `InputBox.Focus()` 把焦点还回输入框**（否则焦点留在按钮上，回车触发的是按钮 Click 而非保存）
- `Save()`：`_noteService.SaveNote(text, type: _currentType == "Todo" ? NoteType.Todo : NoteType.Note)`
- 高亮样式：两种选中状态都可辨认（深色 UI，选中按钮 BorderBrush=#4CAF50、Foreground=#E0E0E0；未选中 BorderBrush=#3A3A3A、Foreground=#888888）

#### 任务 1-7：SettingsWindow 新设置项（Windows/SettingsWindow.xaml + .cs）

> 操作位置：SettingsWindow.xaml 新增设置分组 + .cs 读写

新增分组「待办与提醒」，控件：

- 输入框默认类型：ComboBox（笔记/待办）→ `InputDefaultType`
- 类型切换快捷键：复用现有热键编辑控件（参考现有 SummonHotkey 的配置方式）→ `TodoSwitchHotkey`
- 每日汇总：ToggleSwitch/CheckBox → `DailySummaryEnabled`；时间 TextBox（"HH:mm" 校验）→ `DailySummaryTime`
- 稍后提醒间隔（分钟）：TextBox 数字 → `SnoozeMinutes`
- 弹窗收起秒数：TextBox 数字 → `PopupAutoCloseSeconds`

沿用现有**改即保存**模式（控件变更即时写入 AppSettings.Save()，无统一保存按钮）：校验在各控件输入时即时执行——DailySummaryTime 必须匹配 `^\d{1,2}:\d{2}$` 且 00:00~23:59，非法输入即时提示并回退默认；数字字段必须为正整数，非法即时提示并回退默认。热键控件变更后需重新注册（沿用现有 RegisterAll 调用方式，MainWindow 的 onChanged 回调已调 RegisterAll）。

### Phase 1 验收

> 按顺序执行。现象级验收：做完看到 X，看到 Y 即错。

```powershell
# 1. 构建通过
dotnet build -c Debug
# 期望: Build succeeded，0 error
```

1. 启动应用，Alt+Space 唤出输入框 → 底部左侧出现「笔记」「待办」两个按钮，「笔记」默认高亮（未改设置时）
2. 输入「明天上午9点 交报告」，点「待办」按钮（点完焦点自动回输入框），回车保存：

```powershell
Get-Content "$env:USERPROFILE\Documents\FocusCapture\灵感_$(Get-Date -Format yyyy-MM-dd).md" | Select-String "交报告"
# 期望: - [2026-08-26 10:30] 【待办】交报告 (提醒: 2026-08-27 09:00) — 来源: ...   （绝对时间格式，= 明天 09:00；无重复行）
```

3. 全局热键：输入框开着时按 Ctrl+T（任意焦点位置，不必在输入框内）→ 类型按钮在笔记/待办间切换（现象级：选中高亮跟随切换）；设置里改快捷键 → 重新注册生效
4. TimeParser 用例逐条验（每条：输入该文本、选待办保存、查存储行 `(提醒:)` 值）：
   - "明天上午9点 写日报" → 明天 09:00
   - "今天下午3点 开会" → 今天 15:00
   - "30分钟后 吃药" → now+30min（±1min 容差）
   - "周五9点 交周报"（周五早上 8 点输入）→ **今天 09:00**（今天该星期且时刻未过 → 取今天，不是下周五）
   - "2026-08-30 10:00 出差" → 2026-08-30 10:00
   - "8月31日 晚上8点 聚会" → 2026-08-31 20:00
   - "写周报"（无时间）→ 存储行无 `(提醒:)`
   - "上午9点 开会"（当前已过 9 点）→ 无 `(提醒:)`（已过时间不设）
   - "2天后 体检" → now+2d 同时刻
   - "下午3点半 面试" → 当天 15:30
   - **"3点 开会"（下午输入）→ 无 `(提醒:)`**（24 小时制 = 03:00 已过；想设下午必须写"下午3点"——反直觉行为属有意设计）
   - **"下午3点 面试，周五9点交周报"（周四输入）→ 今天 15:00**（多时间取文本第一个，不是按规则优先级取周五）
5. 设置 → 默认类型改「待办」→ 保存 → 重新唤出输入框 → 「待办」默认高亮
6. 设置里改「稍后提醒间隔」为 1、「弹窗收起秒数」为 5 → 保存后值保留（重启应用仍生效）

Phase 1 完成 → git add -A && git commit -m "quest 6 phase 1: 待办数据模型+时间规则+输入框类型选择+设置项"

### Phase 2：面板（徽标 / 已办 / 编辑识别 / 右键 / 筛选）

#### 任务 2-1：UpdateTodo 原地改行（Services/NoteService.cs）

> 操作位置：NoteService.cs 新增方法（放在 DeleteNote 附近）

```csharp
/// <summary>待办原地改行：更新内容/状态/提醒时间。用变更前字段定位原行 → 重建行文本 → 整文件重写该行。找不到原行返回 false。</summary>
public bool UpdateTodo(NoteEntry entry, string? newContent = null, TodoStatus? status = null, DateTime? dueTime = null, bool clearDue = false)
```

实现（**顺序敏感：先定位、后重建**）：

- 定位文件：复用 FindEntryFile
- 读全部行，**用变更前字段定位原行**：以 entry 原始字段（Timestamp + 变更前的 Content）调 `IsEntryLine` 精确整行匹配（区分大小写）——**禁止先套用变更再匹配**（`IsEntryLine` 比较 `line == entry.ToMarkdownLine()`，先改字段后新行文本带 `(状态: 已办)` 等后缀，永远匹配不上原行，UpdateTodo 将恒返回 false）
- 重建新行：以 entry 原始字段为底，套用变更（`newContent` 有值则正文=新内容；`status`/`dueTime`/`clearDue` 按参数套用），调静态方法 `FormatTodoLine(NoteEntry e)`（与 ToMarkdownLine 的待办分支同一套格式逻辑，抽出来供两处复用，**方法名统一为 FormatTodoLine，不再叫 BuildTodoLine**）
- 替换该行 → File.WriteAllLines（UTF-8）
- 成功 → NotesChanged?.Invoke()，返回 true；原行未找到 → false
- **禁止**追加新行；禁止改到非待办行
- **并发安全**：UpdateTodo 与 SyncEngine 的 `AppendLine`/`RemoveLines` 共用同一把静态写锁（`private static readonly object FileWriteLock`），防后台同步与用户操作并发整文件重写互相覆盖

#### 任务 2-2：面板待办展示 + 点标签已办（Windows/QuickViewWindow.xaml + .cs）

> 操作位置：QuickViewWindow.xaml.cs 的条目渲染逻辑 + NoteEntryViewModel 扩展

- 待办条目渲染：内容前加「待办」徽标（绿色小标签 Border，Text="待办"）；已办：整条 Foreground 变灰 + 内容删除线（TextDecoration）；已读：内容后加橙字「已读」小标
- 徽标可点击：点击该待办条目的「待办」徽标 → `_noteService.UpdateTodo(entry, newContent: entry.EditedContent ?? entry.Content, status: TodoStatus.Done)`（正文带当前显示内容，编辑过的待办以新内容重建，防旧正文覆盖）→ 刷新列表（条目变灰划线 + 沉底）
- 沉底实现：ReloadNotes 排序后，`TodoStatus.Done` 的待办排到该日期列表最后（稳定排序，其余保持时间倒序）
- 刷新：UpdateTodo 成功后在 UI 线程 ReloadNotes

#### 任务 2-3：编辑待办 → 原地改行 + 时间识别建议（Windows/QuickViewWindow.xaml.cs + NoteEditWindow.xaml.cs + Services/AI/PromptBuilder.cs）

> 操作位置：SaveEditNote / NoteEditWindow.Save 两条保存路径统一扩展 + PromptBuilder 新增方法

**存储语义（关键，先定死再动手）**：编辑待办时内容有变化 → **原地改行**（`UpdateTodo(entry, newContent: ...)` 重建【待办】行），**不走 AppendEdit 追加【编辑】行**——否则原行是旧正文、新内容只活在【编辑】行里，之后所有 UpdateTodo 都用旧正文重建，编辑内容在存储层永久丢失。普通笔记编辑仍走 AppendEdit（现状不变）。

为防两条保存路径逻辑漂移，抽公共方法（如 `Services/TodoEditService.cs`）：

```csharp
/// <summary>编辑待办：内容变化 → UpdateTodo 原地改行；返回是否保存成功。普通笔记走原 AppendEdit。</summary>
public static bool SaveEdited(NoteService notes, NoteEntry entry, string newContent);

/// <summary>时间识别：本地规则优先，规则未命中才调 LLM 兜底；返回识别到的时间（无则 null）。异常降级返回 null。</summary>
public static async Task<DateTime?> DetectDueAsync(string text, IChatProvider? llm, CancellationToken ct);
```

SaveEditNote（QuickViewWindow）与 NoteEditWindow.Save 都改走上面两个方法，行为完全一致：

1. 保存：`TodoEditService.SaveEdited(...)`（Todo 原地改行 / 笔记 AppendEdit），保存失败提示并中止
2. 识别：`TimeParser.TryParse(newContent, out var due)` → 命中且未来 → 弹建议条（第 4 步）
3. 未命中 → **LLM 兜底**：`DetectDueAsync(newContent, _provider, ct)`（内部调 `_provider.CompleteAsync(BuildTimeParseMessages(newContent), ct)`），输出解析为 JSON `{"has_time":true,"time":"yyyy-MM-dd HH:mm"}`；解析成功且未来 → 弹建议条
4. 建议条：行内浮出（现有编辑框区域上方），文案用 `TimeParser.FormatNaturalTime(due)` 生成（如「检测到明天上午9点，设为提醒？\[设为提醒\]\[忽略]」）；点设为提醒 → `UpdateTodo(entry, newContent: newContent, dueTime: due)`；点忽略/10 秒超时 → 消失
5. 编辑后未识别到时间 → **不自动清除原提醒**（DueTime 保持）
6. LLM 调用异常（未配 Key/网络失败）→ 捕获，不弹建议，编辑保存照常完成

**异步注入（现状缺口，必须补）**：

- QuickViewWindow 构造函数注入 `IChatProvider`（MainWindow 装配时传入，与 AIDialogWindow 共用同一 OpenAICompatibleProvider 实例）
- `SaveEditNote` 现有签名是同步 void（QuickViewWindow.xaml.cs:656）——改为 `async void` 或内部 `await Task.Run`，LLM 调用放后台线程，建议条弹回 UI 线程，**禁止 UI 线程同步阻塞等 LLM**
- NoteEditWindow 同样注入 `IChatProvider`（构造参数加 provider）

PromptBuilder 新增：

```csharp
public static ChatMessage[] BuildTimeParseMessages(string text)
// System: "你是一个时间解析器。从用户文本中找出唯一的提醒时间表达，输出严格 JSON：{\"has_time\":true/false,\"time\":\"yyyy-MM-dd HH:mm\"}（无时间则 has_time=false）。只输出 JSON，不要解释。"
// User: text
```

#### 任务 2-4：右键菜单设置/取消提醒（Windows/QuickViewWindow.xaml + .cs）

> 操作位置：NoteContextMenu 增加两项（仅待办条目显示）

- 「设置提醒」：弹出时间选择（复用现有 MiniCalendarPicker 思路或简单 TextBox 输入"yyyy-MM-dd HH:mm"对话框）→ `UpdateTodo(entry, newContent: entry.EditedContent ?? entry.Content, dueTime: t)`（正文带当前显示内容，防覆盖编辑）
- 「取消提醒」：`UpdateTodo(entry, newContent: entry.EditedContent ?? entry.Content, clearDue: true)`
- 右键菜单 Opened 时：非待办条目这两项 IsEnabled=false（或隐藏）

#### 任务 2-5：筛选（类型 4 档多选 + 来源）（Windows/QuickViewWindow.xaml + .cs）

> 操作位置：QuickViewWindow.xaml 顶部工具栏下加筛选行 + .cs 过滤逻辑

- 筛选行：四个 ToggleButton「全部」「笔记」「待办」「已办」+ 来源 ComboBox（全部/各来源窗口，来源从当前加载列表聚合）
- 交互：默认「全部」选中；点其他档 → 「全部」自动取消；四档全取消 → 自动回「全部」；多选组合生效（如「待办」+「已办」= 未办和已办都显示）
- 过滤在内存执行（ReloadNotes 后应用），筛选状态存字段 `_typeFilter`/`_sourceFilter`，**切日期/刷新后保持生效**
- **档位语义（关键）**：「待办」= 未办（Open+Read，含已读暂缓，**不含已办**）；「已办」= 只 Done。已办不再被子集包含，多选组合才有意义（否则「待办」+「已办」=「待办」，组合形同虚设）。Read（已读暂缓）归入「待办」档，不做独立档（有意设计：已读=还没做完）

### Phase 2 验收

1. `dotnet build -c Debug` → Build succeeded
2. 面板待办操作落盘验证：Phase 1 存的「明天上午9点 交报告」待办 → 面板点「待办」徽标 → 条目变灰 + 删除线 + 沉底；随后：

```powershell
Get-Content "$env:USERPROFILE\Documents\FocusCapture\灵感_$(Get-Date -Format yyyy-MM-dd).md" | Select-String "交报告"
# 期望: 该行含 (提醒: 2026-08-27 09:00, 状态: 已办)（绝对时间 = 明天 09:00），且全文无第二条相同内容行
```

重启应用 → 该待办仍为已办灰显（状态落盘）  
3\. 编辑识别（**同时验证原地改行**）：面板编辑该待办（未办状态那条）为「明天上午10点 交报告」→ 保存后出现建议条「检测到明天上午10点」→ 点[设为提醒] → 存储行**原行被改写**为 `【待办】明天上午10点 交报告 (提醒: ...10:00)`（正文=新内容），**且全文无新增【编辑】行**；重启应用 → 面板显示新内容（编辑内容已落盘，不丢失）  
4\. LLM 兜底：编辑为「周五下班前交」→ 无规则命中 → 触发 LLM（Debug 输出可见调用）→ 有 Key 弹建议条 / 无 Key 优雅降级不弹；编辑保存本身不受影响  
5\. 筛选：点「待办」→ 只显示未办待办（Open+Read，**不含已办**）；「待办」+「已办」→ 未办和已办都显示；「全部」恢复；切日期再切回 → 筛选保持  
6\. 右键：待办条目右键 → 「设置提醒/取消提醒」可用；取消提醒 → 存储行无 (提醒:)；普通笔记右键 → 两项不可用

Phase 2 完成 → git add -A && git commit -m "quest 6 phase 2: 面板待办徽标/已办/编辑识别/右键提醒/筛选"

### Phase 3：提醒（定时器 / 弹窗 / 汇总 / 角标）

#### 任务 3-1：ReminderService 新建（Services/ReminderService.cs）

> 操作位置：新建 Services/ReminderService.cs

```csharp
public class ReminderService
{
    public ReminderService(NoteService notes, AppSettings settings,
        Action<List<NoteEntry>> showDuePopups,      // 到点弹窗（同分钟合并传入）
        Action showDailySummary,                     // 每日汇总弹窗
        Action<int, bool> updateBadge);              // 角标(count, hasRead)
    public void Start();
    public void Stop();
    public void Refresh();                            // 数据变更后刷新角标与下次检查
}
```

实现要点：

- DispatcherTimer 每 30 秒 tick（**禁止 Thread.Sleep**）；**tick 内的 `LoadAllEntries()` 放后台线程（`await Task.Run`）**，避免 UI 线程每 30 秒全量读 md 文件卡顿（文件多时明显），结果回到 UI 线程处理
- 每 tick：`LoadAllEntries()` 过滤 `Type==Todo && TodoStatus==Open && DueTime 在 (now-30s, now+30s]` 且进程内未弹过（HashSet<string> 记忆 **`DueTime.ToString("yyyy-MM-dd HH:mm")|content`** 防重复——**key 必须用 DueTime 不用记录时间戳**，否则稍后提醒改了 DueTime 后 key 不变，该条永不重弹）→ 按分钟分组 → 每组调 showDuePopups（同分钟合并成一次弹窗列表）
- 每日汇总：`DailySummaryEnabled` 且 now.Hour/Minute 命中 `DailySummaryTime` 且**该触发分钟（yyyy-MM-dd HH:mm）当日未弹过** → showDailySummary（按触发分钟记，用户当天改时间后新分钟可再弹，验收 7 可重复验证）
- 角标：任何 tick/Refresh 计算 **未办待办总数 = Open+Read 数量** + 是否存在 Read（已暂缓）→ updateBadge（**count 是总数不是纯 Open**——点[已知悉]后该条转 Read，Open 归零但角标必须仍显示总数并变红，见 Phase 3 验收 5）
- 应用启动即 Start（启动时立刻 Refresh 一次角标，见 Phase 3 验收 9）；窗口事件（NotesChanged）触发 Refresh

#### 任务 3-2：单条提醒弹窗（Windows/ReminderPopupWindow.xaml/.cs 新建）

> 操作位置：新建 Windows/ReminderPopupWindow.xaml + .cs

- 窗口：深色（#1E1E1E）、无边框、Topmost、显示于悬浮球上方（读取 FloatBall 位置计算，或屏幕右下角悬浮球同侧）
- 内容：显示 `EditedContent ?? Content`（编辑过的待办显示新正文）+ 提醒时间；列表模式支持多条（同分钟合并）
- 按钮：每条「已完成」「稍后提醒」「已知悉」：
  - 已完成 → `UpdateTodo(e, newContent: e.EditedContent ?? e.Content, status: Done)` → 移除该条
  - 稍后提醒 → `UpdateTodo(e, newContent: e.EditedContent ?? e.Content, dueTime: e.DueTime + SnoozeMinutes 分钟)` → 移除该条（下次到点再弹；DueTime 变了 → 防重复 key 变 → 必重弹）
  - 已知悉 → `UpdateTodo(e, newContent: e.EditedContent ?? e.Content, status: Read)` → 移除该条
- 所有 UpdateTodo 正文一律用 `EditedContent ?? Content` 重建（防编辑过的待办被旧正文覆盖）
- 自动收起：DispatcherTimer `PopupAutoCloseSeconds` 秒后 Hide（收起时未处理的条保持原状态，靠角标/汇总兜底）
- 全部处理后自动 Hide

#### 任务 3-3：每日汇总弹窗（Windows/DailySummaryWindow.xaml/.cs 新建）

> 操作位置：新建 Windows/DailySummaryWindow.xaml + .cs

- 标题「今日待办汇总」，列出所有 `Type==Todo && TodoStatus==Open`（未读未办，不含已读）待办；每条显示 `EditedContent ?? Content`
- 每条按钮**按类型补全（两类都有「已完成」，防止无法直接标记完成）**：
  - 纯待办（无 DueTime）：\[已完成\]\[已知悉\]\[稍后查看]（稍后查看 = 仅收起不改变状态，下次汇总再弹）
  - 有时间待办：\[已完成\]\[顺延到明天\]\[已知悉]（已知悉 = 标 Read 挂起，与单条弹窗一致）
  - 已完成 → Done；顺延到明天 → DueTime + 1 天（`dueTime + TimeSpan.FromDays(1)`）；已知悉 → Read
- 空态文案「今天没有待处理事项」；同样自动收起（PopupAutoCloseSeconds）

#### 任务 3-4：分组汇总窗（Windows/TodoSummaryWindow.xaml/.cs 新建）

> 操作位置：新建 Windows/TodoSummaryWindow.xaml + .cs

- 分组：标题「待处理」（Open，未读未办）/「已暂缓」（Read）；每条显示 `EditedContent ?? Content`
- 待处理条目操作：同每日汇总（按钮按类型补全，见任务 3-3）
- 已暂缓条目操作：[恢复提醒]（Read→Open，重新进待处理）/[标记完成]（→Done）
- 空态：两组都空 → 「没有待办事项」

#### 任务 3-5：悬浮球角标 + 装配（Windows/FloatBall.xaml/.cs + MainWindow.xaml.cs）

> 操作位置：FloatBall.xaml 加角标控件、FloatBall.xaml.cs 加方法、MainWindow.xaml.cs 装配 ReminderService

- FloatBall.xaml：Ball 右上角叠加 TextBlock（`x:Name="Badge"`，圆角背景，默认隐藏）
- FloatBall.xaml.cs：

```csharp
public void SetBadge(int count, bool hasRead)
{
    // count = 未办待办总数（Open+Read，不是纯 Open）；count<=0 → 隐藏；否则显示数字；
    // hasRead（存在已读暂缓）→ 红底(#E24B4A) 否则绿底(#4CAF50)
}
```

- 点击角标（Badge MouseLeftButtonDown）→ **`e.Handled = true` 吞掉事件，防止冒泡触发悬浮球拖拽/点击唤出逻辑**（FloatBall 的 Ball_MouseLeftButtonDown 有 CaptureMouse+拖拽判定）→ 触发事件 `BadgeClicked`（MainWindow 订阅 → 打开 TodoSummaryWindow）
- **吸附态**：悬浮球吸附成 8px 条时 BallGrid 隐藏（FloatBall 现状），角标随之不可见——属正常行为，不额外处理（汇总入口仍可展开悬浮球后点角标）
- MainWindow.xaml.cs：创建 ReminderService（构造参数传弹窗调度：到点 → 打开 ReminderPopupWindow；汇总 → DailySummaryWindow；角标 → \_floatBall.SetBadge + BadgeClicked 打开 TodoSummaryWindow）；`_noteService.NotesChanged += () => _reminderService?.Refresh()`；**启动时创建后立即 Refresh() 一次（角标初始化，对应 Phase 3 验收 9）**；应用退出时 Stop()

### Phase 3 验收

1. `dotnet build -c Debug` → Build succeeded
2. 到点弹窗：设置里「稍后提醒间隔」临时改 1 分钟；输入「1分钟后 吃药」选待办保存 → 约 60 秒后悬浮球上方弹出该待办（现象级：弹出小窗含内容+时间）
3. 同分钟合并：两条同为「1分钟后」的待办 → 一个弹窗列出两条
4. 稍后提醒：弹窗点[稍后提醒] → 约 1 分钟后又弹（间隔=设置值；防重复 key 用 DueTime，改时间后 key 变 → 必重弹）
5. 已知悉：弹窗点[已知悉] → 不再弹；**角标变红显示数字（count=未办总数 Open+Read，该条转 Read 后总数仍含它）**；存储行该条含 `(状态: 已读)`
6. 角标：未办待办 2 条 → 角标显示「2」（绿色）；其中 1 条点[已知悉] → 角标仍显示「2」但变红（1 Open + 1 Read）；点角标（**不触发悬浮球拖拽**）→ 打开汇总窗，分「待处理/已暂缓」两组，已读的在「已暂缓」；点[恢复提醒] → 该条回「待处理」，角标恢复绿色
7. 每日汇总：设置时间改为当前时间 + 1 分钟，等 1 分钟 → 弹出汇总窗列未办待办（纯待办条目有\[已完成\]\[已知悉\]\[稍后查看]，有时间待办有\[已完成\]\[顺延到明天\]\[已知悉]）
8. 自动收起：弹窗出现后不操作 → 约 `PopupAutoCloseSeconds` 秒后自动消失
9. 应用重启后：到点但已过的提醒不弹（静默）；**启动时角标即初始化**（无需等第一个 tick），正确显示现存未办数

Phase 3 完成 → git add -A && git commit -m "quest 6 phase 3: 提醒定时器/单条弹窗/每日汇总/分组汇总窗/悬浮球角标"

### Phase 4：兼容（导出 / 同步 / 旧数据）

#### 任务 4-1：导出待办格式（Services/NoteExportService.cs）

> 操作位置：NoteExportService.cs 的 markdown 行输出逻辑

- 普通笔记：现状不变
- 待办导出：`- [ ] 内容 (提醒: ...)`（Open/Read）；`- [x] 内容 (提醒: ...)`（Done）；无提醒不输出 (提醒:)
- **导出即终态，不回导（有意设计）**：`- [ ]` 任务格式不符合 NoteLineRegex（只认 `- [HH:mm]` 行），导出的 md 拷回笔记目录或经导入功能回读时，待办行会被静默丢弃——这是"给人/外部任务工具看的展示格式"的定位，不承诺回读；普通笔记行维持现状可回读
- Json/Txt/Word 导出保持原样，**明确不携带待办类型信息**（解析时【待办】前缀已剥离进 Type 字段，Content 无标记）；如需类型信息用 Markdown 导出

#### 任务 4-2：同步兼容验证（Services/Sync/）

> 操作位置：无代码改动预期；跑验证

- SyncEngine 行级同步天然兼容（待办状态全在行文本内，同步 = 传行）
- 验证：双目录模拟（本机 + 临时副本目录走一遍 SyncEngine 或直接复制 md 文件模拟双机），确认待办行（含状态/提醒）在两侧一致
- 若发现行格式导致同步冲突（如状态改写触发重传）→ 记录到 BLOCKED.md，不阻塞本 Phase 其余任务

#### 任务 4-3：旧数据兼容验证

> 操作位置：无代码改动预期；跑验证

- 用 Phase 1 前的存量 md（无待办标记）打开面板 → 全部按普通笔记显示，无异常、无【待办】字样残留
- LoadNotes/LoadNoteCounts/搜索/导入/回收站恢复 对旧数据全部正常（回归基线重跑）

### Phase 4 验收

1. `dotnet build -c Debug` → Build succeeded
2. 导出：面板导出 markdown → 打开导出文件：待办行为 `- [ ] 内容`、已办行为 `- [x] 内容`；普通笔记行不变
3. 同步模拟：复制 md 到临时目录模拟双机 → 用 SyncEngine 逻辑比对 → 待办行两侧一致（含 (状态:)/(提醒:)）
4. 旧数据：打开 Phase 1 之前的历史 md 日期 → 全部按笔记显示，无异常
5. 总回归：回归基线 4 条全绿（构建/输入框保存/速览/设置）

Phase 4 完成 → git add -A && git commit -m "quest 6 phase 4: 导出格式+同步兼容+旧数据回归"

---

## 验收标准（总验收，全部 Phase 完成后执行）

> 按顺序执行，每条结果与期望一致。总验收 = Phase 1~4 验收全过 + 回归基线 4 条全绿 + 以下抽验：

```powershell
# 1. 最终构建
dotnet build -c Debug
# 期望: Build succeeded
```

1. 端到端：输入「明天上午9点 写日报」选待办保存 → 面板显示待办徽标 → 面板点徽标已办 → 重启应用 → 状态仍已办灰显 → 角标无该条（已办不计入未办总数）
2. 提醒闭环：设「1分钟后 喝水」（稍后提醒间隔=1）→ 弹窗 → 点[已知悉] → 角标变红含「1」（Read 计入总数）→ 点角标 → 汇总窗「已暂缓」组有该条 → [恢复提醒] → 回「待处理」，角标恢复默认色
3. 汇总闭环：每日汇总时间设为 now+1min → 弹汇总窗 → 有时间待办点[顺延到明天] → 存储行 (提醒:) 变为明天同时刻
4. 编辑闭环：编辑待办内容为「明天上午9点 写周报」→ 保存 → 重启 → 面板显示新内容且提醒时间正确（编辑内容不丢失，原地改行生效）
5. 回归：Alt+Space 输入框保存普通笔记 → 速览显示 → 删除进回收站 → 设置保存生效

---

## 交付

全部验收通过后执行：

```powershell
git add -A
git commit -m "quest 6: v3.5 待办与提醒系统（类型选择/时间识别/面板操作/提醒弹窗/汇总/角标/导出同步兼容）"
git checkout main
git merge --ff-only <开发分支>
git push <双远程>
```

分支纪律：全程在开发分支（v3.5 新建 `feature/todo-reminder`，或按当时确认的主线）开发；main 只做快进合并接收方；验收通过、用户确认后才合 main + 推送。
