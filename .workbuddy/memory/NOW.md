# NOW — 当前状态（唯一活文件）

> **谁干活谁覆盖更新；并行开发按分支分节；旧状态被覆盖即自动作废。** 硬上限 40 行。
> 维护规则见根目录 `AGENTS.md`「三、开发记忆」。更新：2026-09-27（chat-ui-polish 已合并回 main）

## 当前：`feature/chat-ui-polish` 已合并回 main（用户验收通过，ff-only），分支已按「合并即删」删除

- **phase1 打字闪动 + 占位重叠两修**（`2fe7127`）：闪动根因 = 每键 TextChanged →
  ApplyComposerLayout 末尾无条件播 140ms 透明度淡入 → 改幂等；占位与光标重叠 5 处统一「光标起点+6px」
- **phase2 界面明暗两极可调**（`de1cdb6`）：设置·显示板块滑块 ChatShade（0=浅白/1=深=现状默认），
  `Services/ChatThemeService` 37 角色色插值；深端逐像素不漂移；已知妥协：全局滚动条/ContextMenu/ToolTip 浅色端仍深色
- **phase3 快捷按钮**（`570850e`）：[[OPTIONS:a|b]] 协议 → 气泡胶囊点击即发送；错误「重试」；
  起手页胶囊（ChatQuickPrompts ≤8 条，只存本机不进同步）
- **验收期两修**（`5eca3b3` + `5132d3d`）：① BubbleText 绑只读 DisplayContent 漏 Mode=OneWay →
  每气泡抛 XamlParseException 弹窗轰炸；② 胶囊真根因 = WelcomePanel 垫在透明但可命中的
  MessagesScroll 下（Z 序），看得见点不到 —— 已挪到滚动区之后 + UpdateWelcomeContent 幂等化
  （不再每次布局重建胶囊容器）。快照 InputHitTest 命中链实测走通（方法论记 2026-09-27 事件账）
- 检查点：快层 136 / 慢层 **740**（REGRESSION.md 已同步）；合并前 ready 全绿

## 分支

- main（含上述全部）；`feature/chat-restore-cross-device` 未并入保留

## 遗留

- ⚠ Gitee 推送受阻（token 过期 403，修法见 PITFALLS）；本地 main 领先 origin 19+ 提交待推
- 慢层实测 ~35 秒 vs 锚点「约 170 秒」差异未查证；2 个 flaky（`[8]` 子进程流式读、Agent 工具时间前提）未隔离
