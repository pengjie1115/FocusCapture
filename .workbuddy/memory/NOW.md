# NOW — 当前状态（唯一活文件）

> **谁干活谁覆盖更新；并行开发按分支分节；旧状态被覆盖即自动作废。** 硬上限 40 行。
> 维护规则见根目录 `AGENTS.md`「三、开发记忆」。更新：2026-09-27

## 当前：分支 `feature/ai-tidy-rules`（基于 main `c7c68fc`）**已提交待合并**；main 仍停在 c7c68fc，Gitee 依旧待推（token 过期）

- **AI 整理规则化 + 预览窗非模态**（[ZCode] 本分支，1 个提交）：
  - 提示词拆「输出纪律 + 规则指令段」：预置 4 条（理顺条理〔默认〕/ 总结摘要 / 提取待办 / 解释扩展）
    + 自定义规则（**整段替换，不拼纪律** —— 用户拍板）；旧⑦「只用 - 与 **」取消，改正常序号
  - 预览窗底部**规则条**（不设上限、超宽横向滚动；「自定义」跳设置 → 整理规则板块，非模态两边同开）
  - **提取待办**：结果区切待办行列表（行内日期 / 时间框），出口加「**创建待办**」落 NoteType.Todo；
    解析 0 条自动退回文本形态（`NoteTidyPrompt.ParseTodoLines`）
  - **预览窗 ShowDialog → Show 非模态**（用户拍板：预览窗开着时灵感速览可操作）；落库搬进 Closed 回调
  - 设置新增「**整理规则**」板块（列表排序 / 显隐 / 默认规则 / 自定义增删改 / 自定义入口开关）；
    **默认规则与显隐互不干涉**（隐藏的规则照样当默认跑）
  - 检查点：快层 114→**125**（[17] 规则拼装 + 待办行解析 +11）/ 慢层 709→**717**（规则驱动 + 非模态契约 +8）；
    snap 45→**46 张**（30 改造 + 新增 30b 提取待办形态 —— **出图逮住 ItemTemplate 忘挂的真 bug**）；ready 退出码 0
- 数据模型：`Models/TidyRule.cs` + `Services/AI/TidyRuleCatalog.cs`；AppSettings 新增
  `TidyRules / TidyDefaultRuleId / TidyShowCustomEntry`（已注册 AppJsonContext，漏注册=运行时炸）

## 分支

- `feature/ai-tidy-rules` —— **待用户确认后合并**（本会话只提交，不合并不推送）
- `feature/chat-restore-cross-device` —— 未并入，保留

## 遗留

- ⚠ **Gitee 推送受阻：`Oauth: Access token is expired`（403）** —— 修法见 PITFALLS；token 不进聊天
- 快照 30b 的 DatePicker 是系统控件浅色样式，深色底下偏亮（可接受，B-23 有记录；要统一深色需自绘日历）
- 慢层实测 ~35 秒 vs 锚点「约 170 秒」差异未查证
- 已实测到的 2 个 flaky（`[8]` 子进程流式读、Agent 工具组的时间前提）仍未隔离
