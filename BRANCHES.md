# BRANCHES.md - FocusCapture 分支索引与管理规范

> 创建：2026-09-01 ｜ 最近整理：2026-09-09（修正 7 处失实登记、删除 6 个已合并本地分支、归档表压缩）
> 用途：全项目分支的唯一登记表。每次建分支、合并、删除后必须同步更新本表。
> 定位：「施工日志」——分支可以删，本表不能丢。

---

## 一、分支命名规范（铁律）

| 前缀 | 用途 | 示例 |
|------|------|------|
| `feature/xxx` | 新功能开发 | feature/import-range-search |
| `fix/xxx` | bug 修复 | fix/recycle-baml-error |
| `docs/xxx` | 文档、落地页 | docs/website-v2 |
| `release/vX.Y.Z` | 发版分支 | release/v0.3.0 |
| `experiment/xxx` | 实验探索，做坏即弃 | experiment/xxx |

**禁止**：
- `codex/` 前缀（2026-09-01 已全部清理，属历史遗留，不再使用）
- 分支名带 phase 编号（阶段拆在提交信息里，不拆分支）
- 无类型前缀的裸分支名

## 二、分支生命周期铁律

1. **一个功能 = 一个分支**：从 main 拉 `feature/xxx`，开发完合并回 main
2. **合并即删**：功能合并进 main 后，立即删除本地 + 远程分支（不留档）
3. **main 只做合并接收方**：绝不在 main 上直接改代码
4. **开工前查重**：`git log --all --grep=功能关键词` 先查 main 是否已有类似功能，防重复开发
5. **阶段拆在提交信息**：多阶段功能只用一个分支，commit message 标注阶段（如 `feat(todo): phase2 面板徽标`）

## 三、当前分支索引（2026-09-09 清理后）

| 分支 | 功能板块 | 状态 | 最后提交 |
|------|---------|------|---------|
| main | 主线（含 2026-09-09 会话同步全链路修复） | 活跃 | 0240c3b |

> 当前无未合并的开发分支。**登记规则：只登记「未合并的活跃分支」；合并即删，删完挪进第四节归档，别让已合入的分支躺在第三节。**

## 四、已删除分支归档

> 如需找回：`git branch <name> <hash>`（reflog 90 天内兜底，超期则 hash 也救不回）。
> **4.1 是本表的核心价值**——放弃/未完全并入的分支，功能 main 里没有，丢了就真丢了；4.2 只是索引，功能都在 main 里活着。

### 4.1 放弃 / 未完全并入（有独有提交，只此一份）

| 原分支 | 功能板块 | 备注 | 最后提交 |
|--------|---------|------|---------|
| feature/reminder-sound | 提醒音效 MVP（4 款内置 + wav/mp3 自定义 + 音量/试听） | 确认放弃（2026-09-05） | d620f79 |
| feature/annotation-quote | 批注功能（选中文字一键批注，UIA 三层取文） | **永久搁置**，未来扫描自动跳过 | f33d0d0 |
| feature/build-trim-iconfix | 构建瘦身（锁 RID win-x64）+ 图标嵌入修复 | 末位提交未并入 main（2026-09-09 实测） | 27b1c02 |
| codex/quest-v3-sync | 云同步 v3 | 主体已合入，独有 docs 提交 | 00306cb |
| feature/license-gate | v0.2.0 发版：LicenseGate + 设置面板大改版 + 输入框 v3.6 | 功能已 cherry-pick 进 main，独有 docs 提交 | 2f752f0 |
| feature/website-landing | 官网落地页 v1（被 v2 替代） | 有独有提交未并入（2026-09-09 实测） | 349f767 |
| feature/inspiration-sync-buttons | 灵感速览云同步入口 / 回收站多选（早期并行版） | 有独有提交未并入（2026-09-09 实测） | e2df13b |

### 4.2 已完全并入 main（一句话 + hash，细节看 git log / CHANGELOG / REGRESSION.md 对应板块）

| 原分支 | 一句话 | 最后提交 |
|--------|-------|---------|
| feature/sync-cursor-fix | 笔记增量游标改 UploadedAt 修复他端漏拉 + 盐自愈/引号/对账/限流等 09-09 七连修（B-3） | 0240c3b |
| feature/ai-chat-sync | AI 会话云同步两阶段：ChatSyncEngine + 历史会话管理 UI（B-10） | 55763be |
| feature/ai-chat-history-streaming | 历史会话抽屉 + 真流式 + 停止 + 新会话（B-9） | 34be7a1 |
| feature/getnote-button-upload | 得到大脑按钮直传：包装台① + 指纹去重 + 推送编排（B-8） | 78ba9cc |
| feature/agent-tool-framework | Agent 工具框架 v2.0 + 7 本地工具 + 得到大脑适配器（B-7） | 2ca5754 |
| feature/website-refresh | 网站隐私表述对齐 + 上手板块重写 + Gitee 下载渠道 | e1461fb |
| fix/ai-response-limits | max_tokens / 工具轮数 / 截断阈值可配置 | a10cba8（merge） |
| feature/sync-auto-mkdir | 云同步自动建目录 + 授权码 4 处文案修复 | ea5be50 |
| feature/sync-hint-text-fix | 授权码提示文案改客户端申请路径 | c204f3b |
| feature/date-numeric-recognition | 待办日期识别新增数字式 10/8 等 | c8a7e8d |
| feature/ai-provider-presets | AI 供应商预设下拉 / 密钥遮罩 / 申请页跳转 | 384883d |
| fix/todo-reminder-optimizations | 待办提醒优化八项（严格弹窗/稍后/每日汇总等） | 784a855 |
| codex/new-feature | 灵感速览最近 3 天切换 | bb90fb4 |
| codex/quest1-ai-provider | AI provider 接入 / 日历弹窗修复 | 7bff665 |
| feature/asr-pure-csharp | ASR 纯 C# 迁移（后续语音功能被移除） | 371b491 |
| feature/restore-0831 | 8-31 版本恢复 | 78588e2 |
| feature/todo-reminder | 待办提醒 phase1-2 | 48cf98d |
| feature/todo-reminder-phase3 | 待办提醒 phase3 | 1c36957 |
| feature/todo-reminder-phase4 | 待办提醒 phase4 | 16bbc27 |
| feature/website-v2 | 官网落地页 v2（绿调，实际上线版） | e2e9e88 |
| feature/import-range-search | 区间筛选迷你日历 Popup | 90a135c |

## 五、远程分支状态（2026-09-09 更新）

| 远程 | 分支 | 备注 |
|------|------|------|
| Gitee（origin） | main | 旧分支已删（2026-09-01） |
| GitHub | main | 2026-09-02 删 4 个残留；**feature/license-gate 漏删仍在（2026-09-09 实测确认），当晚补删因 SSL 连续断连未成，待网络窗口补删** |
