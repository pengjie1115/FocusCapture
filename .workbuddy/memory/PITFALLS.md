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

## 工具行为
- 写文件工具可能**假成功** → 落笔后 Grep 复核；同文件多 Edit 串行
- `dev.ps1 snap` **不先编译** → 改完代码先 `build` 再 `snap`，否则出的是旧图
- 快照 Seeder 必须**幂等**——每场景 new 窗口但沙箱共享，不清数据会重复两套
- 剪贴板写入失败先跑 `tools/clipdiag`（常是网易UU远程抢占，非本应用 bug）

## WPF / .NET
- `ItemsControl` 没有 `ScrollIntoView`（那是 ListBox 的）→ 用容器 `BringIntoView()`
- `ShowDialog` 禁用应用内**所有**其他窗口 → 长驻面板用 `Show()` + 自挡重入
- `BeginAnimation` 默认 HoldEnd 占住属性 → 动画后要赋值/拖拽的，Completed 里先落本地值再 `BeginAnimation(prop, null)`
- 固定 `Height` 的小弹窗会裁掉底部按钮（Height 含系统标题栏 ≈31px）→ 用 `SizeToContent="Height"`
- DataTemplate 条目自身状态用 `Trigger SourceName`，禁 `RelativeSource AncestorType`（会绑到外层共享元素）
- 带子菜单的父项**绝不绑 Click**（context=null 被宿主解释成动作）
- XAML 模板根只能一个子级（MC3089）；模板内 x:Name 不进窗口 NameScope
