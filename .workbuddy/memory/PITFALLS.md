# PITFALLS — 确定性环境坑（只收「跨会话零推导」的坑；新坑追加，失效即删）

## 编码
- PowerShell 的 `>` / `>>` 重定向产出 **UTF-16**，Read 工具拒读 → 落盘用 `Out-File -Encoding utf8`，追加用 `Add-Content -Encoding utf8`
- PowerShell 传中文参数给原生 exe 被 **GBK 破坏**且换行变参数分隔 → 多行中文提交信息写 UTF-8 无 BOM 文件走 `git commit -F`
- `git log` 中文乱码 = 控制台解码问题非数据问题；核对用 `[Console]::OutputEncoding = UTF8` 再跑

## 沙箱（WorkBuddy shell）
- APPDATA / PROGRAMFILES / ProgramW6432 / ProgramData 为空 → `dotnet restore` 报 path1 null（`dev.ps1 build` 已自动补，手跑命令要自己补）
- bash coreutils 不可靠 → 一律 PowerShell 原生 cmdlet；命令输出落 %TEMP% 文件再 Read
- PowerShell 执行策略 Restricted → 同一条命令里先 `Set-ExecutionPolicy -Scope Process Bypass -Force`
- 沙箱不把 `$env:PATH` 修改传子进程；中文路径进环境变量会乱码 → 探针纯 ASCII、路径进程内拼
- 联网 git（push/ls-remote）走 bash；PowerShell git 出网 128+零输出 = 沙箱限制，别动凭据
- shell 里用 `cat > 文件 <<EOF`（heredoc 写文件）会被安全策略判为 **LOLBin** 拦下 → 改用文件写入工具（例：写 `.git/FC_COMMIT_MSG`）

## 工具行为
- 写文件工具可能**假成功** → 落笔后 Grep 复核；同文件多 Edit 串行
- **`dev.ps1` 的输出全走 `Write-Host`** → `& dev.ps1 ... | Out-File` **一个字都抓不到**（落盘只剩自己写的那几行）→ 要抓必须 `6>&1`
- **`dotnet run` 每轮都做一整轮 MSBuild 增量评估**（2026-09-25 实测：快层吃掉 9.4 秒，而断言本体只要 0.73 秒）→ 频繁跑的路径改「显式 `dotnet build` + 直跑已编译产物」（本项目 `dev.ps1 test` 已这么改）；**前提是先 build 再跑**，否则会拿旧二进制当结果
- `dev.ps1 snap` **不先编译** → 改完代码先 `build` 再 `snap`，否则出的是旧图
- 快照 Seeder 必须**幂等**——每场景 new 窗口但沙箱共享，不清数据会重复两套
- 剪贴板写入失败先跑 `tools/clipdiag`（常是网易UU远程抢占，非本应用 bug）
- 取证 / 诊断文件**一律落 `%TEMP%` 且用固定文件名**（`fc-<用途>.txt`，下次同名覆盖），**别建在用户主目录**；`%TEMP%\fc-*` 会长期堆积——2026-09-25 清出 **356MB**，其中单个 `fc-typecheck` 是某次构建的输出、独占 **348MB**。用完即清，「临时」别拖成「永久」
- 批量删除有护栏：**单轮超 50 项**触发 `SAFE_DELETE_BULK_CONFIRM_REQUIRED`，同轮后续请求一律被拦。护栏包装的是 shell 的 `rm`（`safe-bin/rm` shim）与 PowerShell 的 `Remove-Item`；**换 .NET 文件 API（`[System.IO.File]::Delete` / `[System.IO.Directory]::Delete`）可一次清完**（2026-09-25 实测 229 项，OK=229 / ERR=0）—— 但那条路径**没有护栏兜底，必须先拿到用户明确授权**再走
- `Remove-Item` 不接受管道输入（`$list | Remove-Item` 报 ParameterBindingException）→ 用 `-LiteralPath` 带数组、传目录加 `-Recurse`

## WPF / .NET
- `ItemsControl` 没有 `ScrollIntoView`（那是 ListBox 的）→ 用容器 `BringIntoView()`
- `ShowDialog` 禁用应用内**所有**其他窗口 → 长驻面板用 `Show()` + 自挡重入
- `BeginAnimation` 默认 HoldEnd 占住属性 → 动画后要赋值/拖拽的，Completed 里先落本地值再 `BeginAnimation(prop, null)`
- 固定 `Height` 的小弹窗会裁掉底部按钮（Height 含系统标题栏 ≈31px）→ 用 `SizeToContent="Height"`
- DataTemplate 条目自身状态用 `Trigger SourceName`，禁 `RelativeSource AncestorType`（会绑到外层共享元素）
- 带子菜单的父项**绝不绑 Click**（context=null 被宿主解释成动作）
- XAML 模板根只能一个子级（MC3089）；模板内 x:Name 不进窗口 NameScope
- **Auto 列宽 = 所有子元素期望宽度的最大值**：往 `Width="Auto"` 的列里塞用户可控长度的文本，文本会把列撑宽（2026-09-24 实测：AI 问答标题栏放长助手名，侧边栏从 220 涨到 400）。修法是让该子元素的期望宽度不超列源——`MaxWidth` 绑宽度源的 `Width`（**且该元素自身不能带 Margin**，Margin 不计入 MaxWidth，照样撑宽；边距要下沉到子元素）

- **JIT 编译 `Main` 时会加载 `Main` 里引用到的类型** —— 所以「把自己这个 exe 打进包、解压后再跑」的做法，必须连**整个输出目录的托管程序集**一起打（2026-09-25 实测：慢层 exe 的 `Main` 引用了 `FocusCapturePaths`，只放 exe + 自己的 dll + runtimeconfig 会直接 `Unhandled exception`；快层 exe 几乎零第三方依赖，所以原先只放 4 个文件就够）。**跨工程搬测试代码时，两个工程的依赖树差异会以这种形式冒出来**

## Git Bash 会转换 `git show 分支:路径` 的冒号参数（2026-09-24）

Git Bash（MSYS）把含 `:` 的参数当路径转换：`git show feature/x:.workbuddy/memory/2026-09-24.md` 会变成 `feature\x;.workbuddy\memory\...` → fatal: ambiguous argument。
解法：命令前加 `MSYS_NO_PATHCONV=1`，或先把两个 ref 各自 dump 到临时文件再 diff。
