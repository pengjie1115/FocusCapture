# QUEST-5 — Phase 0：云端同步（WebDAV 自用过渡，可插拔架构）

> Codex 执行手册 | 预估 3-4 天 | 需求来源 docs/FocusCapture-v3-PRD.md v0.3 §5.0 | 全部完成后统一合并

## 0. 衔接到 QUEST-4

**前提**：QUEST-1~4（v2.0 AI 增强）已完成，开发分支 = `codex/quest1-ai-provider`（已快进合并 main 并推送双远程；批注分支 `feature/annotation-quote` 冻结不动）。你接手时：

- **存储是 MD 文件体系，不是数据库**：笔记存 `AppSettings.NotesPath`（默认 `%Documents%\FocusCapture`）下的 `.md` 文件，按标签分文件（`{Tag}.md`）或无标签按天（`灵感_{yyyy-MM-dd}.md`）
- 行格式：`- [yyyy-MM-dd HH:mm] 内容 — 来源: xxx`（`NoteService.NoteLineRegex` 解析；多段内容换行转义为 `\u23CE`）
- **MD 只增不减铁律**（v2.0 立）：编辑 = 追加【编辑】行，不覆盖原行
- `NoteService`：SaveNote / ParseNotes / DeleteNote（行级物理删除 + DeletedNoteService 软删记录）
- `DeletedNoteService`：deleted.json 软删记录（v2.0 的"软删"是删除标记记录，**不是** v3.0 的本地回收站，两者不同）
- `AppSettings`：JSON 序列化到 `%AppData%\FocusCapture\settings.json`，含 NotesPath / AiAssistantName 等
- 悬浮球右键菜单（`FloatBall.Ball_MouseRightButtonDown`）、设置窗（`SettingsWindow`）、灵感速览（`QuickViewWindow`）均已就绪
- 笔记行**没有稳定 ID**（只有 Timestamp + Content + SourceWindow + Tag）

**本阶段任务**：给客户端加"可插拔云端同步"——Phase 0 接**坚果云 WebDAV**（免费），E2EE 加密，本地回收站兜底，为将来自建服务器（Phase 1-4）留好 Provider 抽象位。**本阶段不做账号体系、不做服务器、不做任何云端 UI 之外的新窗口**。

## 1. 项目背景

**这是什么**：自用阶段的多端同步。主力电脑记灵感 → 第二台电脑配置同一坚果云 WebDAV → 拉取全部笔记。0 成本（坚果云免费）、0 服务器、0 备案、0 运维。

**为什么做**：v3.0 的两阶段路径第一步。架构按"可插拔"设计——同步引擎写死，渠道（坚果云/自建服务器）可换，将来商业化时只换 Provider 实现，全部复用。

**不是什么**：不做账号体系（注册/登录/JWT，Phase 1-4 的事）；不做自建服务器；不改捕捉/速览/语音主流程；不做手机端；不做实时推送（30 分钟轮询足够）；**不是让本地笔记存到数据库**——本地 MD 文件体系保持不动，云端只是它的镜像。

**用户画像**：开发者本人（彭杰），Windows 两台电脑，WPF .NET 8，自用工具。对数据安全敏感：**云端必须密文（E2EE）**。

**技术栈拍板**：
- 同步：`HttpClient` + WebDAV 协议（PROPFIND/PUT/GET），坚果云 `https://dav.jianguoyun.com/dav/`
- 加密：`System.Security.Cryptography` —— PBKDF2（Rfc2898DeriveBytes）派生 + AES-256-GCM 加密
- 本地存储：保持 MD 文件体系，新增 sidecar 同步状态文件（JSON）
- 依赖克制：除官方 `System.Security.Cryptography.ProtectedData`（DPAPI，Windows 原生能力，微软官方包）外不用第三方 NuGet

**开发方式**：AI 代劳，逐条执行任务清单，跑完验收标准才算完成。

## 2. 全局约束

### 必做
- **同步引擎与 UI 解耦**：同步永远在后台线程跑，不得阻塞/卡顿捕捉、速览、语音等主流程；断网时本地功能 100% 可用
- **ISyncProvider 契约**：接口方法签名固定为 `Push(SyncNote[] changes)` / `Pull(string? since)` / `Full()`（返回类型见 §7 第三步），本阶段实现 `WebDAVProvider` 一个实现即可，但契约结构必须支持将来加 `ServerProvider`
- **笔记 ID = 确定性哈希**：`SHA256(相对文件路径 + "|" + 完整行内容)` 取前 16 字节转 hex 作 ID。同一行在任何设备生成相同 ID → 天然幂等、天然去重、无需索引表。编辑追加新行 = 新 ID（与"MD 只增不减"语义天然契合，旧行在下次解析时不再存在则标记软删）
- **打包存储**：云端按桶文件存，**禁止每条笔记一个文件**。桶规则：按 `updatedAt` 的 ISO 周排序，每桶 ≤200 条，文件名 `notes-{yyyy-Www}-{seq}.json`，桶内 JSON 数组
- **桶清单与孤儿桶清理**：云端同目录维护 `sync_meta.json`（E2EE 盐 + 桶清单 + 同步游标）。push 完成后按清单 diff 删除不再存在的孤儿桶；笔记换周/换桶后旧桶必须同步删，防止已删笔记经旧桶复活
- **E2EE**：云端桶内 `content` / `tags` 必须是 AES-256-GCM 密文（Base64）；密钥来自主密码 PBKDF2 派生，**密钥永不上传**；本地 MD 明文不动（本地明文兜底是密钥重置的前提）。**盐跨设备一致**：盐由首配设备随机生成，明文存云端 `sync_meta.json`，后续设备拉盐后派生同一 DEK
- **device_id**：每台设备启动时生成并持久化（GUID），随每条笔记上传（`deviceId` 字段）；引擎必须用它区分"自己推的"与"别人推的"（回声识别）
- **同步层纯行级，不感知标记行语义**：`【编辑】`/`【AI 释义】` 标记行是独立 MD 行，同步层一律当普通行（独立 ID、独立 SyncNote）上传/拉取；合并回原笔记是展示层 `ParseNotes` 的事，靠行尾 `(ref 绝对时间戳)` 精确关联（跨设备成立，因为是绝对时间戳）。同步层不得识别标记行或跨行合并
- **频率克制**：推送 = 变更后合并 **30 秒窗口**批量发 1 次；拉取 = 启动 1 次 + 每 **30 分钟**轮询；503/网络失败指数退避 30s/2min/10min，连续 3 次失败停止自动重试，UI 显示失败原因
- **本地回收站**：删除笔记先进回收站（保留 30 天，可配置），回收站内可恢复；确认清空才物理删除并同步软删；拉取到软删标记的笔记也进本地回收站
- **构建通过**：`dotnet build -c Release` 0 警告 0 错误
- 新配置项一律进 `AppSettings`（settings.json），不硬编码

### 禁止（违反就算失败）
- **不许**云端存明文（content/tags 未加密直接上传）
- **不许**每条笔记一个云端文件（必须 200 条/桶打包）
- **不许**本地也加密（E2EE 只加密云端副本；本地 MD 必须明文——密钥重置流程依赖它）
- **不许**同步阻塞 UI / 主流程（大同步期间悬浮球、捕捉、语音必须流畅）
- **不许**不做回声识别（无 device_id 或不用它判断，A 推 B 拉会死循环）
- **不许**删除直接物理删除（必须进回收站，30 天可恢复）
- **不许**把主密码 / 恢复码 / 派生密钥写进云端或日志
- **不许**同步频率写死"每改即传"（必须 30s 合并窗口，否则撞坚果云 600 次/30 分钟限流）
- **不许**动现有 MD 文件的行格式（`ToMarkdownLine` 输出格式不得改变）
- **不许**在 main 分支上开发（本 QUEST 在新建分支进行，见 §9）

## 3. 项目目录结构（本期新增文件标 ★）

```
FocusCapture/
├── QUEST-5.md                 # 本文件
├── Models/
│   ├── NoteEntry.cs           # 不改（同步层自行解析）
│   └── SyncNote.cs            ★ 新建：同步数据模型 + 确定性 ID 生成 + 序列化
├── Services/
│   ├── Sync/
│   │   ├── ISyncProvider.cs   ★ 新建：同步契约接口
│   │   ├── SyncEngine.cs      ★ 新建：核心引擎（游标/冲突/打包拆包/回声识别/频率合并窗口/自愈重置）
│   │   ├── WebDAVProvider.cs  ★ 新建：坚果云实现（PROPFIND/PUT/GET + 503 退避）
│   │   ├── RecycleBinService.cs ★ 新建：本地回收站（30 天/恢复/清空）
│   │   └── CryptoService.cs   ★ 新建：E2EE（PBKDF2 + AES-256-GCM + 恢复码 + 密钥重置）
│   └── NoteService.cs         # 改：暴露行级读取/写入能力给 SyncEngine（新增方法，不改现有方法）
├── Windows/
│   ├── SettingsWindow.xaml(.cs) # 改：新增"云同步"设置区（WebDAV 配置/主密码/自动同步/立即同步/重置同步状态/回收站入口）
│   └── RecycleBinWindow.xaml(.cs) ★ 新建：回收站管理（列表/恢复/清空）
├── Models/
│   ├── AppSettings.cs         # 改：加 SyncSettings 子对象（Provider 配置 + deviceId + 同步游标 + e2ee 盐 + 恢复码哈希 + 待推软删清单）
│   └── AppJsonContext.cs      # 改：注册 SyncNote / SyncSettings / RecycleBinEntry / SyncNote[] / List<SyncNote>（trimmed 下不注册必崩）
└── docs/
    └── PROGRESS.md            # 改：追加 QUEST-5 进度
```

## 4. 反作弊（点名具体偷懒姿势，用了就算失败）

1. **云端存明文** — `content` 原样写进桶 JSON。
   验证方式：坚果云网页端打开桶文件，`content` 字段必须是 Base64 密文（不可读原文）；代码审查：加密调用必须在序列化前。

2. **每条笔记一个文件** — 云端出现 `灵感_2026-08-12.md` 这类"行文件"。
   验证方式：坚果云网页端目录里只能有 `notes-*.json` 桶文件（和后续迁移用导出包）；单桶条数 ≤200。

3. **本地也加密** — 把 E2EE 做成全盘加密，本地 MD 变密文。
   验证方式：`NotesPath` 下 .md 文件仍是明文可读；代码审查：加密只发生在 SyncEngine 序列化边界。

4. **不做回声识别** — 拉取回来不比对 `deviceId`，A 推→B 拉→B 又推，无限循环。
   验证方式：双设备模拟（§8）连续 3 轮同步后，云端桶文件无变化（日志确认无"重复推送"）；代码审查：引擎有"上次推送游标"对比逻辑。

5. **删除直删** — 删除笔记直接物理删 + 同步，回收站形同虚设。
   验证方式：删一条 → `NotesPath` 里该行仍在回收站区（可恢复）→ 清空回收站后才消失并同步软删；未清空前云端无软删标记。

6. **频率写死每改即传** — 无视 30s 合并窗口，保存即触发请求。
   验证方式：1 分钟内连续改 5 条笔记，日志显示只有 1 次 push 请求（合并窗口生效）。

7. **主密码/恢复码进云端或日志** — 随同步数据上传或写入日志。
   验证方式：坚果云桶文件、`%LocalAppData%\FocusCapture\`（startup-error.log 等日志）grep 无主密码/恢复码；恢复码只存本地 settings.json 的加盐哈希（非明文）。

8. **同步阻塞 UI** — 全量拉取时主界面卡死。
   验证方式：首次全量同步期间，悬浮球可拖、灵感速览可开、捕捉仍生效。

## 5. 取舍

```
数据安全（E2EE） > 同步正确性（不丢不重） > 功能可用 > UI 美观 > 性能优化
```

- 数据安全排第一：云端必须密文，为此可以牺牲"云端可检索"（不做云端搜索）、牺牲加密速度（PBKDF2 迭代 10 万次，首次配置等 1 秒可接受）
- 同步正确性第二：宁可慢、宁可多轮询，不能丢数据、不能重复数据。冲突拿不准时保留双方快照
- 功能先跑通：UI 用现有样式，不做新设计语言
- 别搞过早优化：SQLite 不上、Redis 不上、并发锁能用简单 Mutex 就不上复杂方案
- **本地明文 > 云端可读**：本地 MD 永远明文（你的数据你做主），云端永远密文（泄露不可读）

## 6. 未知处理

1. **坚果云 503 / 429** → 触发限流。指数退避 30s/2min/10min，连续 3 次失败停止自动重试，UI 显示"上次同步失败：限流"，等手动。**不要**无限重试
2. **WebDAV 401（授权码错）** → 明确提示"坚果云授权码无效，请在坚果云网页端『安全-第三方应用管理』重新生成"，配置不改，等用户修正
3. **PBKDF2 派生慢** → 迭代次数 100_000（AES-256-GCM 场景安全且 <1s）；首次配置时 UI 显示"正在生成密钥…"
4. **GCM 解密失败（密钥不对/数据损坏）** → 捕获异常，UI 提示"云端数据解密失败，可能主密码不正确"，**不崩溃**、不改动本地
5. **桶文件损坏/JSON 解析失败** → 跳过该桶并记日志，继续其他桶；不中断全量同步
6. **NotesPath 不存在或为空** → 首次同步按空库处理，正常创建桶
7. **一个任务卡住超过 30 分钟** → 写 BLOCKED.md（卡在哪里、试了什么、什么错误），**跳过做下一个**

## 7. 任务清单

### 第一步：同步数据模型与确定性 ID

> 操作位置：`Models/SyncNote.cs`（新建）

1. 定义同步模型（与云端桶 JSON 字段完全一致，这是"第二层契约"）：
   ```csharp
   public class SyncNote
   {
       public int SchemaVersion { get; set; } = 1;   // 格式演进预留
       public string Id { get; set; } = "";           // 确定性哈希
       public string Content { get; set; } = "";      // 云端=密文；本地=明文
       public string[] Tags { get; set; } = [];       // 云端=密文数组；本地=明文
       public string CreatedAt { get; set; } = "";    // ISO 8601 UTC，明文（不敏感）
       public string UpdatedAt { get; set; } = "";    // ISO 8601 UTC，明文（对账需要）
       public bool Deleted { get; set; }
       public string DeviceId { get; set; } = "";     // 最后修改设备
       public string? PrevContent { get; set; }       // 冲突被覆盖方快照（本地留存）
   }
   ```
2. 确定性 ID 生成（静态方法）：`SHA256($"{相对路径}|{完整行内容}")` 前 16 字节 hex。相对路径 = 相对 `NotesPath` 的路径（如 `灵感_2026-08-12.md`），保证两台电脑路径一致 → ID 一致
3. 序列化：`ToBucketJson()`（密文后字段序列化）/ `FromBucketJson()`，桶 JSON 结构 `{ "bucket": "notes-2026-W33-1", "notes": [SyncNote...] }`。**命名策略统一 camelCase**（对齐 PRD §5.0.3 示例）：用 `JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }`，WebDAV 与将来 Server 两端共用同一份 options——这是"格式统一"的落地。**必须把 SyncNote / SyncNote[] / SyncSettings / RecycleBinEntry 等新类型注册进 `AppJsonContext`（`[JsonSerializable]`）**，本项目 trimmed + 源生成，漏注册运行时抛 `NotSupportedException`
4. 特别注意：**Tags 从相对路径解析，不从行内解析**——现有 `NoteEntry.Tag` 由文件名承载（`{Tag}.md`，灵感文件无标签），行内 `#tag` 在 `SaveNote` 时已被剥离进文件名。同步层规则：`灵感_*.md` → `Tags=[]`，其余 `{文件名}.md` → `Tags=[文件名]`。本步只做模型与 ID 生成，不做全量解析（第二步接）

### 第二步：行级读取/写入能力（NoteService 扩展）

> 操作位置：`Services/NoteService.cs`（改，新增方法，不改现有方法）

1. 新增 `List<(string relativePath, string line, NoteEntry entry)> ReadAllLines()`：遍历 `NotesPath` 下全部 `.md`，用现有 `NoteLineRegex` 解析每行，返回 相对路径 + 原始行 + 解析结果
2. 新增 `void AppendLine(string relativePath, string line)`：向指定文件追加一行（目录不存在自动创建）；**格式严格用 `ToMarkdownLine()` 输出**（含 `\u23CE` 转义，保证单行）
3. 新增 `void RemoveLines(string relativePath, HashSet<string> lineContents)`：从文件移除指定行（供"清空回收站后同步软删"与"冲突替换"用）
4. 特别注意：所有新增方法**不改动**现有 SaveNote/ParseNotes/DeleteNote 行为；删除仍走现有流程进回收站（第五步接）
5. **时间戳映射（UTC 规则）**：MD 行时间 `yyyy-MM-dd HH:mm` 是本地时间、分钟精度、无 Kind。转 `CreatedAt/UpdatedAt`（ISO 8601 UTC）时用 `DateTime.SpecifyKind(ts, DateTimeKind.Local).ToUniversalTime()`，禁止直接 `.ToString()` 或当 UTC 用（DateTime vs DateTimeOffset 的 Kind 坑）。分钟精度下增量过滤用 `UpdatedAt >= since`（含边界），避免同分钟双端变更漏一轮

### 第三步：ISyncProvider 契约

> 操作位置：`Services/Sync/ISyncProvider.cs`（新建）

```csharp
public interface ISyncProvider
{
    string Name { get; }                          // "WebDAV" / "Server"
    SyncLimits Limits { get; }                    // 频率/容量上报（渠道属性，参数化核心）
    Task<SyncPullResult> PullAsync(string? since, CancellationToken ct);  // 增量拉取
    Task<SyncPushResult> PushAsync(IReadOnlyList<SyncNote> changes, string? lastCursor, CancellationToken ct); // 批量推送
    Task<List<SyncNote>> FullAsync(CancellationToken ct);                  // 全量导出（新设备/重置）
}

public record SyncLimits(int MinIntervalSeconds = 30, int MaxBatchSize = 200, int MaxRequestsPerWindow = 500);
// WebDAV 上报 30/200/500；将来 Server 上报 3/500/10000 —— 引擎按 Limits 自动选合并窗口与批大小

public record SyncPullResult(List<SyncNote> Notes, string? NewSince);
public record SyncPushResult(string? NewSince);
```

- `since` = 上次同步游标（云端最新 updatedAt，UTC 字符串）
- **注意**：WebDAV 无服务端游标，`PullAsync` 的 `since` 语义 = 只拉取桶内 `UpdatedAt > since` 的笔记（客户端过滤）；`NewSince` 返回当前扫描到的最新时间

### 第四步：E2EE 加密模块

> 操作位置：`Services/Sync/CryptoService.cs`（新建）

1. **密钥派生**：主密码 + 随机盐（16 字节，`RandomNumberGenerator`）→ PBKDF2（SHA-256，100_000 次迭代）→ 32 字节 DEK
2. **加解密**：AES-256-GCM，`Encrypt(byte[] dek, string plaintext) → {nonce(12) + ciphertext+tag}` Base64 单串；`Decrypt(byte[] dek, string encrypted) → string`。非ce 每次随机
3. **盐与恢复码存储**：盐由**首配设备**随机生成（16 字节，`RandomNumberGenerator`），**明文写云端 `sync_meta.json`**（盐不需保密，明文上传不泄露）；后续设备首次配置先拉取 `sync_meta.json` 的盐再派生同一 DEK——**保证跨设备同主密码 → 同 DEK**。本地也缓存盐到 `AppSettings.SyncSettings.E2eeSalt`（离线可派生）。**恢复码 = 10 位随机数字**（`RandomNumberGenerator.GetInt32` 逐位拼接），加本地随机盐后 SHA-256 哈希存 `AppSettings.SyncSettings.RecoveryCodeHash`（加盐防暴力枚举，盐同存该对象）；主密码本身**任何位置都不存**
4. **密钥重置流程**（用户忘主密码的兜底）：
   - 检测到"主密码验证失败"（解密第一条笔记报错）→ UI 引导：输入新主密码 → 生成新盐 → 重新派生 DEK → 用本地明文笔记重新加密 → 全量重新上传（覆盖云端桶）→ 恢复码重置
   - 恢复码重置：设置页"忘记主密码"→ 输入恢复码（校验哈希）+ 新主密码 → 走上述重置流程
5. 加密边界：只加密 `SyncNote.Content` / `SyncNote.Tags[]`；`Id/SchemaVersion/CreatedAt/UpdatedAt/Deleted/DeviceId` 全部明文（引擎对账需要，均不敏感）
6. 特别注意：首次配置主密码的引导 UI 在第七步做，本步只提供纯函数服务

### 第五步：本地回收站

> 操作位置：`Services/Sync/RecycleBinService.cs`（新建）+ `Windows/RecycleBinWindow.xaml(.cs)`（新建）

1. **存储**：回收站区 = `NotesPath` 下 `.recycle_bin/` 目录，删除的笔记行以 `recycle-{timestamp}.json` 记录 `{relativePath, line, deletedAt, expiresAt}`；30 天过期自动清理（启动时扫描）
2. **流程**：
   - 删除（替换现有 `DeleteNote` 的物理删除路径）：从原文件移除行 → 写入回收站记录 → **不**同步软删
   - 恢复：从回收站记录取回 → 写回原文件（追加到末尾，保持 ID 不变）→ 删除回收站记录
   - 清空（设置页/回收站窗按钮，二次确认）：物理删除回收站文件 → 对该笔记触发同步软删（`Deleted=true` 上传）
   - 拉取到软删笔记：本地对应行移入回收站（可恢复），**不**物理删除
3. `RecycleBinWindow`：列表（内容预览 + 过期时间）+ 恢复按钮 + 清空按钮（二次确认）；入口放设置页云同步区
4. 特别注意：现有 `DeletedNoteService`（deleted.json）保留不动，两套机制并存——v2.0 软删记录管 UI 展示过滤，回收站管 30 天恢复。**但删除路径只能走一条**：改造 `DeleteNote` 后，删行改由回收站接管（从原文件移除行 → 写回收站记录），**不再调用 `_deletedService.MarkDeleted`**；恢复时仅从回收站取回写回文件，不碰 deleted.json。避免"删一条同时进回收站 + 记软删，恢复后仍被 `IsDeleted` 过滤看不到"的双轨冲突

### 第六步：SyncEngine 核心

> 操作位置：`Services/Sync/SyncEngine.cs`（新建）

1. **状态**（`AppSettings.SyncSettings`）：`DeviceId`（GUID，无则生成）、`ProviderName`、`WebDavUrl`、`WebDavUser`、`WebDavToken`（坚果云授权码，**DPAPI 加密后**存，用官方 `System.Security.Cryptography.ProtectedData`；解密失败兜底为空并提示重新配置，不崩）、`LastCursor`、`AutoSyncEnabled`、`E2eeSalt`、`RecoveryCodeHash`、`PendingDeletes`（待推软删清单：`List<SyncNote>`，清空回收站时压入 `Deleted=true` 的笔记，推送成功后清空）、`LastSyncResult`、`LastSyncAt`
2. **回声识别**：push 前比对——只推送本机修改过（`DeviceId == 本机`）且 `UpdatedAt > LastCursor` 的笔记；拉取时跳过 `DeviceId == 本机` 的笔记（自己推的不再拉回），其余按 last-write-wins 合并
3. **打包/拆包**：push 流程 = **先 `PullAsync` 拉取云端当前桶（含 `sync_meta.json`）→ 按笔记 ID 合并本机变更（本机变更 wins）→ 重组桶 → 整桶 PUT 覆盖**；严禁未经合并直接用本机数据 PUT（B 空库首推会覆盖 A 数据）。pull 时 GET `sync_meta.json` + 全部桶，过滤 `UpdatedAt >= since`，按 ID 与本地对账。push 完成后按桶清单 diff 删除孤儿桶（笔记换周/换桶后旧桶同步删，防已删笔记经旧桶复活）
4. **冲突（确定性 ID 下的真实场景）**：ID=内容哈希，故"同 ID 双端都改"不会发生（改了内容 ID 就变）。真实冲突只有两类：① **删除 vs 编辑**——A 软删 ID-X，B 编辑 ID-X 追加新行 ID-Y，按 `UpdatedAt` 裁决：删晚则软删胜，改晚则保留新行；② **同 ID 状态分歧**（极罕见）——同 ID 双端一个 `Deleted=true` 一个 `false`，`UpdatedAt` 大者胜。被覆盖方内容/状态存本地 `PrevContent`（不丢数据），日志记录冲突。**不要实现"同 ID 不同内容"的合并逻辑——那是死代码**
5. **频率合并窗口**：本机变更收集 30 秒（Timer 或延迟任务），窗口结束批量 push 一次；`Limits.MinIntervalSeconds` 来自 Provider
6. **自愈**：`ResetSync()` 方法——清空 LastCursor + 删除云端所有桶（二次确认）→ 触发全量重新上传；拉取侧自愈 = 清 LastCursor → 全量拉取对账
7. **失败重试**：指数退避 30s/2min/10min；连续 3 次失败置 `LastSyncResult = "失败: {原因}"`，停止自动重试，等手动"立即同步"
8. 线程：`Task.Run` 后台执行 + `SemaphoreSlim(1)` 防并发；所有 UI 更新走 `Dispatcher`

### 第七步：WebDAV Provider

> 操作位置：`Services/Sync/WebDAVProvider.cs`（新建）

1. **Base URL**：`https://dav.jianguoyun.com/dav/FocusCapture/`（桶文件放此目录）；`WebDAVToken` = 坚果云"第三方应用管理"生成的应用密码，Basic Auth（`Authorization: Basic base64(user:token)`）
2. **操作映射**：
   - `PullAsync` → `GET sync_meta.json`（盐 + 桶清单 + 游标）→ `PROPFIND`（Depth:1 列目录）→ 对每个 `notes-*.json` 桶 `GET` → 解析 → 过滤增量
   - `PushAsync` → 变更按桶重组 → 每个桶 `PUT`（整桶覆盖，桶内 = 该桶全部当前笔记）→ 更新 `sync_meta.json` → 按桶清单 diff `DELETE` 孤儿桶
   - `FullAsync` → 同 PullAsync 不过滤
   - 删除桶文件 → `DELETE`（清空云端时）
3. **限流适配**：请求间最小间隔 = `Limits.MinIntervalSeconds`（30s 合并窗口天然控制频率）；遇 503 抛限流异常（SyncEngine 走退避）
4. **幂等**：PUT 整桶覆盖是天然幂等——重复推送同一桶结果一致
5. 特别注意：请求失败必须区分 401（授权码错）/ 503（限流）/ 网络错误（可重试），向上层传明确信息

### 第八步：设置 UI 与联调

> 操作位置：`Windows/SettingsWindow.xaml(.cs)`（改）

1. 新增"云同步"设置区：
   - WebDAV 配置：服务器地址（默认 `https://dav.jianguoyun.com/dav/FocusCapture/`）、坚果云账号、授权码（PasswordBox）——"保存并连接"按钮做连通性测试；连接成功后**自动拉取 `sync_meta.json`（若无则本机为首配设备：生成盐并上传）+ 立即做一次全量同步 + 提示开启自动同步**（避免配完不拉一次，用户误以为没生效）
   - E2EE：首次配置引导（输入主密码 ×2，**强度校验：≥8 位且含字母 + 数字** + 生成并展示恢复码，提示"抄下来，别跟主密码放一起"）；已配置显示"已启用"+"重置主密码"入口
   - 同步控制：自动同步开关（默认关）、"立即同步"按钮 + 上次同步时间/结果、"重置同步状态"按钮（二次确认）
   - 回收站入口按钮
2. 同步状态显示：悬浮球右键菜单加一项"同步状态"（只读文本：上次同步时间/结果/是否自动）——或退而求其次放设置页内，**最佳努力，不阻塞**
3. 启动时：`AutoSyncEnabled` 时启动 30 分钟轮询 Timer；首次同步提示（无主密码 → 引导设置）
4. 联调（本机自测，见 §8）

## 8. 验收标准

> 按顺序执行。单机 Codex 环境用"双设备模拟"：本地建两个测试 NotesPath 目录（`NotesPath` 切到 `%AppData%\FocusCapture\notes_testA\` 与 `notes_testB\`，`deviceId` 各自独立），轮流以 A/B 身份跑同一份 SyncEngine。真实双机验收留给用户（见末尾清单）。

### A. 构建与静态检查

```bash
dotnet build -c Release
# 期望: 0 警告 0 错误
```

代码审查（人工 + grep）：
- `grep -r "dav.jianguoyun" Services/Sync/` → WebDAVProvider 独有，SyncEngine 无渠道引用
- `grep -rn "Encrypt\|Decrypt" Services/Sync/CryptoService.cs` → 加解密只在 CryptoService；SyncEngine 序列化边界调用
- `grep -rn "deviceId\|DeviceId" Services/Sync/SyncEngine.cs` → 回声识别逻辑存在
- settings.json 序列化字段检查：WebDavToken 为 DPAPI 密文，RecoveryCodeHash 为哈希，无明文主密码

### B. E2EE 正确性（先做，依赖它跑后续）

```bash
# 用坚果云网页端或 WebDAV GET 检查云端桶文件
# 期望: content 字段为 Base64 密文（非原文明文）；tags 同理；Id/UpdatedAt/Deleted 为明文
# 期望: 本地 NotesPath .md 文件仍为明文可读
# 解密自测: CryptoService.Decrypt(dek, 密文) == 原文（单测覆盖，见下）
```

### C. 双设备模拟同步（核心验收，A/B 两目录各存 3 条不同笔记）

1. A 首次同步（配置主密码 + WebDAV）→ 云端出现桶文件，A 的 3 条全部在桶内
   - 期望：桶文件 `notes-*.json` 存在，≤200 条/桶
2. B 首次同步（同一主密码 + 同一 WebDAV）→ B 拉取到 A 的 3 条
   - 期望：B 的 NotesPath 出现对应 .md 行，内容一致
3. A 新增 1 条 → B 同步 → B 多出 1 条；B 编辑 1 条（追加行）→ A 同步 → A 多出 1 条（编辑行）
   - 期望：双向增量收敛，无重复行（回声识别生效：连续 3 轮同步后云端桶无变化）
4. A 删除 1 条 → B 同步
   - 期望：B 对应行进回收站（可恢复），未清空前云端无软删标记
5. A 清空回收站 → B 同步
   - 期望：B 该行不可见（软删传播）；云端桶内该笔记 `Deleted=true`
6. 断网测试：关网（或改错 URL）→ 本地功能正常 → 恢复后同步自动补齐
   - 期望：UI 不卡、不崩；恢复后数据一致
7. 失败重试：连续 3 次断网同步 → 自动重试停止，UI 显示失败原因 → 手动"立即同步"恢复

### D. 密钥重置流程

1. 故意输错主密码触发解密失败 → UI 提示"云端数据无法解密"
2. 走"新主密码重置"：输入新主密码 → 重新加密本地明文 → 全量重新上传
3. 期望：上传后 B 用**新主密码**能解密拉取；云端桶全部为新密文
4. 恢复码路径：再重置一次，用恢复码 + 新主密码完成
   - 期望：恢复码校验通过（哈希比对），流程走通

### E. 自愈与回收站 UI

1. 手动删除本地 `LastCursor`（模拟游标丢失）→ 设置页"重置同步状态"→ 全量重新对齐
   - 期望：数据一致，无重复无丢失
2. 回收站窗：删除一条 → 回收站列表可见 → 恢复 → 原文件行回来（ID 不变）→ 再次删除 → 清空（二次确认生效）
   - 期望：每一步 UI 反馈正确，二次确认弹窗存在

### F. 单测（写入项目测试或独立脚本）

- 确定性 ID：同一行两次生成 ID 相同；不同行不同
- 桶拆分：201 条 → 2 桶（200+1）；桶文件名序列正确
- 加密往返：Encrypt→Decrypt == 原文；盐不同 → 密文不同；解密失败（错 DEK）抛异常
- 冲突：同 ID，updatedAt 大者胜，被覆盖方进 PrevContent
- 回声：模拟 A 推送 → B 拉取 → B 不再推送（引擎层断言）

### G. 用户真实双机验收（交付后由用户执行）

- 主力电脑：配置坚果云 + 主密码 → 自动同步开 → 记几条
- 第二台电脑：同坚果云 + 同主密码 → 拉取 → 一致
- 第二台改 → 主力同步回 → 一致；其中一台断网，另一台不受影响

## 9. 交付

完成后执行：

```bash
git checkout -b codex/quest-v3-sync   # 从当前分支新建 v3.0 开发分支（如已在则跳过）
git add -A
git commit -m "quest 5: Phase 0 云端同步（WebDAV + E2EE + 回收站 + 可插拔架构）"
```

**不要合并到 main，不要删分支。** 等人工验收通过后再问用户是否快进合并 + 推送双远程。

---

**提醒**：本 QUEST 涉及数据安全核心，任何"先跑通再补加密"的冲动都是失败——加密是第一步验收（§8-B），不是最后补丁。
