# QUEST-4 — Phase 4：个性化收尾（AI 助手名称 + 图标自定义 + 主题配色 + 正式 .ico）

> Codex 执行手册 | 预估 2-3 天 | 全部完成后统一合并

## 0. 衔接到 QUEST-3

**前提**：QUEST-3 已完成且验收通过。你接手时：
- 面板标题栏布局：标题/日历按钮/刷新/AI 问答/导出/关闭；日历热力图可用
- 编辑/回填已落地"MD 只增不减"；输入面板语音输入 + 快捷键唤起修复完成
- 托盘菜单（`MainWindow.CreateTrayIcon`）含：显示设置/灵感速览/AI 问答/退出；悬浮球右键菜单（`FloatBall.Ball_MouseRightButtonDown`）含：灵感速览/沉浸记录/设置/AI 问答
- 面板标题栏 AI 问答按钮、悬浮球右键菜单、托盘菜单三处文案写死"AI 问答"
- `AppSettings.AiAssistantName` 已存在但未绑定任何 UI
- `ThemeService` 已存在（先读它，了解现有主题机制）
- 托盘图标：`CreateTrayIcon` 代码生成 32x32 深灰方块（#3A3A3A）；csproj 无 ApplicationIcon（exe 用 .NET 默认图标）

**本阶段任务**：①AI 助手名称自定义（两处入口同步）；②托盘图标自定义（设置上传图片）；③主题配色统一（新 UI 元素接入主题）；④正式 .ico（替换深灰方块 + exe 图标）。

## 1. 项目背景

**这是什么**：收尾轮。用户明确：悬浮球图标保持现状不改；要改的是**任务栏/托盘图标**（那个深灰方块）；AI 问答入口名称可自定义；v2.0 新增 UI 元素配色统一跟面板主题走。

**为什么做**：个性化是产品化的基本体面——默认图标是"黑坨"不专业，AI 助手有名字更像产品。

**不是什么**：不是改悬浮球（桌面悬浮窗）外观；不是改 exe 文件图标运行时逻辑（exe 图标编译期嵌入，这次配 .ico 后重新编译即生效）；不做主题编辑工具（只统一新元素跟随现有主题）。

## 2. 全局约束

### 必做
- 名称自定义：设置页输入框 → `AppSettings.AiAssistantName` → 面板标题栏按钮 + 托盘菜单项**两处文案同源读取**；名称 ≤10 字符
- 图标自定义：设置"外观"区上传图片（png/jpg ≤1MB）→ 复制到 `%AppData%\FocusCapture\custom_icon.png` → `AppSettings.CustomIconPath` 记录 → 运行时托盘图标从文件加载；**悬浮球图标不动**
- 主题统一：日历热力色（Phase 3 固定色）、AI 对话框、浮动工具条、输入面板按钮等新 UI 元素，跟随 `ThemeService` 现有机制取色
- 正式 .ico：生成一个 `Resources/app.ico` + csproj 配 `ApplicationIcon`（exe 编译期图标，默认兜底）
- 构建通过：`dotnet build -c Release`

### 禁止（违反就算失败）
- **不许**把 AI 助手名称写死回"AI 问答"（必须读 `AiAssistantName`）
- **不许**图标只当前会话生效不持久化（重启必须保持自定义图标）
- **不许**上传图片不校验格式/大小（非图片、超大文件必须拒绝）
- **不许**新 UI 元素用与主题无关的硬编码色（热力色必须接 ThemeService）
- **不许**用假 .ico（必须真实有效图标文件，exe 构建后可见）

## 3. 项目目录结构（本期新增文件标 ★）

```
FocusCapture/
├── QUEST-4.md                 # 本文件
├── Models/
│   └── AppSettings.cs         # 改：加 CustomIconPath（AiAssistantName 已有）
├── Resources/
│   └── app.ico                ★ 新建：正式默认图标
├── FocusCapture.csproj        # 改：加 <ApplicationIcon>
├── Services/
│   └── ThemeService.cs        # 改：提供主题色供日历/对话框等使用（先读现有实现）
└── Windows/
    ├── SettingsWindow.xaml(.cs) # 改：加"AI 助手名称"输入框 + "外观"图标选择
    ├── QuickViewWindow.xaml(.cs) # 改：AI 问答按钮文案读 AiAssistantName
    ├── CalendarWindow.xaml(.cs)  # 改：热力色接主题
    ├── AIDialogWindow.xaml(.cs)  # 改：配色接主题（最佳努力，不阻塞）
    ├── MainWindow.xaml.cs     # 改：托盘菜单项文案 + CreateTrayIcon 读自定义图标
    └── InputWindow.xaml(.cs)  # 改：语音按钮配色接主题（最佳努力）
```

## 4. 反作弊（点名具体偷懒姿势，用了就算失败）

1. **名称写死** — 入口文案直接写 `"AI 问答"` 不读配置。
   验证方式：设置里改名为"小助手"→ 面板按钮、**悬浮球右键菜单、托盘菜单**三处都必须显示"小助手"；代码审查入口文案必须引用 `_settings.AiAssistantName`。

2. **图标不持久化** — 上传后只存在内存，重启还原深灰方块。
   验证方式：上传自定义图标 → 重启应用 → 托盘图标**仍为自定义**。

3. **不校验上传** — 任意文件都能选，选完崩。
   验证方式：选一个 .txt 文件 → 必须被拒绝并提示格式错误。

4. **热力色硬编码不接主题** — Phase 3 的固定绿继续用，无视 ThemeService。
   验证方式：切换主题（若 ThemeService 支持）→ 日历热力色跟随变化；代码审查 CalendarWindow 取色走 ThemeService。

5. **假 .ico** — 放个 0 字节或无效文件，csproj 配了但构建失败或 exe 无图标。
   验证方式：`dotnet build` 通过；发布后的 exe 文件资源里能看到图标（文件管理器大图标模式可见，非默认 .NET 图标）。

## 5. 取舍

功能正确 > 代码整洁 > 一致性 > UI 美观 > 性能优化

- 功能正确：名称/图标改了必须真生效且持久化。
- 一致性：配色跟主题走，不搞局部美观破坏整体。
- 别过度设计：图标上传做"选择文件 → 复制 → 生效"最小闭环，不做预览/裁剪/多尺寸。

## 6. 未知处理

1. **上传的 png 转 Icon 失败**（部分 png 格式）→ 用 `System.Drawing.Bitmap` 加载 → `GetHicon()` 转 Icon；失败则提示"图片无法使用，请换一张"并回退默认。
2. **主题切换不即时**（ThemeService 机制限制）→ 热力色在日历打开时取当前主题即可，不要求实时热切换。
3. **生成 .ico 没有现成工具** → 写个一次性 C# 脚本（`System.Drawing` 画图标 → `Icon.FromHandle` 或 `Bitmap.Save` 转 ico），脚本跑完可删；若 System.Drawing 无法直接存 ico，用最小多尺寸 PNG 手工封装 .ico 文件（ico 容器格式简单，可手写字节）。
4. **一个任务卡住超过 30 分钟** → 写 BLOCKED.md，**跳过做下一个**。

## 7. 任务清单

### 第一步：AppSettings 加 CustomIconPath

> 操作位置：`Models/AppSettings.cs`

```csharp
// ── 外观 ──
public string CustomIconPath { get; set; } = ""; // 自定义托盘图标（%AppData%\FocusCapture\custom_icon.png）
```

`AiAssistantName` 已有（`"AI 问答"`），不改。

### 第二步：AI 助手名称自定义

> 操作位置：`Windows/SettingsWindow.xaml(.cs)`、`Windows/QuickViewWindow.xaml(.cs)`、`Windows/FloatBall.xaml.cs`、`MainWindow.xaml.cs`

- SettingsWindow 加"AI 助手名称"输入框（`AiAssistantNameInput`，MaxLength=10）→ TextChanged 写回 `_settings.AiAssistantName` + `_settings.Save()`
- **三处入口文案同源读取** `_settings.AiAssistantName`：
  1. QuickViewWindow 标题栏 AI 问答按钮 Text
  2. FloatBall 右键菜单"AI 问答"项文本（`Ball_MouseRightButtonDown` 构建 ContextMenu 处）
  3. MainWindow 托盘菜单项文本（`CreateTrayIcon` 的 `cm.Items.Add` 处）
- 名称下次打开/重建菜单时生效即可（三处菜单都是每次打开时动态构建，天然生效；QuickViewWindow 按钮名称在窗口构造时绑定）

### 第三步：托盘图标自定义

> 操作位置：`Windows/SettingsWindow.xaml(.cs)`、`MainWindow.xaml.cs`

**SettingsWindow 加"外观"区**：
- 按钮"选择图标"（标签：自定义任务栏/托盘图标，png/jpg ≤1MB）
- 点击 → `OpenFileDialog`（filter png/jpg）→ 校验扩展名 + 文件大小 ≤1MB → 复制到 `%AppData%\FocusCapture\custom_icon.png` → `_settings.CustomIconPath = 该路径` + `_settings.Save()` → 提示"重启应用后生效"（简单做法）或立即刷新托盘（最佳努力）
- 加"恢复默认"按钮 → 删 custom_icon.png + 清空 CustomIconPath + 保存

**MainWindow.CreateTrayIcon 改造**：
- 若 `_settings.CustomIconPath` 非空且文件存在 → `System.Drawing.Image.FromFile` → `GetHicon()` → Icon
- 否则 → 现有深灰方块逻辑（保留作兜底）
- 窗口图标：`this.Icon` 同步（若 MainWindow 有标题栏）——设置窗口/速览面板的 Window.Icon 也统一从自定义图标或 app.ico 加载（最佳努力，不强制）

### 第四步：主题配色统一

> 操作位置：先读 `Services/ThemeService.cs`，按其机制扩展

- 日历热力图 4 档色：从 ThemeService 取（无主题色则回退 Phase 3 固定绿）——**必须走 ThemeService 方法或属性，不许在 CalendarWindow 硬编码色值**
- AI 对话框背景/文字、浮动工具条、输入面板语音按钮：应用 ThemeService 提供的画刷（最佳努力——若 ThemeService 仅服务特定窗口，则新建元素引用同款色值常量，不阻塞）
- 验收点：切换主题后（若支持）日历弹窗配色变化

### 第五步：正式 .ico + exe 图标

> 操作位置：`Resources/app.ico`、`FocusCapture.csproj`

- 设计一个简洁图标（专注力主题：如圆形聚焦环 + 中心点，色系与产品一致，32x32 或 256x256 多尺寸）
- 生成 .ico 文件放 `Resources/app.ico`（方法见未知处理 3）
- csproj 加：

```xml
<PropertyGroup>
  <ApplicationIcon>Resources\app.ico</ApplicationIcon>
</PropertyGroup>
```

- 构建验证：`dotnet build -c Release` → exe 图标为 app.ico；发布后文件管理器可见

## 8. 验收标准

> 按顺序执行，每条必须与期望一致。

### 构建与静态检查

```bash
cd "项目根目录"
dotnet build -c Release
# 期望：Build succeeded

grep -n "AiAssistantName" Windows/QuickViewWindow.xaml.cs MainWindow.xaml.cs
# 期望：两处入口文案都引用它

grep -n "CustomIconPath" MainWindow.xaml.cs Windows/SettingsWindow.xaml.cs Models/AppSettings.cs
# 期望：三处都出现

grep -n "ApplicationIcon" FocusCapture.csproj
# 期望：输出 <ApplicationIcon>Resources\app.ico</ApplicationIcon>
```

### 手动验收（名称自定义）

1. 设置 → AI 助手名称改为"小助手"→ 保存 → 重启应用 → 期望：面板标题栏按钮、**悬浮球右键菜单、托盘菜单**三处都显示"小助手"
2. 名称输入超过 10 字符 → 期望：输入被截断（MaxLength）
3. 改回"AI 问答" → 期望：恢复

### 手动验收（图标自定义）

4. 设置 → 外观 → 选择一张 png → 期望：提示成功
5. 重启应用 → 期望：托盘图标为自定义图片
6. 选择 .txt 文件 → 期望：被拒绝并提示格式错误
7. 恢复默认 → 重启 → 期望：回到默认图标（app.ico）
8. **悬浮球（桌面悬浮窗）外观不变**（回归确认）

### 手动验收（主题配色）

9. 打开日历弹窗 → 期望：热力色与面板主题协调（非刺眼硬编码色）
10. AI 对话框背景与文字可读、与主题一致

### 手动验收（正式 .ico）

11. Release 构建的 exe → 文件资源管理器大图标视图 → 期望：显示 app.ico 图标（非 .NET 默认）
12. 任务栏固定应用后图标正常

### 回归验收

13. QUEST-1~3 全部功能正常：AI 对话/流式/回填、编辑/MD 只增不减、日历/刷新/多选删除、语音输入/快捷键、沉浸式语音识别

## 9. 交付

全部验收通过后：

```bash
git add -A
git commit -m "quest4: AI 助手名称/托盘图标自定义 + 主题配色统一 + 正式 .ico"
```

**不要合并到 main。** 全部 QUEST 验收通过后，由人类统一决定合并。

---

## 收尾提醒（给执行者）

- 若执行中发现 QUEST-1/2/3 有必须修正的问题（接口签名、逻辑错误），写 BLOCKED.md 记录并继续，不要擅自改动已验收代码
- PROGRESS.md 每完成一条任务追加一行 `- [x] 任务 N`
