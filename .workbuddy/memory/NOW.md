# NOW — 当前状态（唯一活文件）

> **谁干活谁覆盖更新；并行开发按分支分节；旧状态被覆盖即自动作废。** 硬上限 40 行。
> 维护规则见根目录 `AGENTS.md`「三、开发记忆」。更新：2026-09-25

## 当前：feature/process-slimming（已提交，**未合并未推送**）

- 2026-09-25 [WorkBuddy] 流程减重**批 1（P5+P1）：快层物理分层**
  ① 原快层 6 个「真做事」组（`[5]`~`[10]` = 剪贴板容错 / Skill 目录扫描 / 运行时候选目录 /
     子进程流式读 / 内置技能落地恢复 / 运行时下载流程，共 80 条）**连同源文件整体搬进慢层**
     `tests/sync/Program.OutOfScope.cs`（partial class）；快层 csproj 移除对应 `<Compile Include>`
     → 「快层源码不出现这些 API」成为**机器可读契约**（想加回来必须动 csproj 这个显眼文件）
  ② 快层末尾保留运行时墙钟闸门（> 3 秒即红）作第二道防线
  ③ 删掉 `-All` / `--all` 死开关（快层已无默认集 / 全集之分）
  ④ `dev.ps1 test` 改走「显式 build + 直跑产物」：**11.14 秒 → 3.24 秒**（绕开 dotnet run 的 MSBuild 增量评估）
- 实测：快层 **96 条 / 墙钟 0.65 秒**；慢层 **685 条（605 + 80）/ 全绿**；`dev.ps1 ready` 全过
- 搬迁是**字节级原样搬移，一条断言没改没删**（两边各自全绿，逐组条数与源码 Check 数完全吻合）
- 决策件：`docs/2026-09-25-流程体系减重建议.md`（含 P1~P8 冲突分析 + 8 条不做清单）
- 下一步：批 2 / 批 3 未做（P2a 检查节奏入规范 / P3 REGRESSION 拆册 / P4 daily 降级 /
  P6 触发表机器可读 / P8 改动分级）；合并顺序**必须先 chat-search 再本分支**（本分支从它开出）

## 另一条分支：feature/chat-search-refinements（已提交，**未合并未推送**）

- 2026-09-25 下午 [ZCode] AI 问答搜索四项（3号分流=会话内查找条 / ＋新建会话 / 搜索词历史胶囊 / 修分组内切会话）
- 涉及 `AIDialogWindow.xaml(.cs)`、`ChatSearchPanel*`、新 `Services/ChatSearchHistoryStore.cs`、`Services/AI/InSessionFindMatcher.cs`
- ⚠ REGRESSION 新增 6 行**待用户人工验收**；**必须先于 process-slimming 合并**

## main 状态（分支开出时）

- 与双远程一致（`3116b94`）；另有 `feature/chat-restore-cross-device`（未合并，保留）
- 人工验收旧账 2026-09-25 已全清（见 REGRESSION 结案注记）

## 遗留

- 沙箱内推送仍走 bash（`dev.ps1 push` 在 PowerShell 侧出不了网，见 `PITFALLS.md`）
