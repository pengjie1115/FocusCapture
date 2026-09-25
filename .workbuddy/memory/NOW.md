# NOW — 当前状态（唯一活文件）

> **谁干活谁覆盖更新；并行开发按分支分节；旧状态被覆盖即自动作废。** 硬上限 40 行。
> 维护规则见根目录 `AGENTS.md`「三、开发记忆」。更新：2026-09-25

## 当前：main（已合并推送，双远程同步，无在途分支）

- main = `d0c9182`；本地 / Gitee(origin) / GitHub 三处一致（`ls-remote` 核实）
- 本次并入 `feature/chat-search-overlay`（ff-only，2 提交）：ChatSearchWindow 独立窗体 → `ChatSearchPanel` 居中覆盖层（主内容毛玻璃 + 点外/Esc/× 关闭）+ 词级高亮
- 按用户指令已删两个分支：`feature/chat-search-overlay`、`fix/ai-chat-ui-bugs`（a0672b7，早已并入 main）
- 现存唯一其他分支：`feature/chat-restore-cross-device`（`251db19`，未合并，保留）
- 无待推送提交；工作区干净

## 人工验收欠账（已在 main，需补真机手验）

- B-16（Agent 工具）/ B-12：需真实环境手跑（B-15、B-6 已结案勿再提）
- fix/ai-chat-ui-bugs 条目（B-9/B-10/B-21/B-22）：输入区沉底 / 发送停止两态 / 欢迎语 / 头像圆形 / 相对时间 / 标题栏两态 / 默认模型框 / 进分组不收起 / 分组三点与移动到分组 / 两处批量管理全流程
- chat-search-overlay 条目：搜索覆盖层（毛玻璃 / 居中 / 点外 / Esc 关闭）、词级高亮观感
- COM 互操作 / 全局热键 / 剪贴板改动须真机手验（自动化证明不了）

## 遗留

- 无。沙箱内推送仍走 bash（`dev.ps1 push` 在 PowerShell 侧出不了网，见 `PITFALLS.md`）
