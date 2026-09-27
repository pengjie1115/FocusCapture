# NOW — 当前状态（唯一活文件）

> **谁干活谁覆盖更新；并行开发按分支分节；旧状态被覆盖即自动作废。** 硬上限 40 行。
> 维护规则见根目录 `AGENTS.md`「三、开发记忆」。更新：2026-09-27（chat-ui-polish 三 phase 完）

## 当前：`feature/chat-ui-polish` 三 phase 已提交**待用户验收后合并**（未合并，用户拍板暂不合）

- **phase1 打字闪动 + 占位重叠两修**（`2fe7127`）：闪动根因 = 每键 TextChanged →
  ApplyComposerLayout 末尾无条件播 140ms 透明度淡入 → 改幂等（布局没变直接 return）；
  占位与光标重叠 5 处统一校准「光标起点+6px」（AI 主框/分组搜索/会话搜索/设置搜索/灵感速记）
- **phase2 界面明暗两极可调**（`de1cdb6`）：设置·显示板块滑块 ChatShade（0=浅白/1=深=现状默认），
  `Services/ChatThemeService` 37 角色色插值 → 应用级资源 DynamicResource 全窗实时换；
  AI 窗+侧边栏中性色全资源化（深端=key 后六位原值，shade=1 逐像素不漂移，检查点守护）；
  DarkTitleBar 加 dark 开关；已知妥协：全局滚动条/ContextMenu/ToolTip 浅色端仍深色模板
- **phase3 快捷按钮**（`docs 提交前最后一条`）：[[OPTIONS:a|b]] 协议（QuickOptions.cs）→
  气泡下胶囊点击即发送；剥除只在显示层（DisplayContent，历史保留原文）；错误气泡「重试」命令型；
  起手页胶囊（ChatQuickPrompts 预置 4 条，设置可改 ≤8 条，**只存本机不进同步——用户拍板**）；
  欢迎面板底部偏移 180→230（快照实测胶囊被输入区压住）
- 检查点：快层 136 / 慢层 **739**（4b 7 条 + 4c 11 条，REGRESSION.md 已同步）；**ready 全绿**

## 分支

- `feature/chat-ui-polish`（自 main 拉出，领先 4 提交）；`feature/chat-restore-cross-device` 未并入保留

## 遗留

- ⚠ Gitee 推送受阻（token 过期 403，修法见 PITFALLS）；本地 main 领先 origin 12+ 提交待推
- 浅色端妥协三处待用户验收定夺：全局滚动条/右键菜单/ToolTip 仍深色
- 慢层实测 ~35 秒 vs 锚点「约 170 秒」差异未查证；2 个 flaky（`[8]` 子进程流式读、Agent 工具时间前提）未隔离
