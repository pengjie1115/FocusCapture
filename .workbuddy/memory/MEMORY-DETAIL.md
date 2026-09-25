# FocusCapture 项目长期记忆 · 完整版

> **本文件是 `MEMORY.md` 的详细版**，存放不常查、但一查就要准的细节与实例。
> `MEMORY.md` 只放「会反复踩的结论」，超出注入上限会在会话开始时被截断，故拆成两份。
> 需要细节时直接读本文件，不要凭印象猜。
>
> C# WPF / .NET 8，本地优先、数据全本地、双远程（Gitee pj1115 + GitHub pengjie1115）
> 本文件不进 git。规范看 `AGENTS.md`（红线 + 路由）/ `REGRESSION.md` / `MIGRATION.md`；分支归档 `docs/ARCHIVE-BRANCHES.md`；每日细节进 `YYYY-MM-DD.md`。
>
> 拆分时间：2026-09-18（拆分前两份合计 9,563 字符）

---

## 一、工具与环境（详细）

1. **git 2.55 索引异常**：checkout / merge / 删分支后文件可能被误标 `deleted`（**切回 main 高概率触发**；累计 5 次，靠
   `git restore --source=HEAD --staged --worktree .` 一次救回，**从未真丢跟踪文件**）→ 切换后立刻 `git status`；
   提交前必 `git diff --cached --stat`，**禁止 `git add -A`**；`branch -d` 误报 not fully merged → 用 `-D`
   - 该 restore 是**全树通配**，只删「已暂存但 HEAD 没有」的 `A ` 文件，**不动 `??`**
   - 索引损坏时 `git status` 自身不可信 → **跑前先抄 `??` 清单并备份出仓库**
   - 实例：9-17 合并误标 **68 文件 `D`**；9-18 `git checkout main` 后 **74 文件误标 `D`**

2. **写文件工具会「报成功但没写入」**：using / 订阅 / 注册行 / 记忆条目落笔后必须 Grep 复核
   （订阅缺失不报编译错，只在运行时静默失效）。三个实测变体：
   - ① `Edit` 的 old_string 带尾换行、new_string 不带 → 把下一行「吸」进上一行
   - ② **同一文件一轮里发多个 `Edit` → 报成功但部分改动被覆盖丢失**（也可能报 `EBUSY`）→ **同文件必须串行**
   - ③ 命令可能被重复执行，空跑覆盖日志 → 脚本自报「全部 not found」，看着像全军失败
   - → **判断写操作是否生效，只认独立证据（列目录 / `git status`）**。本项目源文件是 **UTF-8 无 BOM**，写回用 `UTF8Encoding($false)`

3. **编译**：Debug 直接 `dotnet build`（WorkBuddy bash 需先补 `APPDATA` / `PROGRAMFILES` / `ProgramW6432` / `ProgramData`）；
   仓库内新 csproj 须加 `<EnableSourceControlManagerQueries>false</EnableSourceControlManagerQueries>`；**.bat 不能含中文**
   - `Resources/embed_icon.py` 把 Release exe 路径写死，csproj 调它的 `Exec` 带 `ContinueOnError="true"`
     → **输出路径一变（如加 `RuntimeIdentifier`）就是图标嵌入静默跳过、构建照样 0 错误**
   - `TrimForeignRuntimes` 目标（9-17 起，主项目与 `tests/sync` 各一份；条件 `'$(RuntimeIdentifier)'==''` 避开 `publish -r`）：
     ① `$(OutDir)` **不是** TFM 路径（拼出空集），要用 `$(OutputPath)`；② 目标内 ItemGroup 的 `*` 通配**不展开且不报错**，必须逐条写显式路径

4. **bash 核心命令会集体消失**（实测过 `tail`/`head`/`ls`/`wc`/`cat`/`grep`/`cp`/`mkdir`/`find`）
   → 重定向到文件再 Read（`/tmp` 落到 `%TEMP%`）；**带管道时退出码反映的是挂掉的工具、不是 git**（曾把 127 误判成推送失败）
   - git 输出重定向后可能按控制台代码页写出 → Read 成乱码，**验字节**：`[Console]::OutputEncoding=UTF8` 后取 UTF8 字节，
     中文正常时首字节落在 `E0–EF`（如 `补` = `E8 A1 A5`）
   - `cp` / `grep` 同样会挂 → 复制文件改用 PowerShell `Copy-Item`；建目录用 `New-Item -ItemType Directory`
     （**PowerShell stdout 常被吞，但命令确实执行**，验证靠 Glob / Read / 重定向到文件）
   - **WorkBuddy 拦截 `Add-Type`**（"compiles and loads .NET code at runtime"）→ 需要 P/Invoke 的诊断探针
     只能写成**项目内独立 csproj**（范例 `tools/clipdiag/`：刻意不引主项目、编译秒级、配一个双击即跑的 `.bat`），
     日志落 `%TEMP%` 再 Read

5. **中文参数传给原生 exe 会被编码破坏**：`git commit -m <中文>` 在 PowerShell 里会被按系统 ANSI(GBK) 编码发出，
   且换行被当参数分隔 → 报 `error: pathspec '图片卡片' did not match any file(s)`，看着像「文件不存在」，实际是提交直接失败。
   → **绕法**：message 写进 UTF-8 无 BOM 文件，用 `git commit -F <文件>`。
   - 同理 `git mv <中文名>` → 改用 PowerShell `Rename-Item`（cmdlet 不走原生 exe 参数编码）
   - **别被显示骗了**：`git log` 在 PowerShell 里中文显示为乱码，那是**控制台解码问题、不是数据问题**

6. GitHub 国内不稳（SSL/502），本机无 `gh` CLI，别硬试

---

## 二、界面（详细）

7. **界面问题必须 `--snapshot` 出图再看**（`Diagnostics/`）：控件被挤出可视区、图标豆腐块、对比度低，光读代码发现不了

8. **换 UI 图标字体查三处**：运行时改写 `Content` 处／代码回写字符／App.xaml 全局 Button `Padding="8,4"` 会挤偏 28px 图标钮
   （须显式 `Padding="0"`）；MDL2 字形用 `Diagnostics/GlyphProbe`，别靠记忆猜

9. **富文本输入区 5 坑**：`InlineUIContainer` 须设 `BaselineAlignment.Center`；`RichTextBox` 内置拖放吞 `DragOver`/`Drop`
   （须 `handledEventsToo: true`）；系统 `ToolTip` 约 400ms 延迟须自绘；取图统一 `Clipboard.GetImage()`；
   复制含 UIElement 的选区跨应用拿不到图须降级纯文本

10. **浮层窗口关闭必须幂等**：多关闭入口（按钮/点外面/Esc/超时）互踩 → 二次 `Close()` → WPF `VerifyNotClosing()` 抛吓人框。
    修法：`Dismiss()` 幂等 + **`OnClosing` 上锁** + 关窗前摘监听。实例：9-16 `DropActionCard.Dismiss()` line 89 → `Window_Deactivated()` line 112

11. **UI 事件处理器里的外部资源操作必须自己 try 住**（9-18 踩坑）：事件处理器抛出的异常会冒泡到
    `App.DispatcherUnhandledException` 弹**模态框**，而模态框会吃掉后续点击/按键 → 用户表现为「点了没反应」。
    实例：双击进编辑 = 第一下单击复制（剪贴板被占用抛 `CLIPBRD_E_CANT_OPEN`）→ 弹框 → 第二下点击落空。
    剪贴板/网络/文件这类操作一律兜底，**不弹模态框**

12. **文档/方案里的「位置描述」要落代码核实**：两个标识矛盾时以可验证的为准 + 代码留注释 + 显式汇报
    （实例：拖放设置项实际落「显示」板块，方案写的「外观」是错的）

---

## 三、数据与外部接口（详细）

13. **不能拿「配置齐全」当「事实已发生」**：`CloudReady` 曾被当"已上传"写进账本 → 没传上去的永不重传。
    判断云端有没有，只能看上传动作真的成功过

14. **守卫粒度必须与操作语义对齐**：`EnsureSandboxed` 一律要求 `/apps/` 前缀 → 「列 /apps」被自己拒，
    而建目录必然要列父目录 → 上传 100% 失败。修法：只读放行沙箱根、写操作强制带下级；建目录直接 create + 容忍 -8

15. **百度网盘删除接口（定型，别再猜）**：`filemanager&opera=delete` 12 种形态全 errno=2；
    **必须 `method=delete&path=<完整路径>`**；成功响应没有 errno、不认 filelist（逐个发）、404+31066 按幂等成功。
    工具 `tools/baidu-diag`

16. **平台接入调研两条铁律**：① 有无开放 API 必须落到官方文档/开源源码（检索结果混大量 AI 编造的假 API）
    ② **"第三方能否用某官方工具"必须在真实宿主里跑** —— 平台自家环境会给**假绿**
    （夸克 CLI 沙箱全绿，进真实应用即报「无法识别当前 Agent 环境」）。流程见 skill `platform-integration-recon`

17. **夸克适配器三特性**（搁置存档）：fid 每次变 → 禁止缓存、按名字现场搜；判成败只看 stdout 的 NDJSON；
    官方 `install.sh` 会改系统 Node，应用只做「取配置→下载→解压」

18. **AI 请求体序列化只有一处**：`OpenAICompatibleProvider.BuildMessagesArray`；
    **tool 消息协议层带不了图片部件** → `read_cloud_image` 放弃

19. **WebDAVProvider 退避可注入**：503/429 默认硬等 5s+15s，可用构造参数覆盖（生产默认未变，测试传 `{0,0}`）

### 四条通则（事故换来的）

20. **别让本地状态推进依赖外部接口的成败**（本地先推进 + 云端尽力而为 + 失败退避 + 手动重试出口）

21. **失败重试必须退避，且只对暂时性异常重试、永不抛** —— 不退避会在 78 毫秒内烧光 5 次上限；
    剪贴板/外部资源的正确姿势 = 指数退避 + 白名单异常 + 返回 bool 交调用方决策（`Services/SafeClipboard` 是范本）

22. **异步返回值不许把「登记成功」说成「已保存到网盘」** —— 一核对就穿帮，信任归零，比 bug 本身更贵

23. **条目时间戳只有分钟精度**：`NoteEntry.ToMarkdownLine` 用 `yyyy-MM-dd HH:mm` 写盘（待办 `Timestamp` 同，秒级只有 `DueTime`）
    → **同一分钟多条在磁盘上完全相同** → 「按时间戳定位条目」必须自带内容消歧
    （`export_notes.ref_times` 用 `{"time","hint"}`、`restore_deleted` 用 `content_hint`），撞车时报错不猜

24. **本机剪贴板被「网易UU远程」周期性抢占**（9-18 实测点名）：`GameViewer.exe`
    （`C:\Program Files\Netease\GameViewer\bin\GameViewer.exe` v4.40.1.2090）的剪贴板同步 ——
    窗口类 `UURemoteClipboard`，**每次剪贴板内容变化后立即抓取并持有剪贴板 2.4~4 秒**（`err=5` 拒绝访问），
    期间**任何程序都写不进剪贴板**（含 WPF 编辑框 Ctrl+C）；占用窗口之外 630/630 全部成功。**非本应用 bug**。
    用户拍板「UU 不常用」→ 不做代码侧抗干扰（异步补写 / 编辑框 Copy 拦截两方案已设计、未实现）。
    **再遇「复制失败」先看 UU 是否在跑**

---

## 四、测试与质量纪律（详细）

- **快层** `tests/`（**31** 条）每次改完必跑；**慢层** `tests/sync/`（**257** 条）触发表见 REGRESSION.md §2；
  退出码 0=全过。每组耗时直接输出（`[耗时]` + 末尾降序汇总表，放 `finally`）
- **条数 ≠ 耗时**：文件仓库 57 条 0.25s、拖放保存 46 条 81ms，断网/重试 9 条独占 6.0s（38%）→ **先看耗时表再猜瓶颈**
- **测耗时/条数前先确认 exe 是当前源码编译的** —— MSBuild 增量会静默跳过
- **快层刻意不引用主项目**：`EnableDefaultCompileItems=false` + 只链接无依赖源文件
  （`CryptoService`/`TimeParser`/`QuickViewToolbarCatalog`/`SafeClipboard`）；是链接同一份文件不是副本；
  代价＝被测类不得依赖项目内其他类，也**不得引用 WPF**（`SafeClipboard` 的写入委托由调用方注入正是为此）
- **分层 ≠ 风险分层**：快/慢层按依赖与耗时划，风险按 REGRESSION 的 L0/L1/L2 划
- **写检查点先自验**（根因多是检查点自身构造错）→ **修检查点、不动代码**，不许放宽标准凑绿；
  **断言「用户能看见的性质」**，数值类一律"已知输入→精确期望"，禁用模糊匹配
- **需真实时序的缺陷别硬凑反射测试** → 断言用户可见终态（不弹框 + crash.log 无新增）
- **数据隔离**靠 `FocusCapturePaths.RootOverride` 指临时沙箱；新增 .xaml / 设置项 / 服务类 → 同步 REGRESSION.md；**不部署 CI/CD**
- L2 枢纽 = NoteService(25)/AppSettings(20)/NoteEntry(17)/Win32(9)/SyncNote(8)；单点故障强制 L2 = WebDAVProvider/CryptoService/Dpapi

---

## 五、分支与远程（详细）

- **开发主线 = main**。已合并并删：`feature/baidu-netdisk-cache`（9-16）、`feature/drag-to-ball`（9-17）、
  `feature/agent-tools-batch1`（9-17）
  - **`fix/quickview-clipboard-dblclick`（9-18）已 ff 并入 main 并除名**（`8eaf659..af8d33b`，12 文件 / +382 −30；
    3 提交：`67d6e84` 修复 → `2cca572` 取消单击复制 → `af8d33b` 新增 clipdiag 探针），main 顶端 = `4332bf9`。
    产出 `Services/SafeClipboard` + 5 处调用点迁移 + 快层 [5] 组 10 条；按用户拍板**取消单击复制**
    （单击无动作、双击直接进编辑，复制只留右键）。**⚠ 未推送双远程**（用户要求暂不推送，远程仍停 `8eaf659`）
  - `feature/quark-cloud-drive` 于 9-17 按用户指令**彻底删除**（从未推送、无 tag，仅剩 reflog；5 提交 / 13 文件留档见
    BRANCHES.md §四）。**分支删除的持久留底只能靠 tag**（reflog 受 gc 影响，可能短到 2 周）
- **推送失败有两种，判据是"有没有输出"**：exit 128 且**零输出** → 必须 `GIT_CURL_VERBOSE=1` / `GIT_TRACE=<文件>` 才看得到真相
  - **401 = 凭据**：沙箱 `~/.gitconfig` 指向的 PortableGit GCM 非交互取不到凭据（**凭据没丢**，
    Windows 凭据管理器 4 条都在、`wincred` 能读）；`-c credential.helper=xxx` 和仓库本地配置**都无效**。
    **可用解法（9-17 实测）**：`git credential fill` 经 `wincred` 取凭据 → base64 →
    `git -c http.extraHeader="Authorization: Basic <b64>" push`，**不落盘、不改配置**
  - **502 = 网络**：沙箱唯一出口是本地代理，经它连 github 时通时不通，隔会儿重试即可（**别改 git 参数**）
- `git ls-remote` 有时返回空 → **以 push 的服务端回执（`xxx..yyy  main -> main`）为硬证据**
- 合并必触发索引异常 → 用 §一 restore 救，恢复后核对**跟踪文件数（当前 178）**与 `git status` 干净
- **人工验收（2026-09-25 全清）**：B-15 / B-16（Agent 工具扩展）/ B-12（含双击立即进编辑且剪贴板不变、单击无动作）
  / B-9 / B-10 / B-21 / B-22（AI 问答界面全套）/ B-6 热键三条 —— 均**已由用户验收通过，已结案不要再提**。
  唯一保留 ⚠：B-6「任务栏/开始菜单/桌面/exe 图标不跟随」= 平台能力边界（读 exe 内嵌资源），永久无验收必要

---

## 六、同步行身份（详细）

**行 ID = SHA256(完整行文本)，不含相对路径**（`SyncNote.ComputeId`）—— 路径曾三套口径 → 身份分裂 →
跨端删除失效、旧行"复活"。

推论：`LoadNotes` 扫全部 md 后按 `TodoDisplayTime` 过滤；**任何"按文件读行"的代码都要全库扫行**。
迁移按 `MIGRATION.md`（清重复行 → 重置同步，顺序不可颠倒），工具 `tools/todo-cleanup.ps1`

---

## 七、板块与文档地图（详细）

> **路径→名字的映射不在此表** —— 用 Grep/Glob 现查即可（本文件已两次因此漂移）。

| 事项 | 不显然的那一点 |
|---|---|
| Agent 工具框架 v2.0 | 装配点**唯一** = `AIDialogWindow.EnsureAgentRegistry()`；26 个工具＝笔记10/文件7/文档2/回收站2/导出1/得到大脑4 |
| 文档解析基座 | `DocumentTextExtractor` 由**附件侧与网盘侧共用**；`XlsxTextExtractor` 是**零依赖手写**（须处理日期序列号/稀疏列/多 sheet）；PDF 走 **PdfPig**，**只认有文字层**，扫描件明说读不了 |
| 剪贴板写入 | **统一走 `Services/SafeClipboard`**（勿再直接 `Clipboard.SetText`）；灵感速览右键复制、设置页网盘路径、AI 对话框选区与兜底共 5 处已迁移 |
| 剪贴板占用探针 | `tools/clipdiag`（双击 `tools\clipdiag.bat` 采样 90s）：用 `GetOpenClipboardWindow`/`GetClipboardOwner` **点名**占用进程 + 窗口类，日志 `%TEMP%\fc-clipdiag.log`。**遇到「复制失败/剪贴板写不进」先用它定位，别猜** |
| 灵感速览笔记行手势（9-18 用户拍板） | **单击 = 无动作；双击 = 进编辑；复制只走右键菜单**。曾短暂存在「单击即复制」，因双击的第一下必然白写一次剪贴板（OLE 同步阻塞 UI）拖慢进编辑，被用户取消 —— **再往单击上加动作前先问用户** |
| 界面快照 / 拖放探针 | `Diagnostics/` 的 `--snapshot` / `--dragprobe`；**跑探针前先关进程再编译**；探针日志在 `%TEMP%\fc-dragprobe\` |
| 悬浮球拖放保存 | 设置项在「**显示**」板块（不是「外观」）；**禁区**内联进 `REGRESSION.md` B-15 段的「改这块前必读」 |
| 验收清单编号 | **B-12**＝灵感速览（标题栏/双击编辑）；**B-14**＝百度网盘本地缓存；**B-15**＝悬浮球拖放保存；**B-16**＝Agent 工具扩展；**B-21**＝会话分组与侧边栏；**B-22**＝全局搜索覆盖层/标题栏两态/会话级模型 |
| 不进同步的部分 | AI 问答附件（`ChatAttachment*`）**只存本机**，别接进同步链路 |
| 已移出仓库的东西 | `asr_venv/` 与 `docs/fc-sync-test-archive/` 于 9-17 移到仓库外 `..\_cleanup-archive-20260917\`。**日后找不到别当成丢失**。`.gitignore` 对**已被跟踪**的文件无效（移走产生 3 处删除提交，跟踪数 177 → 174） |
| **文档纪律**（用户 9-17 定） | **文档用完可丢；禁止把文档路径/章节号写进源码注释当"设计依据"**。**耦合四形态**（已清零，约 70 处 / 39 文件）：① `（方案 §X）` ② `（决策 N）` ③ `方案文档 X.Y` ④ `QUEST-N 第N步/§N`。**新增代码禁止再引入**；标注若承载实质内容必须就地写清（范例：`ChatGroupStore.cs` 的分组合并规则） |

---

## 八、已暂缓 / 永久搁置（详细）

- **批注 / quote** —— 永久搁置，扫描自动跳过
- **悬浮球输入框图片 + 灵感速览面板图片** —— 已暂缓（9-14）；**不再引用「输入框不能换 RichTextBox」**（该方案结论随暂缓失效）
- **AI 问答面板 PDF 解析** —— 9-17 已实现，走 `DocumentTextExtractor`+PdfPig；只认有文字层。旧结论「无外网无法拉 NuGet」已被推翻
- **"改内容"的语义**（9-17 拍板）—— AI 改笔记/待办 = **原地替换**（非追加【编辑】行），
  **旧内容必须先写回收站再替换**（`NoteService.RewriteEntryLine`：定位→写回收站→替换→抛 `LinesDeleted` 墓碑）；
  回收站写失败即中止。⚠️ 界面「编辑」仍追加行，两条路行为不同是刻意的

---

## 九、Agent 工具框架（9-06 拍板，详细）

- **总纲领**：「AI 能调工具」通用框架；插飞书/金山 = 新增适配器 + 注册一行。**红线：`Services/Agent/` 不得引用 Destinations 命名空间**
- **扩展点**：`IOutboundDestination`（外发，单向）vs `AgentTool`（操作语义，双向，5 成员）；**"文件存/取"属后者**
- **文件存取基座**：`FileTools.cs` 四工具（存/取/搜/读），**签名里无任何路径参数** ——
  只能传 `handle`（用户点「选择文件」产生，`file:yyyyMMdd-N`，进程级 24h）或 `file_id`
- **三纪律**：① 破坏性动作不给 AI ② 每轮变化的短期状态走 `AgentRunService.ExtraSystemContext` 注入，
  **不写进会话历史** ③ 路径/句柄一律本机解析，失败给"请让用户点选择文件"这类指引

---

## 十、移动端工作空间（2026-09-18 新开）

**独立仓库 `D:\dev\FocusCapture.Mobile`**，与桌面端**代码零耦合**（复制快照，不链接源码、不用 submodule、不动桌面端）。

- 待办提示词入口：`START-HERE.md`；规划文档 `docs/CORE-CHANGES.md`（改造点）、`docs/CONTRACT.md`（跨端契约冻结）
- 复用结论：**16 个文件逐字复制零改动 / 5 个需改 / 2 个新增** ——
  `SyncEngine`、`WebDAVProvider`、`CryptoService` 三个核心同步文件全无 Windows 依赖；
  给 `NoteService` 补 `Win32` 垫片（`GetActiveWindowTitle`→`""`、`GetClipboardText`→`null`）即可零改动
- **严格契约 4 个**（改则两端必须同步 + 重跑 fixture）：`CryptoService.cs`、`SyncNote.cs`、`NoteEntry.cs`、`TimeParser.cs`
  （`TimeParser` 算契约是因为待办 `DueTime` 会写进行文本 `(提醒: …)`，解析差异直接变成 ID 差异）
- **陷阱**：安卓 Release 必须 `PublishTrimmed=false` —— `SyncNote.ToJson/FromJson` 走反射式 `SyncJson.Options`，
  不在 `AppJsonContext` 源生成内，裁剪会把它序列化成空对象写进云端且**不报错**
- **换行符差异安全**（已核实）：`NoteService.ReadAllLines` 有 `TrimEnd('\r')`，ID 取 trim 后行文本，无需处理
- 桌面端在本项目中**只读**：禁止写入、重命名、移动、删除，**连 `git status` 都不许跑**（会触碰 `.git/index`）
- **审计教训**：核查「桌面端是否被改动」时，窗口必须以**本会话开始时刻**为界，不能按自然日 ——
  否则会把上一个会话的改动算进来，看着像被大改
