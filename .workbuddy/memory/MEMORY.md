# FocusCapture 项目长期记忆

> 细则/实例/历史 → 同目录 `MEMORY-DETAIL.md`；每日细节 → `YYYY-MM-DD.md`。
> C# WPF/.NET 8，本地优先、数据全本地、双远程（Gitee `origin`=pj1115 / GitHub=pengjie1115）。本文件进 git（2026-09-24 拍板：开发记忆共享给所有 Agent）。
> 跨 Agent 共享层：`NOW.md`（当前状态）/ `DECISIONS.md`（决策账本）/ `PITFALLS.md`（环境坑）——规则见根 `AGENTS.md`「三、开发记忆」；本文件是 WorkBuddy 注入视图。
> 规范真源：`AGENTS.md`（红线+路由，探针词「青柠」）/ `REGRESSION.md` / `MIGRATION.md`。

## 零、AI 模型多供应商改造（2026-09-23 完成，已并入 main 并推双远程）

- **合并与推送**：main = `6bc191e`，Gitee（origin）与 GitHub 均已同步到同一提交（`ls-remote` 核实）。分支 `feature/ai-model-providers` **按用户要求保留未删**；`feature/chat-restore-cross-device` 亦保留、未并入
- **设计稿** `docs/2026-09-23-AI模型板块重构设计稿.md`（§12 = 调研结论 + 3 处设计修正 + 检查点分层）；**调研** `docs/2026-09-23-供应商models端点调研.md`；**人工验收** REGRESSION.md **B-20**（13 条，全需真实环境手跑）
- **新结构**：设置拆成「AI 模型」（只管供应商 × 模型）与「AI 功能」（其余 AI 设置，索引 2）
- **代码入口**：`Services/AI/AiModelResolver.cs` = **配置→provider 的唯一映射点**（三级回退；末级读旧扁平字段兜底 —— 那是迁移的保命机制，别删）
- **四个纯函数进了快层**（零依赖、秒级、每次都跑）：`AiModelListParser`（宽容解析 `/models`）、`AiHealthClassifier`（失败四分：网络/Key/账户/供应商）、`ContextBudget`（裁剪 + 20% 余量 + **工具调用组不可拆**）、`TokenCountParser`
- **新增通用教训（以后必查）**：
  ① 往设置导航**插板块会让其后所有板块索引 +1**，而 `Diagnostics/UiSnapshot.cs` 按硬编码索引选板块 → 快照会**静默拍到隔壁**（不报错）；代码里判断"当前是哪个板块"要用**面板引用而非索引数字**
  ② 「路径/入口」类文案不只在文档里 —— 还写进了**代码里面向用户的提示**与**随包分发的技能文件**，且被检查点断言钉死。改它必须**协同改名**，改一半必红
- 注：`builtin-skills/` 的文案改动**只影响全新落地**的技能（落地即归用户，不自动更新）
- 时间线细节见 `.workbuddy/memory/2026-09-23.md`

## 零之二、AI 问答界面重构（进行中，2026-09-23 起）

- 分支 `feature/ai-chat-ui-revamp`（base `6bc191e`）；设计稿 `docs/2026-09-23-AI问答界面重构设计稿.md`（§0~§11）
- **进度**（分支 `feature/ai-chat-ui-revamp`，**9 提交，未合并未推送，ready 全绿**）：…→ 批 3a `9c6d148` → **批 A `d74a531`（悬停 Trigger SourceName / 分组置顶 `ChatGroup.Pinned` / MenuItem 深色模板 / 菜单父项不绑 Click / PromptDialog Loaded 后全选）→ 批 B `fc59446`（分组删除墓碑：`DeletedAt` + `Load`/`LoadAll` 口径分离 + 删除即终态永久保留）→ 批 C `30fe56c`（`StartNewSession` 加 groupId 修"分组内会话不归组" + 组内搜索 UI + 图标工具行 + 无返回按钮 + 点分组/发消息收侧边栏）→ 批 D `0f36d23`（批量操作找回：三点菜单入口+底部操作条；输入区按钮进框 + 圆形向上箭头 + 「选择分组」）→ 交接 `45b97a7`（`docs/2026-09-24-AI问答重构交接文档.md`）**。快层 175 / 慢层「会话分组」63 条全绿
- **剩余**（详见交接文档）：① REGRESSION 条数与触发表同步 ② 失效检查点（B-9/B-13、标题栏断言、SettingsWindow:440 文案）③ 设置项 UI（昵称/图标/头像/侧边栏默认展开）④ 侧边栏全局搜索 UI ⑤ 会话级模型下拉（先核实发送路径是否已消费 `SessionFile.ModelKey`）⑥ `GroupsChanged` 接线（**必须先给 ChatSyncEngine 加 Dispose**）
- **三条新铁律**：① DataTemplate 条目自身状态用 `Trigger SourceName`，`RelativeSource AncestorType` 会绑到外层共享元素（悬停全员高亮事故）② 带子菜单的父项**绝不绑 Click**（context=null 会被宿主解释成动作，"分组到…"清空分组事故）③ 同步里表达"删除"必须墓碑，并集必复活
- **快照教训**：Seeder 必须幂等 —— 每个场景 new 一个窗口但**沙箱是共享的**，不清数据第二张图里会重复两套；且 `dev.ps1 snap` **不先编译**，改完代码必须先 `build`
- **分组指令注入通道**：Agent 路径走 `AgentRunService.ExtraSystemContext`（每轮求值）；普通问答路径在请求时临时追加一条 system 消息，**不写回会话历史**。注入文本生成放在 `ChatGroupService.BuildInstructionContext` —— 放服务层才守得住"必须标来源与从属关系"这条安全要求
- **新增文件**：`Services/ChatGroupService.cs`（分组业务唯一入口）、`Services/Sync/ChatGroupMerge.cs`、`Services/ChatSearchService.cs`、`Services/AI/ChatSearchMatcher.cs`、`Services/ChatAssetsService.cs`
- **用户 9-23 追加 3 条决策**：侧边栏宽度**可拖拽**；欢迎语图标 / 应用图标 / 用户头像**三套彻底隔离**；搜索 = **消息正文全文搜索**（推翻初版"只搜标题"，规格见设计稿 §4.10）
- **待办**：批 1 数据层（`SessionFile.ModelKey` + 分组指令注入）→ 批 2 侧边栏（`ChatSidebar`）→ 批 3 起手页与输入区 → 收尾（`GroupsChanged` 接线 + `Dispose` + ready）
- **并行开发纪律**（用户 9-23 要求"能并行就并行，风险优先于速度"）：只并行**零共享文件的新增服务**；子 Agent 严禁跑 build/test/git（并发构建锁 + 慢层沙箱交叉污染，当天已有事故先例）；共享文件（`ChatGroupStore`/`ChatSyncEngine`/`AIDialogWindow`）一律主线程串行
- `GroupsChanged` 钩子**刻意暂未接线**：main 的 `ChatSyncEngine` 没有 `Dispose`，贸然订阅会造成测试环境事件泄漏 + 已停引擎定时器写真实目录
- **分组代码有 7 处问题（用户质疑"有问题"，实查成立，详见设计稿 §11）**：
  ① `ChatGroupStore` 读-改-写无锁 ↔ 后台同步做同样的事 = 竞态丢分组
  ② `HistoryDrawer.xaml.cs:160` 置顶会话 `continue` 摘出分组（与"置顶两处都显示"需求正面冲突）
  ③ `ChatSyncEngine.cs:449` 同 Id 分组"本地无条件 wins" → 跨端改名被回滚（根因：ChatGroup 无时间戳）
  ④ 两个"新建分组"入口查重行为不一致（拒绝并提示 vs 静默复用）
  ⑤ 删分组非原子 → 留下「（未知分组）」幽灵分区
  ⑥ 分组清单无变更通知钩子，空分组同步延迟；⑦ 组重映射触发再同步循环
- **修 vs 重写边界**：重写 `ChatGroupStore.cs` + 重建 HistoryDrawer/ChatGroupsWindow（并入新侧边栏）+ **只修补** `SyncGroupsAsync` 合并段。**不许整体重写同步引擎**（牵扯 E2EE / 删除闭环 / 坚果云限流红线）
- **新增字段**：`ChatGroup.Instruction` / `NameUpdatedAt` / `InstructionUpdatedAt`（后两个是跨端 LWW 的前提，属还债）；`SessionFile.ModelKey`（空 = 跟随全局 `ActiveModelKey`，老会话零迁移；但 `SettingsWindow.xaml:440` 文案语义要从"当前使用"改成"新建会话的初始模型"）
- **本次会导致失效、必须同步改的检查点**：`REGRESSION.md` B-13:317/320、B-9:564-575、`tests/sync/Program.cs:679`（标题栏绿色下划线断言）、`SettingsWindow.xaml:440`
- **已实现、勿重复做**：输入框滚动条（`AIDialogWindow.xaml:487`，样式走全局隐式样式且慢层禁止重定义）、删分组回未分组（`ChatGroupsWindow.cs:132`）
- 用户已拍板 12 条决策 + 代定 10 条（设计稿 §0 / §0.1）

## 一、环境坑
1. 沙箱 PowerShell 执行策略=Restricted：.ps1 一律加载失败且被吞成零输出 → 每次调用前同一条命令里 `Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass -Force`（不跨调用）；dev.ps1 的 Write-Host 要 `6>&1` 才抓得到
2. bash coreutils 全灭 → 一律 PowerShell；stdout 常被吞 → 结果落 %TEMP% 再 Read（`Out-File -Encoding utf8`；禁 `>`，`>` 产 UTF-16）
3. 写文件工具会假成功 → 落笔后 Grep 复核；同文件多 Edit 串行；生效与否只认独立证据（列目录/git status）
4. 联网 git（push/ls-remote）走 bash：PowerShell git 出网 128+零输出。判据「非零退出码+零输出」=沙箱限制，别动凭据/git 参数；401=凭据、502=网络重试
5. git 索引异常（切分支后文件误标 D）→ `git restore --source=HEAD --staged --worktree .`；禁 `git add -A`；提交前必 `git diff --cached --stat`；`branch -d` 误报用 `-D`
6. 中文传原生 exe 被 GBK 破坏 → commit message 写 UTF-8 无 BOM 文件走 `-F`；见乱码先验字节（首字节 E0–EF）别急着重写
7. 编译先补 APPDATA/PROGRAMFILES/ProgramW6432/ProgramData（否则 restore 报 path1 null）；新 csproj 加 `<EnableSourceControlManagerQueries>false</EnableSourceControlManagerQueries>`；.bat 不能含中文
8. 沙箱拦 Add-Type/Start-Job/Process.Start → 需子进程/P-Invoke 的探针写成项目内独立 csproj（范例 tools/clipdiag、tools/depdiag）
9. 读环境变量用 `[System.Environment]::GetEnvironmentVariables()`（`Get-ChildItem Env:` 返回空）；沙箱不把 `$env:PATH` 修改传子进程、中文路径进 env 变乱码 → 探针落 %TEMP%、纯 ASCII、路径全在进程内拼（9-21 定稿）
10. 本机无 gh CLI；GitHub 国内不稳别硬试

## 二、界面
- 界面问题必须出快照图（`dev.ps1 snap`）；窗口矩形裁切快照查不出 → 只能测量（先 UpdateLayout）
- UI 事件里的外部资源操作自己 try 住、不弹模态框（模态框吃掉后续点击=「点了没反应」）
- 浮层关闭幂等：`Dismiss()` + OnClosing 上锁 + 关窗前摘监听
- 图标唯一入口 `Services/AppIconService`（托盘 HICON 必须 Clone）；任务栏/开始菜单/桌面/exe 图标=内嵌资源，运行期改不了
- 悬浮球窗口 56×56、球 40×40（四周 8px 留白）；改尺寸同步 FloatBall.xaml 与 `ExpandedSize`；位置记忆存窗口原点，偏 4px 刻意不补偿
- **长驻面板一律 `Show()`（非模态）**：`ShowDialog` 是 WPF 模态，会禁用**应用内所有其他窗口**（官方文档原文「disables all other windows in the application」，2026-09-22 设置窗口实测坐实）—— 先开的灵感速览/AI 问答会集体点不动，且"先设置后面板"方向不对称（禁用名单在打开那一瞬定格）。只有真对话框才用 ShowDialog；非模态后必须自己挡重入（单例 + Closed 置 null）
- `ItemsControl` **没有** `ScrollIntoView`（那是 ListBox 的，编译报 CS1061）→ 定位条目用容器 `BringIntoView()`
- 列表项要"就地进编辑"时别重建列表：ItemsSource 重载会换掉条目对象引用、编辑目标与草稿一起丢 → 只读/编辑两套元素都建出来、只切 Visibility
- 固定高度的小弹窗改 `SizeToContent="Height"`：`Height` 含系统标题栏（≈31px@100%），固定值会把底部按钮裁掉一截（DueTimeDialog 实测内容要 182 而写死 170）
- WPF `BeginAnimation` 默认 HoldEnd：动画播完后仍占着属性，之后对属性的直接赋值全被吞（9-21 抽屉拖不动真凶）。凡动画后还要赋值/拖拽的：Completed 里先落本地值再 `BeginAnimation(prop, null)` 解除占用
- 牌号卡片按会话隔离（9-21 拍板）：显示+注入看 `ConversationRuntime.HandleIds`；`TryResolve` 保持全局（在途工具调用依赖它），别改

## 三、通则（事故换来）
- 本地状态推进不依赖外部接口成败（本地先推+云端尽力+失败退避+手动重试出口）
- 重试必退避、只重试暂时性异常、永不抛（`SafeClipboard` 范本）
- 不许把「登记成功」说成「已保存到网盘」
- 条目时间戳只有分钟精度 → 按时间定位必须自带内容消歧，撞车报错不猜
- Agent 工具体跑线程池线程 → 弹窗/碰 UI 必须走 `Services/UiThread.AskAsync`（失败=未确认，永不抛）；此类报错极像"授权过期"实际没发网 → 拿 app_*.log 堆栈定归因

## 四、测试纪律
- 快层 `tests/`、慢层 `tests/sync/`；条数以 REGRESSION.md §2 为准；判据双重：退出码 0 **且**含 `[RESULT] ALL CHECKS PASSED`
- 慢层测 WPF 对象丢独立 STA 线程；快层刻意不引主项目；隔离靠 `FocusCapturePaths.RootOverride`；无 CI/CD
- 检查点红了先自验修检查点，不许放宽凑绿；新增 .xaml/设置项/服务类 → 同步 REGRESSION.md

## 五、分支与远程
- 主线 main；双远程均与 main 同步；以 push 服务端回执为硬证据
- **每完成一步立即提交**（2026-09-23 用户拍板）：一个实施步骤做完就 commit，不攒到最后。理由是攒着提交 = 出问题时无法回退到「上一步的干净状态」
- 提交前必查 `git diff --cached --stat`；**只暂存自己改的文件**（工作区常有他人/前序会话的未跟踪文件，禁 `git add -A`）
- 提交信息走 UTF-8 无 BOM 文件 + `git commit -F`（中文直接传参会变 GBK，见一-6）
- 完全并入 main 的分支不打 tag；未并入的归档 → `docs/ARCHIVE-BRANCHES.md`
- **人工验收已全清**（2026-09-25 用户确认「旧账都已验收通过」）：B-9/B-10/B-12/B-16/B-21/B-22 全部 ⚠ 条目 + B-6 热键三条 + chat-search 覆盖层与词级高亮，全绿；`REGRESSION.md` 各段已加 ✅ 结案注记（7 处）。唯一保留 ⚠ 的是 B-6「任务栏/开始菜单/桌面/exe 图标不跟随」= 平台能力边界（读 exe 内嵌资源，运行期改不了），**永久无验收必要**

## 六、同步
- 行 ID=SHA256(完整行文本)不含路径（`SyncNote.ComputeId`）；迁移按 MIGRATION.md，顺序不可颠倒
- 标签承载 `SyncNote.Tags`（不参与行 ID，改标签不产墓碑）；标签写进正文=改行 ID=删旧加新（禁止）；Tags 目前是文件名投影，要多标签必须另立独立来源（9-21 结论）

## 七、关键约定
- Skill 依赖 CLI 由应用负责：`runtime\<id>\` → AppData → PATH，不硬编码他处；授权设备码流应用内走完；登录态判据=JSON 字段非退出码
- 面向用户/模型的文案禁出现命令行/终端步骤/`tools\*.bat`（慢层有断言）
- 剪贴板统一走 `SafeClipboard`；「复制失败」先跑 tools/clipdiag（常是网易UU远程抢占，非本应用 bug）
- 速览手势（9-18 拍板）：单击无动作、双击进编辑、复制只走右键
- 「改内容」=原地替换（9-20 拍板）：旧内容先进回收站再覆盖，旧行发 LinesDeleted，旧【编辑】痕迹清理（【AI 释义】保留）
- Agent 工具装配点唯一=`AIDialogWindow.EnsureAgentRegistry()`；`Services/Agent/` 禁引 Destinations
- 移动端 `D:\dev\FocusCapture.Mobile` 独立仓库，桌面端只读（连 git status 都不许跑）
- 已搁置：批注/quote（永久）、悬浮球输入框图片+速览面板图片
- 文档用完可丢；禁把文档路径/章节号写进源码注释当设计依据

## 八、脚本纪律
- 日常动作走 `tools/dev.ps1`：build/test/test -Slow/ready/status/start/merge/push/recover/snap/commit
- 退出码：0 成功 / 1 任务失败 / 2 脚本故障（交付附脚本异常报告）
- 脚本铁律：只存「动作」不存「知识」，会变的一律现场读取（防软失真）
- 批量删文件后必复查工作区；`dotnet publish`≠build（sherpa.onnx 原生库从 NuGet 直进 publish 目录）→ 改 build 收尾必问"publish 那条路呢"，修法挂 `AfterTargets="Publish"`

## 九、效率纪律
- git 操作先 bash；Edit 后立即 Grep 复核；编译前一次性补环境变量
- UI 改动优先出快照图给用户看；用户报问题先问清「几个独立问题+现象+期望」

## 十、Skill 授权闭环（进行中）
- 飞书授权跨机失效根因：只做 auth login 没做 config init；config.json 只是 keychain 引用，拷文件不搬密钥
- `config init --new` 真值：全 stderr、纯文本+URL、非 JSON、约 1s 流式后阻塞；抓到 URL 不能 kill；超时 kill 保留已读
- 设计稿 `docs/2026-09-21-依赖授权闭环设计稿.md`；分支 feature/dep-auth-prepare

## 十一、AI 问答 / Agent 确认弹窗（9-21 调查）
- 三道闸：①写操作确认窗（标题「AI 操作确认」，`AgentRunService.ConfirmHandler`，受设置 `AgentWriteConfirmPopup` 管，默认关）②Skill 准入窗（`SkillScriptRunner.TrustPrompt`，每 Skill 首次跑脚本弹一次并记住，无开关，安全地基）③依赖授权窗 `SkillAuthWindow`（外部 CLI 未登录时弹）
- `run_skill_script` IsReadOnly=false → 开关开着时**每次**脚本调用都弹写确认窗，与准入窗叠加双弹（设计如此但吵）
- 9-21 实测：settings.json 里 `AgentWriteConfirmPopup=true` 而用户自称已关 → 落盘未生效原因未解，待活体复测（用户关一次→立即读 settings.json）
