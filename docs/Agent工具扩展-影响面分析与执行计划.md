# Agent 工具扩展 — 影响面分析与执行计划

> 日期：2026-09-17
> 范围：第一批（#1-#6、#10、#12）+ 第二批（#7、#8）+ PDF（#11）+ 时间上下文注入（A/C）+ 输入框支持 PDF
> 状态：**开工前的分析文档。已按用户要求完成调研与实测，待确认 4 项决策后开分支写码。**
> 上游依据：`docs/Agent工具盘点与新增建议.md`（工具清单与边界）

---

## 一、一句话结论

| 项 | 结论 |
|---|---|
| 可做 | 11 个工具 + 时间注入 + 输入框 PDF，**改动集中在 8 个文件 + 3 个新文件**，无架构级改造 |
| 做不了 | **`read_cloud_image`（#13）有架构障碍** —— tool 消息在协议层只能带文本，工具无法把图片交给模型看（详见 §四） |
| 实测已验证 | PDF 引库（PdfPig）**restore + 编译 + 抽取全通**（证据见 §三），不再是"未验证" |
| 仍有未知 | **中文 PDF 的抽取质量**（需用真实文件测，见 §五 Q2） |

---

## 二、改动清单（逐项落文件）

### 2.1 新增文件（3 个）

| 文件 | 内容 | 说明 |
|---|---|---|
| `Services/Files/DocumentTextExtractor.cs` | 统一文档抽文本：docx / xlsx / pdf / 纯文本 | **关键复用点**：让「对话附件」与「网盘文件」两条路径共用同一套抽取器，从根上消除"同一文件两条路能力不一致" |
| `Services/Files/XlsxTextExtractor.cs` | xlsx 零依赖解析（zip + XML） | 需处理：共享字符串表、内联字符串、**日期序列号**、多 sheet、合并单元格 |
| `Services/Agent/DocumentTools.cs` | `read_spreadsheet`、`read_pdf` | 面向 Agent 的两个文档工具 |

### 2.2 修改文件（8 个）

| 文件 | 改动 | 风险 |
|---|---|---|
| `Services/Agent/LocalTools.cs` | 加 `update_note`（含改提醒时间）、`list_notes_by_date`、`note_stats` | 中：`update_note` 的语义见 §五 Q1 |
| `Services/Agent/FileTools.cs` | 加 `update_file_meta`；`read_cloud_file` 支持 docx/xlsx/pdf | 低 |
| `Services/Agent/RecycleBinTools.cs`（新） | `list_recycle_bin`、`restore_deleted`（复用 `NoteService.RecycleBin`，已是 public） | 低 |
| `Services/Agent/ExportTools.cs`（新） | `export_notes` | 中：落点见 §五 Q4 |
| `Services/AI/ChatAttachmentService.cs` | ① `DocumentExts` 增加 `.pdf`（及 `.xlsx`，见 Q3）② 删掉第 117-118 行的 `.pdf` 拒绝分支 ③ 抽取逻辑改为调用 `DocumentTextExtractor` ④ `FileDialogFilter` 加 `*.pdf` | 低（三个入口都汇入 `AddFilesAsync`，改一处即全生效） |
| `Services/Files/FileRepository.cs` | `ReadTextAsync` 白名单之外，docx/xlsx/pdf 走抽取器 | 中：这是网盘读取的唯一出口，改错影响面大 |
| `Windows/AIDialogWindow.xaml.cs` | ① `ExtraSystemContext` 追加当前时间（A）② `EnsureAgentRegistry` 注册新工具 | 低（各一行级） |
| `Services/AI/PromptBuilder.cs` | `BuildTimeParseMessages` 补当前日期（C） | 低，但它在待办时间识别的兜底路径上，需回归验证 |

### 2.3 依赖与配套（3 个）

| 项 | 内容 |
|---|---|
| `FocusCapture.csproj` | 加 `PdfPig` 包引用（**待 Q2 确认**） |
| `tests/sync/Program.cs` | 新增「Agent 工具组」检查点（照 `TestDragDropSave` 的写法） |
| `REGRESSION.md` / `BRANCHES.md` | 同步登记（项目既有纪律） |

### 2.4 明确不改的

- **框架层红线**：`Services/Agent/` 不得引用 `Services/Destinations/`（新工具也不碰目的地）
- **无路径参数**：所有新工具继续只用 `ref_time` / `file_id` / `handle` / `content` 定位
- **破坏性动作**：不加"彻底删除 / 清空回收站"
- **`OpenAICompatibleProvider`**：**不动**。PDF 走文档文本路径，不需要碰请求序列化（这正是选它作为方案的原因）

---

## 三、已完成的实测（硬证据，不是推断）

对 PDF 方案做了一次独立探针实验（临时工程，不在仓库内）：

| 步骤 | 命令 | 结果 |
|---|---|---|
| 还原包 | `dotnet restore` | ✅ `已还原 ... (用时 7.41 秒)`，退出码 0 |
| 编译运行 | `dotnet run` | ✅ 退出码 0 |
| 抽取验证 | PdfPig 打开一个手写的最小合法 PDF | ✅ `页数: 1`、`提取文本长度: 33`、正确取出 `Hello PdfProbe extraction works` |

**推论（判断）**：PDF 引入 NuGet 这条路是通的；PdfPig 的 API 与抽取管道在 .NET 8 下工作正常。**但中文 PDF 的抽取质量仍未验证** —— 需要拿真实 PDF 测（见 Q2）。

---

## 四、发现一个架构障碍：`read_cloud_image`（#13）做不了

这是本次调研**最重要的发现**，必须让用户知道，因为它推翻了原清单里的一条。

| 事实 | 证据 |
|---|---|
| 图片进模型，靠的是消息上的附件（`m.Attachments`）走多模态部件 | `OpenAICompatibleProvider.BuildMessagesArray:348-351` |
| 但 `Tool` 角色的分支**排在附件分支之前**，且只写纯文本 content | 同文件 `:343-347` |
| 因此**工具返回值（tool 消息）不可能携带图片** —— 模型看不到 | 同上 |

**结论**：Agent 工具想"把网盘图片交给模型看"，在现有协议与代码结构下无解。

**可选出路**：

| 方案 | 做法 | 我的评价 |
|---|---|---|
| A. 放弃 `read_cloud_image` | 只保留"取回 + 给卡片"，用户自己打开看 | ✅ **推荐**。零风险，且 `fetch_cloud_file` 已经能覆盖"我要看这张图" |
| B. 改协议层 | 让 Tool 消息也支持多模态部件 | ❌ 部分供应商端点会拒；属改唯一序列化入口，风险与收益不成比例 |
| C. 取回后由工具提示用户手动拖进输入框 | 等于人工一步 | △ 可用但别扭，机制上没解决问题 |

---

## 五、待确认的 4 项（必须你拍板，我不猜）

### Q1. `update_note` 改笔记正文，用哪种语义？

**背景（事实）**：这个应用的设计是「**MD 只增不减**」——
- 普通笔记的"编辑"，实际上是**追加一条 `【编辑】` 新行**（原行不动，展示层合并为子条目）：`NoteService.AppendEdit:169-194`
- 只有**待办**是原地改行（`UpdateTodo`，MD 只增不减的**唯一例外**）：`NoteService.cs:335-345`
- 原因是行身份 = `SHA256(完整行文本)`，**改行文本 = 删旧行 + 加新行**，跨端会牵动同步墓碑机制

| 选项 | 含义 | 风险 |
|---|---|---|
| **A（推荐）** | 与界面行为完全一致：笔记 → 追加 `【编辑】` 行；待办 → 原地改行 | 无新增风险，用户看到的与手点一致 |
| B | 笔记也原地改行 | ⚠️ 突破"只增不减"红线，可能产生跨端幽灵行 |

### Q2. PDF 方案怎么定？

| 选项 | 含义 |
|---|---|
| **A（推荐）** | 引 PdfPig（Apache-2.0 纯托管）；**扫描件/图片版 PDF 明确提示"读不了"**，不做 OCR |
| B | 不引任何第三方库 → **PDF 功能整体不做**（输入框支持 PDF 也一并取消） |
| C | 引库，且同步评估 OCR 方案（成本高，需另立一项） |

补充两点已知边界：
1. **扫描件读不出文字是必然的**（PDF 里没有文字层），这不是库的问题。届时会给用户一句明确的话，而不是丢一堆乱码。
2. **文件大小上限**：现在是文档 20MB（`MaxDocumentSourceBytes`），PDF 常超。我倾向**保持 20MB**并让提示说清；要不要为 PDF 单独放宽（如 50MB），请一并定。

### Q3. 既然做了表格解析，输入框要不要一并支持 xlsx 附件？

现状：`DocumentExts` 有 `.csv` 但**没有 `.xlsx`**（表格类只有 csv 能贴进对话）。

| 选项 | 含义 |
|---|---|
| **A（推荐）** | 一并支持 `.xlsx`（复用同一解析器，几乎零额外成本，保证"贴进来"和"从网盘调"能力一致） |
| B | 只做网盘侧 `read_spreadsheet`，输入框不动 |

### Q4. `export_notes` 导出到哪？

| 选项 | 含义 |
|---|---|
| **A（推荐）** | 存进**本机文件区**（走 `FileRepository` + `FileDeliveryHub`），对话里给卡片；会**排队上传网盘** |
| B | 只写到本机导出目录，**不上云**（更保守，但用户得自己去文件夹找） |

---

## 六、执行计划（确认后照此开工）

| 阶段 | 内容 |
|---|---|
| 0 | 从 main 开分支 `feature/agent-tools-batch1`（按项目纪律先登记 `BRANCHES.md`） |
| 1 | 时间注入 A + C（最小改动，先落地，独立可验证） |
| 2 | `DocumentTextExtractor` + `XlsxTextExtractor`（先把解析基座做好，PDF 与 xlsx 都依赖它） |
| 3 | 输入框支持 PDF（+xlsx，按 Q3） |
| 4 | 11 个 Agent 工具 + 注册 |
| 5 | 慢层「Agent 工具组」检查点 + 跑全量（快层 21 条 + 慢层 212+ 条） |
| 6 | 更新 `REGRESSION.md` / `BRANCHES.md` 后交付验收 |

**验收口径（现象级）**：
1. 问"今天几号"→ 答对；**隔天同一会话再问 → 跟着变**
2. 拖一个 PDF 进输入框 → 能加上卡片，AI 能说出文件里的内容
3. 说"把那条待办改成明早八点"→ 改成功且界面能看到
4. 说"这个月记了多少条"→ 数字与日历对得上
5. 说"读一下网盘里那个 xlsx"→ AI 能报出表头和前几行

---

## 附：调研中已核实的代码位置（免得重复找）

| 事项 | 位置 |
|---|---|
| 现有工具注册点 | `AIDialogWindow.xaml.cs:1918-1944` |
| 每轮注入上下文的现成入口 | `AIDialogWindow.xaml.cs:1506-1510`（`ExtraSystemContext`）→ `AgentRunService.BuildTurnMessages:164-171` |
| 附件三入口（对话框/拖放/粘贴） | `AIDialogWindow.xaml.cs:731` / `1095-1108` / `520`，全部汇入 `AddFilesAsync:1037-1070` |
| 文档抽取现状 | `ChatAttachmentService.ExtractDocumentText:392-450`（private，需上提为公共） |
| docx 抽取实现 | `ChatAttachmentService.ExtractDocx:404-431`（零依赖，可复用） |
| 视觉能力开关 | `_settings.AiVisionEnabled` + `EnsureVisionAllowed:1110-1123` |
| 回收站服务（已 public） | `NoteService.RecycleBin:43` → `RecycleBinService.List:110` / `RestoreBatch:145` |
| 导出服务 | `NoteExportService.BuildExport:9` / `BuildWord:21` |
| 元数据改名（只改本机，不动云端） | `FileRepository.UpdateMetadata:671-685` |
| 慢层检查点写法参考 | `tests/sync/Program.cs:94`（`TestDragDropSave`） |
