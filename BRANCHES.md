# BRANCHES.md - FocusCapture 分支索引与管理规范

> 创建：2026-09-01 ｜ 2026-09-09 瘦身定稿：只登记活跃分支 + 放弃/未并入归档；已完全并入的分支不再登记（`git log` 即归档）
> 用途：全项目分支的唯一登记表。每次建分支、合并、删除后必须同步更新本表。
> 定位：「施工日志」——但只记 git 现查不到的事。

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
2. **合并即删**：功能合并进 main 后，立即删除本地 + 远程分支，**不登记归档**（git log 就是归档）
3. **main 只做合并接收方**：绝不在 main 上直接改代码
4. **开工前查重**：`git log --all --grep=功能关键词` 先查 main 是否已有类似功能，防重复开发
5. **阶段拆在提交信息**：多阶段功能只用一个分支，commit message 标注阶段（如 `feat(todo): phase2 面板徽标`）

## 三、当前分支索引

| 分支 | 功能板块 | 状态 | 最后提交 |
|------|---------|------|---------|
| main | 主线（含 2026-09-11 自动化检查点体系、2026-09-12 行身份改造 + 全局热键、2026-09-12 灵感速览时间筛选重构） | 活跃 | fceaae3 |
| feature/quickview-customizable-toolbar | 灵感速览可组装标题栏 + 窗口壳（铬区最小化/最大化、宽度/置顶设置、「灵感速览」设置板块，REGRESSION B-12） | 开发中（已提交待用户测试，未合并） | 见 git log |

> 当前无未合并的开发分支。**只登记「未合并的活跃分支」，合并即删即除名。**

## 四、放弃 / 未完全并入归档（本表核心价值）

> 这些分支的末位提交**没有**完全进 main，功能 main 里没有完整版，丢了就真丢了，故永久登记。
> 找回：`git branch <name> <hash>`（reflog 90 天内兜底）。
> 末位提交是否已并入 main 以 `git merge-base --is-ancestor <hash> main` 实测为准（2026-09-09 全部实测过，此前文档 5 处「功能已在 main」的记载是错的）。

| 原分支 | 功能板块 | 备注 | 最后提交 |
|--------|---------|------|---------|
| feature/reminder-sound | 提醒音效 MVP（4 款内置 + wav/mp3 自定义 + 音量/试听） | 确认放弃（2026-09-05） | d620f79 |
| feature/annotation-quote | 批注功能（选中文字一键批注，UIA 三层取文） | **永久搁置**，未来扫描自动跳过 | f33d0d0 |
| feature/build-trim-iconfix | 构建瘦身（锁 RID win-x64）+ 图标嵌入修复 | 末位提交未并入 main | 27b1c02 |
| codex/quest-v3-sync | 云同步 v3 | 主体已合入，独有 docs 提交 | 00306cb |
| feature/license-gate | v0.2.0 发版：LicenseGate + 设置面板大改版 + 输入框 v3.6 | 功能已 cherry-pick 进 main，独有 docs 提交 | 2f752f0 |
| feature/website-landing | 官网落地页 v1（被 v2 替代） | 有独有提交未并入 | 349f767 |
| feature/inspiration-sync-buttons | 灵感速览云同步入口 / 回收站多选（早期并行版） | 有独有提交未并入 | e2df13b |
| feature/sync-line-identity | 行身份改造：行 ID 去路径 / 原地改行补墓碑 / 未到期待办全库扫行 / 空上传时刻补齐 | **代码已并入 main（2026-09-12）**；独有内容是 `MIGRATION.md`（含本机绝对路径的一次性迁移指引，已加入 .gitignore，**不推送远端**，只留本机工作区） | 9ada255 |

## 五、远程分支状态（2026-09-12 更新）

| 远程 | 分支 | 备注 |
|------|------|------|
| Gitee（origin） | main | 旧分支已删（2026-09-01）。当前除 main 外无分支 |
| GitHub | main | 2026-09-02 删 4 个残留；**feature/license-gate 已于 2026-09-12 补删成功**（前两次 09-09、09-11 因网络失败）。删除不影响找回：hash `2f752f0` 见第四节留档 |
