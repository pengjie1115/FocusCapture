# NOW — 当前状态（唯一活文件）

> **谁干活谁覆盖更新；并行开发按分支分节；旧状态被覆盖即自动作废。** 硬上限 40 行。
> 维护规则见根目录 `AGENTS.md`「三、开发记忆」。更新：2026-09-26

## 当前：无进行中分支

- `fix/ai-chat-ui-bugs` **已 ff-only 合并进 main**（main = `a0672b7`）；`dev.ps1 ready` 全过（快175 / 慢583 / 文档20）
- 已删三个已合并残留分支：`docs/dev-memory-system`、`feature/ai-chat-ui-revamp`、`feature/ai-model-providers`
- **推送待手动**：`dev.ps1 push` 在 DSH 沙箱内失败 —— GCM 读不到 Windows 凭据库（凭据已缓存、非缺失）+ askass `sh.exe` signal pipe 被沙箱限；三路绕过（静默环境变量 / PortableGit git.exe）均败。**请真实终端跑 `tools\dev.bat push`**。本地 main 领先 origin/main 6 提交（5 功能 + 本记忆更新）
- `feature/chat-restore-cross-device` 仍未合并（未动，保留）

## 人工验收欠账（已在 main，需补真机手验）

- B-16（Agent 工具）/ B-12：需真实环境手跑（B-15、B-6 已结案勿再提）
- fix/ai-chat-ui-bugs 新增条目（B-9/B-10/B-21/B-22）：输入区沉底 / 发送停止两态 / 欢迎语 / 头像圆形 / 相对时间 / 标题栏两态 / 默认模型框 / 进分组不收起 / 分组三点与移动到分组 / 两处批量管理全流程
- COM 互操作 / 全局热键 / 剪贴板改动须真机手验（自动化证明不了）

## 上一个分支（已合并，勿再当进行中）

- `feature/ai-chat-ui-revamp` 已合并进 main（`687cf05`）并推 Gitee；GitHub 当时 SSL 失败按指令未重试
- AI 模型多供应商改造已于 9-23 并入 main
