# QUEST-3 — Phase 3：面板完善（刷新/日历热力图/多选删除/标题栏 + MD 同步 + 输入面板）

> Codex 执行手册 | 预估 4-6 天 | 验收通过后再进 QUEST-4

## 0. 衔接到 QUEST-2

**前提**：QUEST-2 已完成且验收通过。你接手时：
- AI 三板块对话框（`AIDialogWindow`）已可用，连续对话 + 流式 + 回填（`NoteService.AppendAiFill` 追加 `【AI 释义】` 行）
- 条目右键菜单有 AI 翻译/AI 搜索/复制/编辑/删除；编辑态浮动工具条；标题栏和托盘有 AI 问答入口
- 笔记编辑为 QUEST-1 的"行内覆盖"临时语义（`NoteService.UpdateNote`）
- 速览面板标题栏有日期选择器（`DaySelector`/`DateText`/`BuildDayOptions`）、全选/反选、删除已选按钮（仅全选/反选后出现）

**本阶段任务**：①把编辑语义从"覆盖"升级为"追加"（MD 只增不减落地）；②面板 UI 大改（刷新/日历/多选/标题栏）；③输入面板（语音/快捷键修复）。

## 1. 项目背景

**这是什么**：面板体验完善轮。让灵感速览从"能看"变成"好用"：实时刷新、日历热力图找历史记录、多选删除顺手；MD 文件回归"不可变账本"；输入面板补语音和快捷键体验。

**为什么做**：用户实际使用痛点——面板不实时刷新（只能关掉重开）、删笔记要先全选再删、历史记录无入口、快捷键唤起输入框要鼠标点一下、没有内容时面板卡几秒才消失。

**不是什么**：不是重构存储结构（行式 MD 保留）；不做删除恢复 UI（软删除记录保留但本阶段无恢复入口）；不做多用户。

## 2. 全局约束

### 必做
- **MD 文件只增不减**是本阶段核心：编辑保存 = **追加带标记的新行**，原行永不动；AI 回填沿用 QUEST-2 的追加格式；删除笔记 = 物理删除对应行（用户拍板的选项 A）+ 软删除记录
- 面板展示层与存储层分离：**存储层只增不减，展示层合并**（编辑行覆盖显示、AI 行作为子条目），具体规则见任务清单
- 刷新：标题栏刷新按钮 + 面板打开/焦点回归自动刷新
- 日历：单月视图翻页 + 4 档热力色 + 点日期立即刷新 + 弹窗内"今天"按钮 + 面板重开默认显示当天
- 多选删除：任一 checkbox 勾选即显示"删除已选"按钮
- 标题栏：**不直接显示日期文字**；左 = 标题 + 日历按钮；右 = 刷新 + AI 问答 + 导出 + 关闭
- 语音输入：点开始/点停止，识别结果追加到光标处，与沉浸式输入互斥
- 快捷键唤起修复：Focus 输入框 + 激活 IME + 失焦立即关闭
- 构建通过：`dotnet build -c Release`

### 禁止（违反就算失败）
- **不许**继续用"覆盖原行"实现编辑保存（QUEST-1 临时语义本阶段必须替换为追加）
- **不许**用 `File.WriteAllText` 全文件重写来删行/改行（破坏其他内容）
- **不许**在日历热力图里造假数据（必须从 MD 文件统计真实笔记数）
- **不许**保留"全选/反选才显示删除按钮"的旧逻辑（任一勾选即显示）
- **不许**让标题栏继续显示日期文字（DateText 必须移除或改用途）
- **不许**语音输入 new 一个独立 VoiceService 绕开互斥检查
- **不许**绕过 Focus/IME 修复（快捷键唤起后必须能直接打字）

## 3. 项目目录结构（本期新增文件标 ★）

```
FocusCapture/
├── QUEST-3.md                 # 本文件
├── Models/
│   └── NoteEntry.cs           # 改：加 AiFills / EditedContent 展示字段
├── Services/
│   ├── NoteService.cs         # 改：编辑追加行、删除行、LoadNoteCounts、ParseNotes 识别标记行
│   └── DeletedNoteService.cs  # 复用（软删除，不改）
└── Windows/
    ├── CalendarWindow.xaml(.cs) ★ 新建：日历热力图弹窗
    ├── QuickViewWindow.xaml(.cs) # 改：标题栏/刷新/多选/右键/浮动工具条联动
    ├── InputWindow.xaml(.cs)   # 改：语音输入按钮 + 快捷键唤起修复
    └── VoiceInputWindow.xaml.cs # 改：会话结束/关闭时确认互斥标志清理（已有 ImmersiveSessionService.Stop）
```

## 4. 反作弊（点名具体偷懒姿势，用了就算失败）

1. **编辑保存继续覆盖原行** — 图省事沿用 QUEST-1 的 `UpdateNote` 不换追加语义。
   验证方式：手动编辑一条笔记 → 打开 MD 文件 → 原行**必须仍在**，新增一行 `【编辑】...`。代码审查：`UpdateNote` 删除或改为追加。

2. **删行全文件重写** — `File.ReadAllText` + `Regex.Replace` + 写回，把 `## 沉浸记录` 块误删。
   验证方式：文件里有沉浸记录块时删除一条普通笔记 → 沉浸记录块完整。

3. **日历热力图造假** — 直接生成 0 数据或随机数，不扫 MD 文件。
   验证方式：手动在某天 MD 文件造 7 条笔记 → 日历该天显示"最深色档"。

4. **删除按钮旧逻辑残留** — 只在全选/反选后 `Visibility` 切换。
   验证方式：手动勾选**一条** → 删除已选按钮必须出现；取消勾选 → 隐藏。

5. **标题栏日期文字残留** — DateText 还在更新显示日期。
   验证方式：打开面板 → 标题栏无日期文字（日期只在日历弹窗内）。

6. **语音输入绕互斥** — 直接 new VoiceService 不查 ImmersiveSessionService.IsActive。
   验证方式：沉浸式输入进行中 → 点输入面板语音按钮 → 必须弹提示拦截。

7. **快捷键唤起不做 Focus** — Show() 完事，输入框不抢焦点。
   验证方式：快捷键唤起后**直接打字** → 内容进输入框（无需鼠标点击）。

## 5. 取舍

功能正确 > 体验流畅 > 存储安全 > 代码整洁 > UI 美观

- 功能正确：MD 只增不减是产品承诺，编辑/删除/回填三类操作必须严格按规则，宁可代码多几行。
- 存储安全：文件读写用行级定位，任何写操作不得破坏 `## 沉浸记录` 块和标签文件结构。
- UI 先有再美：日历弹窗用简单 Grid，热力色本阶段固定色，Phase 4 接主题。

## 6. 未知处理

1. **行级定位找不到目标行**（时间戳被改/文件被外部编辑）→ 返回 false，UI 提示"笔记可能已被外部修改"，不破坏文件。
2. **日历统计性能**（文件多）→ 每个文件只读一次，正则匹配行数；月统计在 `LoadNoteCounts` 内完成，不做跨文件重复扫描。
3. **IME 激活不生效**（个别输入法）→ 至少保证 `Focus()` 生效（焦点在输入框即可打字），IME 弹不出时提示用户手动切输入法；`Imports`/`SetIsInputMethodEnabled` 做最佳努力。
4. **一个任务卡住超过 30 分钟** → 写 BLOCKED.md，**跳过做下一个**。

## 7. 任务清单

### 第一步：NoteEntry 展示字段 + ParseNotes 识别标记行

> 操作位置：`Models/NoteEntry.cs`、`Services/NoteService.cs`

**NoteEntry 加字段**（仅展示层，不参与存储序列化）：

```csharp
public string? EditedContent { get; set; }   // 编辑后的内容（面板优先显示）
public List<string> AiFills { get; set; } = new(); // AI 回填内容列表（子条目展示）
```

**ParseNotes 增强**（`Services/NoteService.cs`）：解析行时识别标记（写在行尾来源区，或行内容前缀）：
- 行内容以 `【AI 释义】` 开头 → 提取内容，追加到**最近一条同文件、时间相近（±60s）的原笔记**的 `AiFills`
- 行内容以 `【编辑】` 开头 → 提取内容，设置最近原笔记的 `EditedContent`
- 无标记 → 正常 entry
- 找不到相近原笔记的标记行 → 作为独立 entry（内容去掉标记前缀），Tag 置空

### 第二步：编辑语义升级为"追加"（MD 只增不减）

> 操作位置：`Services/NoteService.cs`、`Windows/QuickViewWindow.xaml.cs`

**替换 QUEST-1 的 `UpdateNote` 临时语义**：删除 `UpdateNote`（或改为私有助手），新增：

```csharp
/// <summary>编辑保存：追加带标记的新行，原行不动（MD 只增不减）</summary>
public bool AppendEdit(NoteEntry entry, string newContent)
```

- 新行格式：`- [{DateTime.Now:yyyy-MM-dd HH:mm}] 【编辑】{newContent} — 来源: 手动编辑`
- 写入位置：与 entry 同文件，追加到文件末尾
- 沉浸式锁定：`ImmersiveSessionService.IsLocked(entry.Timestamp)` → 返回 false

**QuickViewWindow 编辑保存逻辑**：从调 `UpdateNote` 改为调 `AppendEdit`；保存后 `Refresh()`。面板展示：编辑过的笔记显示 `EditedContent ?? Content`（NoteEntryViewModel.Content 属性改为优先返回 EditedContent）。

### 第三步：刷新按钮 + 自动刷新

> 操作位置：`Windows/QuickViewWindow.xaml` + `.cs`

- 标题栏加刷新按钮（图标按钮，放 AI 问答旁）→ `Refresh()`
- `Refresh()` 已是公开方法；`OnActivated`/`Window_Activated` 事件里调用 `Refresh()`（焦点回归自动刷新）
- 打开时 `Show()` 后已加载（保持）

### 第四步：日历热力图（CalendarWindow）

> 操作位置：`Windows/CalendarWindow.xaml(.cs)` ★、`Services/NoteService.cs`

**NoteService 加方法**：

```csharp
/// <summary>统计指定月份每天笔记数（含标签文件与当天灵感文件）</summary>
public Dictionary<DateTime, int> LoadNoteCounts(int year, int month)
```

实现：遍历 `NotesPath` 下 `*.md`，用 `ParseNotes` 的正则解析行内时间戳，过滤 `year/month`，计数。**注意排除已软删除**（`_deletedService.IsDeleted`）。

**CalendarWindow**（弹窗，Owner=QuickViewWindow）：
- 单月视图：7 列 Grid（日一二三四五六）+ 星期表头；月份标题居中 + 左/右箭头翻月 + 右上"今天"按钮
- 每个日期格：日期数字 + 热力色背景（数据来自 `LoadNoteCounts`），4 档：
  - 0 条 = 无色（默认背景）
  - 1-2 条 = 浅绿 `#C8E6C9`
  - 3-5 条 = 中绿 `#81C784`
  - 6+ 条 = 深绿 `#388E3C`
  - （本阶段固定色，Phase 4 接主题色）
- 点击有笔记的日期 → `DialogResult` + 返回选中日期；点击无笔记日期同样可选
- 事件：`event Action<DateTime>? DateSelected`
- 打开时默认定位到当前选中日期所在月

**QuickViewWindow 接入**：标题栏日期选择器（`DaySelector`）替换为**日历按钮**：点击 → 打开 CalendarWindow → 选中日期 → `_selectedDate = 该日` + `ReloadNotes()`。移除 `DaySelector`/`BuildDayOptions`/`DateText` 日期文字展示逻辑。

### 第五步：多选删除

> 操作位置：`Windows/QuickViewWindow.xaml` + `.cs`、`Services/NoteService.cs`

**NoteService 加方法**：

```csharp
/// <summary>删除笔记：物理删除 MD 对应行（选项 A）+ 软删除记录</summary>
public bool DeleteNote(NoteEntry entry)
```

实现：
- 行级定位（同 UpdateNote 定位思路）删除该行，其余内容原样写回（**禁止全文件重写逻辑**，只删目标行）
- 调用 `_deletedService.MarkDeleted(entry)`（软删除，保持 v0.1 机制）
- 若 entry 有 `AiFills`/`EditedContent` 关联的标记行，一并删除

**QuickViewWindow 逻辑改造**：
- "删除已选"按钮显示条件：`_viewModels.Any(v => v.IsSelected)`（任一勾选即显示，替换旧的仅全选/反选后显示逻辑）
- 点击删除 → 确认弹窗 → 遍历选中项 `DeleteNote` → `Refresh()`
- checkbox 勾选/取消 → 更新按钮可见性

### 第六步：标题栏布局改造

> 操作位置：`Windows/QuickViewWindow.xaml`

最终布局（从左到右）：标题"灵感速览" → 日历按钮（图标/文字"日历"）→ 弹性空间 → 刷新按钮 → AI 问答按钮 → 导出按钮 → 关闭按钮。

- **移除**：日期文字（DateText）、日期选择器（DaySelector）、`DateText.Text` 更新代码
- AI 问答按钮沿用 QUEST-2 的入口逻辑
- 面板重开默认 `_selectedDate = DateTime.Today`（现有逻辑保持）

### 第七步：输入面板语音输入

> 操作位置：`Windows/InputWindow.xaml` + `.cs`

- 输入面板加语音按钮（麦克风图标，输入框旁）
- 点击逻辑（写死）：
  1. 若 `ImmersiveSessionService.IsActive` → 弹提示"沉浸式输入正在进行语音识别，暂不可用"（互斥）
  2. 否则点击开始录音（按钮变红/状态图标变化），再次点击停止
  3. 复用 `VoiceService`：InputWindow 持有一个 `VoiceService` 实例（构造时创建，窗口关闭时 Dispose）
  4. `FinalText` 事件 → 结果**追加**到输入框光标处（`InputBox.CaretIndex` 处插入）
  5. `Error` 事件 → 提示错误
- 参考 `VoiceInputWindow` 的 VoiceService 用法（sherpa-onnx 模型路径 `%LocalAppData%\FocusCapture\models\firered-asr2-ctc\`，查看 VoiceInputWindow 初始化代码复用同一套配置）

### 第八步：快捷键唤起修复

> 操作位置：`Windows/InputWindow.xaml.cs`

- `Show()` override（已有）中补：
  1. `Activate()` 确保窗口激活
  2. `InputBox.Focus()` + `Keyboard.Focus(InputBox)`（Dispatcher.BeginInvoke 延迟一拍，窗口完全显示后再抢焦点）
  3. IME：`InputMethod.SetIsInputMethodEnabled(InputBox, true)` 最佳努力
- 关闭逻辑：确认 `Window_Deactivated`（已有）→ `Hide()` 正常工作；检查 `_idleTimer`（超时兜底）——**若它导致"空内容卡几秒"**：失焦立即隐藏已是主路径，`_idleTimer` 改为兜底（如 10s 无输入才触发），不抢在 Deactivated 前面
- 悬浮球唤起路径（`MainWindow` 里 `InputRequested → Show()`）保持同样行为（共用 `Show()` 即可）

## 8. 验收标准

> 按顺序执行，每条必须与期望一致。

### 构建与静态检查

```bash
cd "项目根目录"
dotnet build -c Release
# 期望：Build succeeded

grep -n "AppendEdit" Services/NoteService.cs Windows/QuickViewWindow.xaml.cs
# 期望：两处都出现（编辑已改为追加语义）

grep -n "UpdateNote" Services/NoteService.cs Windows/QuickViewWindow.xaml.cs
# 期望：无输出（覆盖语义已移除）
```

### 手动验收（刷新 + 标题栏）

1. 打开速览面板 → 标题栏**无日期文字**；布局：标题/日历按钮/刷新/AI 问答/导出/关闭
2. 用其他方式新增一条笔记（如剪贴板捕获）→ 切回面板（面板在前台激活）→ 期望：自动刷新出现新笔记
3. 点刷新按钮 → 期望：列表重新加载

### 手动验收（日历热力图）

4. 点日历按钮 → 弹出日历，单月视图
5. 在某天 MD 文件造 7 条笔记 → 重开日历 → 该天格子为**最深色**
6. 点有笔记的日期 → 期望：弹窗关闭，列表刷新为该日笔记
7. 点"今天"按钮 → 回到当月并选中今天
8. 左右箭头翻月 → 月份切换正常

### 手动验收（多选删除）

9. 手动勾选**一条** → 期望："删除已选"按钮出现（旧逻辑已废）
10. 点删除 → 确认 → 期望：列表移除该条；MD 文件对应行**已物理删除**；软删除记录存在
11. 文件里含 `## 沉浸记录` 块时删除普通笔记 → 期望：沉浸记录块完整无损

### 手动验收（MD 只增不减）

12. 编辑一条笔记（Ctrl+S 保存）→ 打开 MD → 期望：**原行仍在**，文件末尾**新增** `【编辑】...` 行
13. 面板上该笔记显示**编辑后的内容**（不是原内容）
14. AI 回填一条 → MD 新增 `【AI 释义】` 行，原行不动；面板上该笔记下显示 AI 子条目

### 手动验收（输入面板）

15. 输入面板点语音按钮 → 开始录音（状态变化）→ 说话 → 再点停止 → 期望：识别结果**追加到输入框光标处**
16. 沉浸式输入进行中 → 点语音按钮 → 期望：弹提示拦截
17. 快捷键唤起输入面板 → **直接打字**（不点鼠标）→ 期望：内容进入输入框
18. 快捷键唤起后点输入框外部 → 期望：**立即消失**（不等几秒）
19. 悬浮球唤起 → 点外部 → 立即消失（回归）

### 回归验收

20. AI 三板块对话/流式/回填正常；托盘 AI 问答正常；悬浮球/剪贴板捕获/沉浸式语音正常

## 9. 交付

完成后执行：

```bash
git add -A
git commit -m "quest3: 面板刷新/日历热力图/多选删除/标题栏 + MD 只增不减落地 + 输入面板语音与快捷键修复"
```

**不要合并到 main。** 验收通过后再进 QUEST-4。
