# NOW — 当前状态（唯一活文件）

> **谁干活谁覆盖更新；并行开发按分支分节；旧状态被覆盖即自动作废。** 硬上限 40 行。
> 维护规则见根目录 `AGENTS.md`「三、开发记忆」。更新：2026-09-27

## 当前：main `c7c68fc` 干净 —— AI 整理 + snap 按需出图**已合并**；GitHub 已推，**Gitee 待推（token 过期）**

- 2026-09-26 夜两次交付已 ff-only 并入 main（`9c29c01` → `c7c68fc`，6 个提交 / 25 文件 / +1576 行）：
  - **AI 整理**（[dsh] `117a248`）：三入口（灵感速览行尾 / 待办汇总 / 全屏编辑窗）→ LLM 理顺 →
    预览对照窗 → 三出口；上限 8000 字符超出直接拒绝；顺带修「待办汇总编辑态点框外自动保存」
  - **snap 按需出图**（[WorkBuddy] `c984507`）：`--only` 过滤 + `--list` 清单、
    出图落 `%TEMP%\fc-ui-snapshot\<yyyyMMdd-HHmmss>\`（保留最近 5 次）、**出图前先增量编译**
  - 检查点：快层 109→**114**（新增 [18] 界面快照按需出图契约 5 条）/ 慢层 709；`ready` 退出码 0
- 分支 `feature/snap-scoped-output`（was `c7c68fc`）与 `feature/ai-tidy`（was `402febe`）**已删除**（合并即删）

## 用法 / 关键设计（别退化）

- 快照按需出图：`tools\dev.ps1 snap -List` 拿清单 → `tools\dev.ps1 snap -Only '03b,10e'`（**值必须加引号**）
- **光加过滤不够**：场景名↔板块的映射只活在 `UiSnapshot.cs` 注释里，纯子串匹配会**静默漏图**
  （改「AI 功能」板块时 `03d-设置-AI 问答界面` 名字里没这四个字）→ 必须配 `--list` 先拿清单
- **`--only` 没匹配到必须失败退出**（退出码 2）：漏图无声 = 把整个判读环节骗过去，比全量慢危险得多

## 分支

- `feature/chat-restore-cross-device` —— 未并入，保留（当前唯一剩余分支）

## 遗留

- ⚠ **Gitee 推送受阻：`Oauth: Access token is expired`（403）** —— 本地 main 与 GitHub 均已到位，
  Gitee（origin）落后 6 个提交。凭据由 git-credential-manager 管（`credential.helper` = PortableGit 自带 GCM，
  `credential.https://gitee.com.provider=generic`）。修法：清掉 `git:https://gitee.com` 旧凭据 → 用新令牌重推；
  **token 不进聊天**，用户自行在终端输入
- [WorkBuddy] PowerShell 静默错误已防呆，但**同类风险仍在**：给脚本传「编号 / 代码 / 标识」都可能被
  后缀语法或逗号分隔改写 → 通则「一律加引号」已进 `PITFALLS.md`
- 慢层实测 35.7 秒 vs 锚点「约 170 秒」差异未查证（2026-09-26 ready 实测墙钟 113.7 秒）
- 已实测到的 2 个 flaky（`[8]` 子进程流式读、Agent 工具组的时间前提）仍未隔离
