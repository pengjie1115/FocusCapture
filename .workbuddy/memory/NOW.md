# NOW — 当前状态（唯一活文件）

> **谁干活谁覆盖更新；并行开发按分支分节；旧状态被覆盖即自动作废。** 硬上限 40 行。
> 维护规则见根目录 `AGENTS.md`「三、开发记忆」。更新：2026-09-27（官网 v0.3.0 已上线并推送 Gitee）

## 当前：官网 v0.3.0 已上线 + README 校对 + embed_icon 清账（均已提交）

- 官网 https://focuscapture.app.workbuddy.host/ 实测已更新：首页含「AI 整理」、FAQ 含联系邮箱、
  下载页 `2026-09-27 / 约 249 MB`；线上 exe `Content-Length: 261460291`（≈249 MB）与本地
  `bin/Release/net8.0-windows/win-x64/publish/` 产物**逐字节一致**（`e0bba50` / tag `v0.3.0`）
- 后续三提交：`aa7130e` README 校对（新能力表 / 补 Ctrl+Alt+S 与 Ctrl+T / 条数 148+740 /
  官网入口替代 Releases / 反馈改邮箱）、`d91e248` 清掉 `embed_icon.py` + csproj `EmbedAppIcon`
  target（清理后 `dev.ps1 build` 0 警告 0 错误，MSB3073 假警告消失）、`90e7d42` 记忆
- 联系方式 = **邮箱 3097199704@qq.com**，全站 10 处（5 页脚 + 5 页 md 文末）；**用户 09-27 确认不补微信号**
- ⚠ 百度网盘与云端核对已按要求上站，但该功能**人工验收项仍未做**（REGRESSION.md B-14 末 7 条，需真机 + 真网盘）

## 待办

- **GitHub 推送待补**：连续两次 `CONNECT tunnel failed, response 502`（纯网络层，非凭据），
  落后 4 提交（`e0bba50`..`90e7d42`），网络恢复后 `git push github main v0.3.0`。**Gitee 已到位**
  （`90e7d42` = 本地 HEAD，tag `v0.3.0` 已推）
- 慢层实测 ~35 秒 vs 锚点「约 170 秒」差异未查证；2 个 flaky（`[8]` 子进程流式读、Agent 工具时间前提）未隔离

## 分支

- main（含官网 v0.3.0、云端核对、语义检索评估）；`feature/chat-restore-cross-device`、
  `experiment/baidu-listall-verify`、`experiment/netdisk-semantic-eval` 保留未删
