# DESIGN.md — FocusCapture 网站设计规范

> 适用于 FocusCapture 官方网站。技术栈：纯 HTML + Tailwind CSS（CDN）。
> 设计哲学：克制、深色聚焦、开发者气质、品牌一致。

---

## 1. Visual Theme & Atmosphere（视觉主题与氛围）

**设计哲学**：一座深夜工作台——克制、聚焦、零干扰。用户从网站到产品的体验应无缝衔接。

| 维度 | 描述 |
|---|---|
| 整体基调 | 深色聚焦（Dark Focus）—— 沉静的暗夜背景，让内容浮起 |
| 视觉气质 | 开发者工具、克制、可信赖、不喧哗 |
| 核心特征关键词 | 暗夜聚焦 · 微光指引 · 克制留白 · 节奏感 · 截图说话 |
| 光影质感 | 纯扁平 + 微光描边；无渐变、无毛玻璃、无装饰阴影 |
| 品牌延续 | 沿用 FocusCapture 应用本身的深黑底 + 绿色强调色 + 蓝色 CTA |

**对比参照**（用于保持气质一致）：
- **Linear.app**：开发者工具的克制感
- **Raycast.com**：暗色聚焦 + 截图驱动
- **Vercel.com**：黑底 + 几何节奏
- **Tailscale.com**：文档型工具的清晰排版

---

## 2. Color Palette & Roles（调色板与角色）

> 网站仅使用 **Dark 模式**。所有色值定义如下。

### Primary Colors（主色）

| 角色 | HEX | CSS 变量 | 使用场景 |
|---|---|---|---|
| 主背景 | `#0A0E14` | `--bg-base` | 页面 body 背景 |
| 次背景 | `#11161F` | `--bg-elevated` | 卡片、Section 分块 |
| 三级背景 | `#1A2030` | `--bg-overlay` | 输入框、二级浮层 |

### Brand & Accent Colors（品牌与强调）

| 角色 | HEX | CSS 变量 | 使用场景 |
|---|---|---|---|
| 品牌绿 | `#4ADE80` | `--brand-green` | 标题点缀、Hero 强调、Tag |
| 品牌绿深 | `#10B981` | `--brand-green-deep` | hover 态 |
| 品牌绿浅 | `#86EFAC` | `--brand-green-soft` | 高亮文字、激活态 |
| 主 CTA 蓝 | `#3B82F6` | `--cta-blue` | 主按钮、链接 |
| CTA 蓝 hover | `#2563EB` | `--cta-blue-hover` | 按钮 hover |
| 链接蓝 | `#60A5FA` | `--link-blue` | 行内链接 |

### Neutral / Gray Scale（中性灰阶）

| 角色 | HEX | CSS 变量 | 使用场景 |
|---|---|---|---|
| 主文字 | `#E5E7EB` | `--text-primary` | 标题、正文 |
| 次文字 | `#9CA3AF` | `--text-secondary` | 描述、辅助 |
| 三级文字 | `#6B7280` | `--text-tertiary` | 提示、Footer 版权 |
| 反白文字 | `#0A0E14` | `--text-inverse` | 按钮内文字 |
| 边框灰 | `#1F2937` | `--border-default` | 卡片描边 |
| 边框强 | `#374151` | `--border-strong` | 聚焦、激活态 |

### Surface & Borders（表面与边框）

| 角色 | HEX | CSS 变量 | 使用场景 |
|---|---|---|---|
| 卡片表面 | `#11161F` | `--surface-card` | 默认卡片背景 |
| 卡片悬浮 | `#1A2030` | `--surface-card-hover` | hover 时 |
| 分隔线 | `#1F2937` | `--divider` | Section 分隔 |

### Semantic Colors（语义色）

| 角色 | HEX | CSS 变量 | 使用场景 |
|---|---|---|---|
| 成功 | `#22C55E` | `--semantic-success` | 「已完成」提示 |
| 警告 | `#F59E0B` | `--semantic-warning` | 注意事项 |
| 错误 | `#EF4444` | `--semantic-error` | 错误提示 |
| 信息 | `#3B82F6` | `--semantic-info` | 一般信息 |

### Shadow Colors（阴影色）

| 角色 | rgba | CSS 变量 | 使用场景 |
|---|---|---|---|
| 微阴影 | `rgba(0, 0, 0, 0.3)` | `--shadow-color-sm` | 卡片底部 |
| 中阴影 | `rgba(0, 0, 0, 0.5)` | `--shadow-color-md` | 悬浮卡片 |
| 强阴影 | `rgba(0, 0, 0, 0.7)` | `--shadow-color-lg` | Modal |

---

## 3. Typography Rules（排版规则）

### Font Family（字体族）

```css
--font-sans: "Inter", "PingFang SC", "Microsoft YaHei", "Source Han Sans CN", system-ui, -apple-system, sans-serif;
--font-mono: "JetBrains Mono", "Fira Code", "Cascadia Code", "Consolas", monospace;
```

- 英文/数字：`Inter`
- 中文：`PingFang SC`（macOS） → `Microsoft YaHei`（Windows） → `Source Han Sans CN`（通用兜底）
- 代码：`JetBrains Mono`

### Type Scale（字号层级）

| 层级 | Size (px/rem) | Weight | Line Height | Letter Spacing | 用途 |
|---|---|---|---|---|---|
| Display Hero | `64px / 4rem` | 600 | 1.1 | -0.02em | Hero 主标题 |
| H1 | `48px / 3rem` | 600 | 1.15 | -0.02em | 页面大标题 |
| H2 | `36px / 2.25rem` | 600 | 1.2 | -0.01em | Section 标题 |
| H3 | `28px / 1.75rem` | 600 | 1.3 | -0.01em | 卡片大标题 |
| H4 | `20px / 1.25rem` | 500 | 1.4 | 0 | 小节标题 |
| Body Large | `18px / 1.125rem` | 400 | 1.6 | 0 | 引导段、描述 |
| Body | `16px / 1rem` | 400 | 1.6 | 0 | 正文 |
| Body Small | `14px / 0.875rem` | 400 | 1.5 | 0 | 辅助文字 |
| Caption | `12px / 0.75rem` | 500 | 1.4 | 0.02em | Tag、徽标 |
| Code Inline | `14px / 0.875rem` | 400 | 1.5 | 0 | 行内代码 |
| Code Block | `14px / 0.875rem` | 400 | 1.7 | 0 | 代码块 |

### 设计哲学

- **只用 400 / 500 / 600 三个字重**，避免 700+ 视觉过重
- **负字距给大标题**（Hero / H1），让英文标题更紧凑
- **正字距给小字**（Caption），提升可读性
- **行高按内容密度调整**：标题紧凑（1.1-1.3），正文宽松（1.5-1.7）
- **代码用等宽字体**，行高 1.7 让代码块呼吸感更好

---

## 4. Component Stylings（组件样式）

### Buttons（按钮）

| 变体 | 背景 | 文字 | 边框 | 圆角 | Padding | Hover |
|---|---|---|---|---|---|---|
| **Primary** | `#3B82F6` | `#FFFFFF` | 无 | `8px` | `12px 24px` | bg → `#2563EB` |
| **Secondary** | `#11161F` | `#E5E7EB` | `1px #1F2937` | `8px` | `12px 24px` | bg → `#1A2030`, border → `#374151` |
| **Ghost** | 透明 | `#E5E7EB` | `1px #1F2937` | `8px` | `12px 24px` | bg → `#11161F` |
| **Danger** | `#EF4444` | `#FFFFFF` | 无 | `8px` | `12px 24px` | bg → `#DC2626` |
| **Brand Green** | `#4ADE80` | `#0A0E14` | 无 | `8px` | `12px 24px` | bg → `#10B981` |

通用规则：
- 字体：500 weight，15px（按钮专用）
- 最小宽度：120px
- 圆角：8px（统一）
- 过渡：150ms ease-out（背景、边框、阴影）

### Cards（卡片）

```css
.card {
  background: #11161F;       /* --surface-card */
  border: 1px solid #1F2937;  /* --border-default */
  border-radius: 12px;
  padding: 24px;
  box-shadow: 0 4px 16px rgba(0, 0, 0, 0.3);
  transition: border-color 150ms ease-out, transform 150ms ease-out;
}
.card:hover {
  border-color: #374151;      /* --border-strong */
}
```

### Inputs（输入框）

```css
.input {
  background: #11161F;
  border: 1px solid #1F2937;
  border-radius: 8px;
  padding: 10px 14px;
  color: #E5E7EB;
  font-size: 15px;
  transition: border-color 150ms ease-out;
}
.input::placeholder { color: #6B7280; }
.input:focus {
  outline: none;
  border-color: #3B82F6;
  box-shadow: 0 0 0 3px rgba(59, 130, 246, 0.2);
}
```

### Navigation（导航）

```css
.nav {
  background: rgba(10, 14, 20, 0.85);
  backdrop-filter: blur(12px);     /* 半透明深底毛玻璃，仅导航 */
  border-bottom: 1px solid #1F2937;
  height: 64px;
  position: sticky;
  top: 0;
  z-index: 50;
}
.nav-link.active { color: #86EFAC; }
.nav-link:hover { color: #E5E7EB; }
```

### Badges / Tags（徽标）

| 变体 | 背景 | 文字 | 用途 |
|---|---|---|---|
| **Default** | `#1F2937` | `#9CA3AF` | 通用标签 |
| **Brand** | `rgba(74, 222, 128, 0.15)` | `#86EFAC` | 平台标签（Windows / WPF） |
| **Success** | `rgba(34, 197, 94, 0.15)` | `#22C55E` | 状态标识 |
| **Info** | `rgba(59, 130, 246, 0.15)` | `#60A5FA` | 信息标签 |

通用：圆角 `6px`，padding `4px 10px`，字号 `12px`，weight `500`。

### Modals / Dialogs（弹窗）

```css
.modal-backdrop {
  background: rgba(0, 0, 0, 0.7);
  z-index: 100;
}
.modal-content {
  background: #11161F;
  border: 1px solid #1F2937;
  border-radius: 12px;
  padding: 32px;
  max-width: 600px;
  box-shadow: 0 24px 48px rgba(0, 0, 0, 0.7);
}
```

---

## 5. Layout Principles（布局原则）

### Spacing System（间距系统）

**基础单位**：4px。倍数：4 / 8 / 12 / 16 / 24 / 32 / 48 / 64 / 96。

| Token | px | 用途 |
|---|---|---|
| `space-1` | 4px | 极小间距 |
| `space-2` | 8px | 图标与文字 |
| `space-3` | 12px | 紧凑元素 |
| `space-4` | 16px | 段落内行 |
| `space-6` | 24px | 卡片内边距 |
| `space-8` | 32px | 卡片之间 |
| `space-12` | 48px | Section 内 |
| `space-16` | 64px | Section 之间 |
| `space-24` | 96px | 页面级留白 |

### Grid System（栅格）

- **列数**：12 列
- **列间距**：24px
- **最大宽度**：1200px
- **容器内边距**：桌面 48px / 平板 32px / 手机 16px

### Container（容器）

```css
.container {
  max-width: 1200px;
  margin: 0 auto;
  padding: 0 48px;
}
@media (max-width: 768px) {
  .container { padding: 0 32px; }
}
@media (max-width: 640px) {
  .container { padding: 0 16px; }
}
```

### Section Spacing（区块间距）

- Hero 与首屏 Section：`space-24` (96px)
- 同性质 Section 之间：`space-16` (64px)
- Section 内子区块：`space-12` (48px)
- 卡片网格间距：`space-8` (32px)

### 留白哲学

> 「以稀为贵」—— 与产品界面同源。深色背景里大量留白不是空旷，是给内容呼吸的暗夜。

---

## 6. Depth & Elevation（深度与层级）

### Shadow System（阴影系统）

| 层级 | box-shadow CSS | 用途 |
|---|---|---|
| `shadow-xs` | `0 1px 2px rgba(0, 0, 0, 0.3)` | Tag、徽标 |
| `shadow-sm` | `0 2px 8px rgba(0, 0, 0, 0.3)` | 按钮按下 |
| `shadow-md` | `0 4px 16px rgba(0, 0, 0, 0.3)` | 卡片 |
| `shadow-lg` | `0 12px 32px rgba(0, 0, 0, 0.5)` | 悬浮卡片 |
| `shadow-xl` | `0 24px 48px rgba(0, 0, 0, 0.7)` | Modal |

> **不使用**模糊半径 > 48px 的阴影；不使用渐变阴影；不使用彩色阴影。

### Surface Layers（表面层级）

| 层级 | 颜色 | 用途 |
|---|---|---|
| L0 | `#0A0E14` | 页面背景（最底层） |
| L1 | `#11161F` | 卡片、Section |
| L2 | `#1A2030` | 输入框、二级浮层 |
| L3 | `#1F2937` | Modal（最上层） |

### Z-index Scale（层级数值）

| 层级 | 数值 | 用途 |
|---|---|---|
| `z-0` | 0 | 默认 |
| `z-10` | 10 | 固定元素 |
| `z-20` | 20 | Dropdown |
| `z-30` | 30 | Sticky 导航 |
| `z-40` | 40 | Modal 遮罩 |
| `z-50` | 50 | Modal 内容 |

### Backdrop Effects（背景特效）

仅在导航条使用：
```css
backdrop-filter: blur(12px);
background: rgba(10, 14, 20, 0.85);
```
**其他地方不使用**毛玻璃——保持暗夜的纯粹。

---

## 7. Do's and Don'ts（设计规范与禁忌）

### Do's（推荐）

1. ✅ **保持品牌一致**：网站与应用共享同一套色彩语言（深底 + 绿强调 + 蓝 CTA）
2. ✅ **截图优先于描述**：能用截图说清的事，不用文字
3. ✅ **行动按钮永远显眼**：Hero、每屏 CTA、Footer 都有「下载」按钮
4. ✅ **行高宽松**：正文 1.6+ 行高，让深色背景下阅读不疲劳
5. ✅ **代码块用等宽字体**：build 命令、热键都要用 `<pre><code>`
6. ✅ **结构化数据**：每个关键板块都有清晰的 H1 → H2 → H3 标题层级

### Don'ts（避免）

1. ❌ **不要用渐变色**：禁用 linear-gradient / radial-gradient
2. ❌ **不要用毛玻璃**：除导航外所有元素保持纯色背景
3. ❌ **不要用 emoji 装饰**：用 CSS 形状或 SVG path 替代
4. ❌ **不要低对比度文字**：灰字最低 #9CA3AF，禁止 #4B5563 或更浅
5. ❌ **不要花哨动画**：仅用 150-300ms 的 fade-in / slide-up
6. ❌ **不要 JS 渲染关键内容**：所有文字必须在 HTML 源码里可直接看到

---

## 8. Responsive Behavior（响应式行为）

### Breakpoints（断点）

| 名称 | 宽度 | 用途 |
|---|---|---|
| `mobile` | `≤ 640px` | 手机竖屏 |
| `tablet` | `641-1024px` | 平板、小笔记本 |
| `desktop` | `1025-1440px` | 标准桌面 |
| `wide` | `> 1440px` | 大屏显示器 |

### Touch Targets（触摸目标）

- **最小尺寸**：44 × 44px
- **按钮间距**：至少 8px

### 折叠策略

| 元素 | Mobile | Tablet | Desktop |
|---|---|---|---|
| 导航 | 汉堡菜单（点击展开） | 横排 + 部分折叠 | 完整横排 |
| Hero 截图 | 单列、占满宽 | 双列 | 双列 + 浮起动效 |
| 卡片网格 | 1 列 | 2 列 | 3-4 列 |
| 热键表 | 横向滚动 | 完整展示 | 完整展示 |
| Footer | 单列堆叠 | 2 列 | 4 列 |

### Font Scaling（字体缩放）

- **桌面**：完整字号（Hero 64px）
- **平板**：Hero 缩到 48px，H1 缩到 36px
- **手机**：Hero 缩到 36px，H1 缩到 28px，正文保持 16px

---

## 9. Agent Prompt Guide（AI 代理提示指南）

### Quick Reference（快速参考）

> 「FocusCapture 是一个 Windows 桌面灵感捕获工具。网站用纯 HTML + Tailwind，部署到 GitHub Pages。视觉是深色聚焦风：背景 #0A0E14、强调绿 #4ADE80、CTA 蓝 #3B82F6。Inter + 思源黑体。不用渐变、不用毛玻璃（除导航外）。所有文字必须在 HTML 源码里直接可读。」

### Component Prompts（组件生成 Prompt 示例）

**Prompt 1 — Hero 区块**
```
请基于 DESIGN.md 生成 FocusCapture 网站的 Hero 区块。要求：
- 深色背景 #0A0E14
- H1：「FocusCapture · 不打断你思路的灵感捕获器」
- 副标题：一句话价值主张（≤ 80 字）
- 两个按钮：「立即下载」（Primary 蓝）+ 「GitHub 仓库」（Ghost）
- 右侧：产品截图（沉浸记录 + 速览的拼接图）
- 所有文字必须在 HTML 源码里直接可见
- 响应式：手机端单列、桌面端双列
```

**Prompt 2 — 4 大特性卡片**
```
请基于 DESIGN.md 生成「核心卖点」板块，4 张卡片：
1. 🖱️ 悬浮球：常驻最前，一键直达
2. 📋 剪贴板捕获：复制即存，400ms 防抖
3. 🗣️ 沉浸语音：本地 ASR，离线可用
4. 🔒 数据本地：100% 本机，零云端

要求：
- 卡片背景 #11161F，边框 #1F2937，hover 时边框变 #374151
- 每张卡片：图标 + 标题 + 一句话描述
- 桌面端 4 列、平板 2 列、手机 1 列
- 字体：标题 20px / 500，副文 14px / 400
```

**Prompt 3 — 热键速查表**
```
请用 HTML <table> 生成 FocusCapture 默认热键速查表。
数据：
| 热键 | 功能 |
|---|---|
| Alt+Space | 唤起灵感输入 |
| Ctrl+Alt+F1 | 剪贴板捕获开关 |
| Ctrl+Alt+V | 打开灵感速览 |
| Ctrl+Alt+R | 启动沉浸语音 |
| Ctrl+S | 语音输入保存 |

要求：
- 用语义化 <table>，Agent 可直接解析
- 表头背景 #1A2030，行 hover 背景 #11161F
- 等宽字体（JetBrains Mono）显示热键
```

**Prompt 4 — JSON-LD 结构化数据**
```
请为 FocusCapture 网站生成 JSON-LD（schema.org/SoftwareApplication），
插入到 <head>。必须字段：
- name: FocusCapture
- applicationCategory: UtilitiesApplication
- operatingSystem: Windows
- softwareVersion: 0.2.0
- description: 不打断思路的灵感捕获器...
- downloadUrl: https://github.com/pengjie1115/FocusCapture/releases
- author: { @type: Person, name: pengjie1115 }
- license: MIT
```

**Prompt 5 — llms.txt 文件**
```
请生成 /llms.txt 文件，遵循 https://llmstxt.org 规范。
结构：
# FocusCapture
> 一句话定位
## 核心信息
- 平台/版本/下载链接
## 主要功能
- 4 条，每条一行
## 技术栈
- 4 条
## 隐私
- 数据全本地说明
```

### Iteration Guide（迭代建议）

1. **改色先改 token**：所有色值都通过 CSS 变量引用，改一处全局生效
2. **加新区块先看层级**：检查是否破坏 H1 → H2 → H3 节奏
3. **新增组件先查 token**：圆角/间距/阴影都在 token 表里，禁止临时定值
4. **响应式先做手机**：mobile-first 思维，确保小屏不破
5. **每次改完跑「Agent 抓取验证」**：右键查看源代码，关键文字必须可见
6. **保持视觉一致**：每次新增截图都用同一暗色背景，避免色温不一致
7. **不要追求花哨**：克制本身就是气质，加任何装饰前先问「有没有必要」
8. **保持更新**：CHANGELOG、Roadmap 板块随产品发布同步更新

---

**版本**：v1.0 · 2026-08-30
**适用**：FocusCapture 官方网站（FocusCapture 仓库 / website 或 /docs 子目录）
**维护者**：pengjie1115