# NOW — 当前状态（唯一活文件）

> **谁干活谁覆盖更新；并行开发按分支分节；旧状态被覆盖即自动作废。** 硬上限 40 行。
> 维护规则见根目录 `AGENTS.md`「三、开发记忆」。更新：2026-09-27（phase2 后）

## 当前：`feature/ai-tidy-rules` **已合并回 main（ff，至 `603885e`）**；本地领先 origin/main 10 个提交待推（Gitee token 过期）

- **AI 整理规则化 + 非模态**（`4ee1ed9`，phase1）：预置 4 条（逻辑梳理〔默认〕/ 总结摘要 / 提取待办 / 解释扩展）
  + 自定义整段替换；预览窗规则条；提取待办行内日期时间 + 创建待办；设置「整理规则」板块
- **phase2 交互打磨**（`ea874df`，用户已验收）：
  - **先开窗后整理**（点 AI 立即弹窗「整理中」态；空/超长/未配 Key 仍开窗前拦）；
    状态行按类型上色（绿进行成功 / 红错误 / 灰中性）
  - **创建待办不关窗**：原地报「创建成功 N 条」，按钮禁用至改行（防手滑重复）；
    CreateHandler 注入落库 + 即时回调刷新列表
  - 底栏并进对照区 Grid（随中缝联动）+ 7px 细滚动条 + Shift+滚轮横滚；
    时间拆小时/分钟两框；DatePicker 收窄压浅灰（日历按钮仍系统浅色 = 已记录妥协）
  - 规则悬停显示适用场景（TidyRule.Description，设置编辑器可填）；**理顺条理→逻辑梳理**
    （Normalize 按 Id 刷新预置条目名称/说明，老 settings 不滞留旧名）
  - 预置规则结果**彻底去 Markdown**（禁 ** 与 - + NormalizePlainFormatting 转「· 」；自定义不清洗）
  - 提取待办：注入今天日期 + 年份补全（未到补当年/已过顺延一年）+ 占位符机械剥除 + TimeParser 兜底回填
  - 手动建待办：设上提醒后正文剥离时间表达（TimeParser.StripTimeExpression；剥空/未命中保留原文）
- 检查点：快层 125→**136** / 慢层 717→**721**；snap 46 张（30/30b 样例已去 Markdown）；ready 全绿

## 分支

- `feature/ai-tidy-rules` —— **已合并，按规范可删**（删除操作留给用户）；`feature/chat-restore-cross-device` 未并入，保留

## 遗留

- ⚠ **Gitee 推送受阻：`Oauth: Access token is expired`（403）** —— 修法见 PITFALLS；token 不进聊天
- DatePicker 日历按钮/弹层仍系统浅色（统一深色需自绘日历，B-23 有记录）
- 慢层实测 ~35 秒 vs 锚点「约 170 秒」差异未查证
- 已实测到的 2 个 flaky（`[8]` 子进程流式读、Agent 工具组的时间前提）仍未隔离
