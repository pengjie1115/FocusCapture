# NOW — 当前状态（唯一活文件）

> **谁干活谁覆盖更新；并行开发按分支分节；旧状态被覆盖即自动作废。** 硬上限 40 行。
> 维护规则见根目录 `AGENTS.md`「三、开发记忆」。更新：2026-09-27（官网 v0.3.0 已上线）

## 当前：官网已按最新 main 全面同步并重新发布（`e0bba50` / tag `v0.3.0`）

- 线上 https://focuscapture.app.workbuddy.host/ 实测已更新：首页含「AI 整理」、FAQ 含联系邮箱、
  下载页 `2026-09-27 / 约 249 MB`；线上 exe `Content-Length: 261460291`（≈249 MB）与本地
  `bin/Release/net8.0-windows/win-x64/publish/` 产物**逐字节一致**
- 网站 13 文件改动：首页 / 功能 / FAQ（10 → 13 条）/ 下载 / 更新日志 / llms.txt / website/AGENTS.md
  （AGENTS.md 新增「联系方式是全站唯一散布式内容，改必 10 处同改」+ 部署前必查项重写）
- 新能力已上站：AI 整理、AI 问答界面（分组·全局搜索·快捷胶囊·明暗可调）、AI 模型多供应商、
  Agent 工具、Skill 扩展、百度网盘与「与云端核对」；另补两个漏登的默认热键（Ctrl+Alt+S、Ctrl+T）
- 联系方式 = **邮箱 3097199704@qq.com**，全站 10 处（5 页脚 + 5 页 md 文末）；llms.txt 未加（用户未勾）
- ⚠ 用户原话要「微信号」，但选项题实际答的是邮箱 → **微信号待补**；补了就改那 10 处
- ⚠ 百度网盘与云端核对已按要求上站，但该功能**人工验收项仍未做**（REGRESSION.md B-14 末 7 条，需真机 + 真网盘）

## 待办

- **推送待用户确认**：本地 main 领先 origin（Gitee token 过期 403）；GitHub 待推；tag `v0.3.0` 未推送
- 官网小账：README.md 第 109 行仍写「docs/ 官网落地页（GitHub Pages）」，与现状
  （`website/` + WorkBuddy 自有托管 `focuscapture.app.workbuddy.host`）不符
- `Resources/embed_icon.py` + csproj `EmbedAppIcon` target 是否清理（历史遗留、对 RID 发布无效）—— 待用户拍板
- 慢层实测 ~35 秒 vs 锚点「约 170 秒」差异未查证；2 个 flaky（`[8]` 子进程流式读、Agent 工具时间前提）未隔离

## 分支

- main（含官网 v0.3.0、云端核对、语义检索评估）；`feature/chat-restore-cross-device`、
  `experiment/baidu-listall-verify`、`experiment/netdisk-semantic-eval` 保留未删
