# NOW — 当前状态（唯一活文件）

> **谁干活谁覆盖更新；并行开发按分支分节；旧状态被覆盖即自动作废。** 硬上限 40 行。
> 维护规则见根目录 `AGENTS.md`「三、开发记忆」。更新：2026-09-25

## 当前：feature/chat-search-refinements（已提交，**未合并未推送**）

- 本轮（2026-09-25 下午，[ZCode]）在分支上完成 AI 问答搜索四项：
  ① 3号搜索分流：收起态 🔍 对话打开=向右展开**会话内查找条**（只搜本会话，n/m+‹ ›+黄高亮），起手页/分组视图=全局覆盖层；1号恒开覆盖层（独立入口）
  ② 收起态标题栏加 ＋新建会话（对话中也能立即新开，旧回答转后台）
  ③ 搜索面板改**搜索词历史胶囊**（逐条×+一键清空，存 `chat_search_history.json`，上限 20；最近会话列表退场）
  ④ 修「分组视图里点侧边栏/搜索面板会话切不过去」：`LoadHistorySession` 补 `CloseGroupView()`
- 涉及：AIDialogWindow.xaml(.cs)、ChatSearchPanel.xaml(.cs)、新 `Services/ChatSearchHistoryStore.cs`、`Services/AI/InSessionFindMatcher.cs`、tests/sync、REGRESSION.md B-22
- `dev.ps1 ready` 全过（编译+快 175+慢 584+文档引用）；REGRESSION 新增 6 行 ⚠ **待用户人工验收**
- 下一步：用户人工验收 → 确认后合并（须用户点头）

## main 状态（分支开出时）

- 与双远程一致（`3116b94`）；现存唯一其他分支 `feature/chat-restore-cross-device`（未合并，保留）
- 人工验收旧账 2026-09-25 已全清（见 REGRESSION 结案注记）

## 遗留

- 沙箱内推送仍走 bash（`dev.ps1 push` 在 PowerShell 侧出不了网，见 `PITFALLS.md`）
