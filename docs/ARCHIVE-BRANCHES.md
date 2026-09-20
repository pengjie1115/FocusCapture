# ARCHIVE-BRANCHES.md — 已放弃 / 未完全并入的分支

> **本文件只存「git 已查不到」的东西。**
> 已完全并入 main 的分支**不登记** —— `git log` 就是归档。
> 想知道现在有哪些分支、有没有未合并的：**当场 `git` 查**（`git branch -a` / `git ls-remote --heads <remote>`）。
> 分支命名与生命周期规范**不在这里**，见项目根 `AGENTS.md` 红线 1–2。

---

## 一、为什么这些分支要永久登记

这些分支的末位提交**没有**完全进 main，功能在 main 里没有完整版 —— **丢了就真丢了**。

**找回**：`git branch <name> <hash>`
⚠ 只靠 `git reflog` 兜底**能撑多久不确定**（受 `gc.reflogExpire` / `gc.pruneExpire` 影响，可能短到 2 周）。
想**永久**可恢复，唯一可靠办法是 `git tag <tag> <hash>`（tag 让对象永不被 gc 回收）。
末位提交是否已并入 main，以 `git merge-base --is-ancestor <hash> main` 实测为准（2026-09-09 全部实测过）。

| 原分支 | 功能板块 | 备注 | 最后提交 |
|--------|---------|------|---------|
| feature/reminder-sound | 提醒音效 MVP（4 款内置 + wav/mp3 自定义 + 音量/试听） | 确认放弃（2026-09-05） | d620f79 |
| feature/annotation-quote | 批注功能（选中文字一键批注，UIA 三层取文） | **永久搁置**，未来扫描自动跳过 | f33d0d0 |
| feature/build-trim-iconfix | 构建瘦身（锁 RID win-x64）+ 图标嵌入修复 | 末位提交未并入 main | 27b1c02 |
| codex/quest-v3-sync | 云同步 v3 | 主体已合入，独有 docs 提交 | 00306cb |
| feature/license-gate | v0.2.0 发版：LicenseGate + 设置面板大改版 + 输入框 v3.6 | 功能已 cherry-pick 进 main，独有 docs 提交 | 2f752f0 |
| feature/website-landing | 官网落地页 v1（被 v2 替代） | 有独有提交未并入 | 349f767 |
| feature/inspiration-sync-buttons | 灵感速览云同步入口 / 回收站多选（早期并行版） | 有独有提交未并入 | e2df13b |
| feature/sync-line-identity | 行身份改造（行 ID 去路径 / 原地改行补墓碑 / 未到期待办全库扫行） | **代码已并入 main（2026-09-12）**；独有内容是 `MIGRATION.md`（不进远端） | 9ada255 |
| feature/quark-cloud-drive | 夸克网盘适配器（借道官方 CLI）：`Services/Destinations/Quark/` 四文件 + 设置页安装授权 + AI 工具化，13 文件 / +1931 −2 | **2026-09-17 按用户指令彻底删除本地分支**：从未推送 → 无远程副本、无 tag，**已无任何 ref 指向**。恢复只能靠 reflog，过期即永久丢失 | aa96192 |

## 二、夸克网盘分支：丢了什么（留档）

5 个提交（`2a5ff50` 接入夸克网盘 → `cc1793f` 支持上传对话附件 → `e5f854f` 登记 → `8cdc1a7` 补记「可行性结论：本方案不成立」→ `aa96192` 标注搁置）。

内容含 **`docs/夸克网盘接入方案.md`（285 行，随分支删除已不在仓库）** —— 那份文档的结论「本方案不成立」本身有价值（负面结论也是结论）。
**结论层已另有留存**（项目 MEMORY.md「夸克适配器三特性」+ skill `platform-integration-recon`），
但**文档正文随分支一起没了**。要救回：`git branch tmp-quark aa96192`（趁 reflog 还在）。

**搁置原因**：官方 CLI 校验宿主白名单，第三方无门。

## 三、远程现状

> **不硬编码哈希 —— 只记结构**（硬编码必然漂移）。具体以 `git ls-remote --heads <remote>` 实测为准。

| 远程 | 分支 | tag | 备注 |
|------|------|-----|------|
| Gitee（origin） | 仅 main | v0.1.0、v0.2.0 | 旧分支已删（2026-09-01）；除 main 外无分支 |
| GitHub | 仅 main | v0.1.0、v0.2.0 | 2026-09-02 删 4 个残留；`feature/license-gate` 已于 2026-09-12 补删 |

**GitHub 推不上去时怎么办**（2026-09-17 对照实验定性）：

| 路径 | 结果 |
|---|---|
| 经沙箱代理访问 `github.com` | **502**（10 秒） |
| 经同一代理访问 `gitee.com` | **200，0.41 秒** |
| 绕过代理直连 `github.com:443` | 连不上，21 秒超时（沙箱无直连出口） |

→ **在 WorkBuddy 里推 GitHub 失败，先按「沙箱出口问题」看，别改 git 参数**（`http.version` / `http.proxy` 都验证无效）。
**直接隔一会儿重试**即可 —— 当日两度验证有效；落后的提交**一次能补完，别怕攒着**。
