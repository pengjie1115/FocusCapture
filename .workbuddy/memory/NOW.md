# NOW — 当前状态（唯一活文件）

> **谁干活谁覆盖更新；并行开发按分支分节；旧状态被覆盖即自动作废。** 硬上限 40 行。
> 维护规则见根目录 `AGENTS.md`「三、开发记忆」。更新：2026-09-25

## 当前：main（无在途分支，双远程同步）

- 最新提交见 `git log`；本地 / Gitee(origin) / GitHub 三处一致（`ls-remote` 核实过同 SHA）
- 本次并入 `feature/chat-search-overlay`（ff-only，2 提交）：ChatSearchWindow 独立窗体 → `ChatSearchPanel` 居中覆盖层（主内容毛玻璃 + 点外/Esc/× 关闭）+ 词级高亮
- 按用户指令已删两个分支：`feature/chat-search-overlay`、`fix/ai-chat-ui-bugs`（a0672b7，早已并入 main）
- 现存唯一其他分支：`feature/chat-restore-cross-device`（`251db19`，未合并，保留）
- 工作区干净

## 人工验收（已全清，2026-09-25）

- 用户 2026-09-25 确认「旧账都已验收通过」：B-9 / B-10 / B-12 / B-16 / B-21 / B-22 全部 ⚠ 条目 + B-6 热键三条 + chat-search 覆盖层与词级高亮，全绿
- `REGRESSION.md` 对应段已加 ✅ 结案注记（7 处）；表格内 ⚠ 字形保留，注记中已写明「自此视同已人工验收」
- 唯一保留 ⚠：B-6「任务栏/开始菜单/桌面/exe 图标不跟随」= 平台能力边界（读 exe 内嵌资源），永久无验收必要

## 遗留

- 无。沙箱内推送仍走 bash（`dev.ps1 push` 在 PowerShell 侧出不了网，见 `PITFALLS.md`）
