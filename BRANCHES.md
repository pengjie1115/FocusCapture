# BRANCHES.md - FocusCapture 分支索引与管理规范

> 创建：2026-09-01
> 用途：全项目分支的唯一登记表。每次建分支、合并、删除后必须同步更新本表。
> 定位：这是"施工日志"——分支可以删，本表不能丢。

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

## 三、当前分支索引（2026-09-01 整理后）

| 分支 | 功能板块 | 状态 | 最后提交 |
|------|---------|------|---------|
| main | 主线（当前最新版 v0.2.x） | 活跃 | 78588e2 |
| fix/todo-reminder-optimizations | 待办提醒优化：严格到点弹窗/弹窗超时默认稍后提醒/每日汇总空态关闭按钮+开关 | 进行中 | — |
| feature/build-trim-iconfix | 构建瘦身（锁 RID win-x64）+ 图标嵌入修复 | **未合入 main，待决策** | 27b1c02 |
| feature/annotation-quote | 批注功能（选中文字一键批注） | 永久搁置（2026-08 拍板），3 个独有提交未合入 | f33d0d0 |

## 四、已删除分支归档（2026-09-01 清理，功能均已确认在 main）

> 如需找回：`git branch <name> <hash>` 即可恢复（reflog 90 天内兜底）。

| 原分支 | 功能板块 | 删除方式 | 最后提交 |
|--------|---------|---------|---------|
| codex/new-feature | 灵感速览最近3天切换 | 已合入 main | bb90fb4 |
| codex/quest-v3-sync | 云同步 v3（主体已合入） | 主体合入，独有 docs 类提交 | 00306cb |
| codex/quest1-ai-provider | AI provider 接入/日历弹窗修复 | 已合入 main | 7bff665 |
| feature/asr-pure-csharp | ASR 纯 C# 迁移（早期尝试） | 已合入 main（后续语音功能被移除） | 371b491 |
| feature/license-gate | v0.2.0 发版：LicenseGate+设置面板大改版+输入框v3.6 | 功能已 cherry-pick 进 main，独有 docs 类提交（远程已删 2026-09-02） | 2f752f0 |
| feature/restore-0831 | 8-31 版本恢复分支 | 已合入 main | 78588e2 |
| feature/todo-reminder | 待办提醒 phase1-2 | 已合入 main | 48cf98d |
| feature/todo-reminder-phase3 | 待办提醒 phase3 | 已合入 main | 1c36957 |
| feature/todo-reminder-phase4 | 待办提醒 phase4 | 已合入 main | 16bbc27 |
| feature/website-v2 | 官网落地页 v2（绿调，实际上线版） | 已合入 main | e2e9e88 |
| feature/website-landing | 官网落地页 v1（被 v2 替代） | 功能已在 main | 349f767 |
| feature/inspiration-sync-buttons | 灵感速览云同步入口/回收站多选（早期并行版） | 功能已在 main（最终版 ef37b64 等） | e2df13b |
| feature/import-range-search | 区间筛选迷你日历 Popup | 已合入 main | 90a135c |

## 五、远程分支状态（2026-09-02 更新）

| 远程 | 分支 | 备注 |
|------|------|------|
| Gitee（origin） | main | 旧分支已删（2026-09-01） |
| GitHub | main | 4 个残留分支已全部删除（2026-09-02：codex/quest-v3-sync、codex/quest1-ai-provider、feature/todo-reminder-phase4、feature/license-gate），GitHub 远程仅剩 main |
