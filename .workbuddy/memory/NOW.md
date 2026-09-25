# NOW — 当前状态（唯一活文件）

> **谁干活谁覆盖更新；并行开发按分支分节；旧状态被覆盖即自动作废。** 硬上限 40 行。
> 维护规则见根目录 `AGENTS.md`「三、开发记忆」。更新：2026-09-25

## 当前：feature/chat-search-overlay（已提交，待用户确认后合并）

- 全局搜索改造完成（ZCode/彭杰会话）：独立窗体 ChatSearchWindow 已删，改为 AIDialogWindow 内居中覆盖层 ChatSearchPanel —— 主内容挂 BlurEffect 毛玻璃、点面板外/Esc/× 关闭；面板三段（历史对话标题+× / 搜索框 / 两态列表：未输入=最近会话前100条，输入=全文搜索结果）；跳转高亮词级化（BubbleText.Select+SelectionBrush，2.5s 清除；TextBox 单选区仅标首处命中）
- `dev.ps1 ready` 全过（快175 / 慢层含新增12条 / 文档20）；提交 1 个，**未合并未推送**（按用户指令）
- 真机待验：覆盖层视觉效果（毛玻璃/居中）、词级高亮观感、Esc/点外关闭
- 注意：main 本地仍领先 origin/main 6 个提交待推送（见下）

## 人工验收欠账（已在 main，需补真机手验）

- B-16（Agent 工具）/ B-12：需真实环境手跑（B-15、B-6 已结案勿再提）
- fix/ai-chat-ui-bugs 新增条目（B-9/B-10/B-21/B-22）：输入区沉底 / 发送停止两态 / 欢迎语 / 头像圆形 / 相对时间 / 标题栏两态 / 默认模型框 / 进分组不收起 / 分组三点与移动到分组 / 两处批量管理全流程
- COM 互操作 / 全局热键 / 剪贴板改动须真机手验（自动化证明不了）

## 遗留

- **推送待手动**：`dev.ps1 push` 在 Agent 沙箱内失败（GCM 读不到凭据）——请在真实终端跑 `tools\dev.bat push`
- `feature/chat-restore-cross-device` 未合并（未动，保留）
