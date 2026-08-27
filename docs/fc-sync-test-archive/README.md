# fc-sync-test 归档（QUEST-5 同步验收测试）

> 归档日期：2026-08-27 | 原位置：`专注力工具 - 上传git - 260726/fc-sync-test/`（已删除，仅保留源码归档于此）

## 这是什么

FocusCapture **QUEST-5 云端同步功能的验收测试程序**（单机双设备模拟，验收 B/C/D/E/F）：
- 本地 WebDAV 桩（HttpListener 实现 PROPFIND/PUT/GET/DELETE/MKCOL）替代真实坚果云；
- 两台设备 A/B：独立 NotesPath + 独立 AppSettings（内存）+ 同一桩地址；
- 测试前备份/恢复真实 `settings.json`（SyncEngine 内部会调 AppSettings.Save）。

## 归档内容

| 文件 | 说明 |
|---|---|
| `Program.cs` | 测试代码本体（单测 + 桶拆分 + 首配目录缺失 + 双设备 + 断网自愈，42 断言） |
| `fc-sync-test.csproj` | 项目文件，`ProjectReference` 指向主项目 FocusCapture.csproj |

**不含** `bin/`、`obj/`（构建产物，可随时由 `dotnet run` 重新生成，原目录 ~275MB 已随删除释放）。

## 为什么归档在这里而不是仓库根/主项目目录

1. 原 csproj 注释的踩坑：测试程序**不能放主项目子目录**——WPF 的 `**/*.cs` glob 会吞掉 Program.cs 导致 `wpmpftmp CS0579` 重复定义。本目录已通过主 csproj 的 `DefaultItemExcludes` 排除（见 `FocusCapture.csproj` 中 `fc-sync-test-archive` 相关配置），**不参与编译**。
2. 原程序刻意放在 git 仓库之外，删除即永久丢失，故归档进仓库 `docs/` 与项目代码区分。

## 如何复跑测试（恢复）

1. 把本目录整个拷回仓库外独立位置，例如 `D:\桌面文件存放位置260518\项目代码\专注力工具 - 上传git - 260726\fc-sync-test\`；
2. 检查 `fc-sync-test.csproj` 中 `<ProjectReference Include="...FocusCapture.csproj" />` 的绝对路径是否仍指向真实主项目，失效则改为当前绝对路径；
3. `dotnet run` 运行，全部 PASS 输出 `===== ALL TESTS PASSED =====`。

## 关联文档

- `docs/FocusCapture-v3-PRD.md`（QUEST-5 需求）
- `docs/PROGRESS.md`（交接状态）
