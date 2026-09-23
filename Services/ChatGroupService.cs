namespace FocusCapture.Services;

/// <summary>
/// 分组业务操作的**唯一入口**（2026-09-23 新建）。
///
/// 为什么必须收敛到一个类：原先「新建分组」有两个入口各自实现，撞名行为还不一致 ——
/// ChatGroupsWindow 弹框拒绝、HistoryDrawer 静默复用已有分组（用户以为新建了，实际进了老分组，全程无提示）。
/// 现在查重策略一处定义、落盘与变更通知一处负责、删除顺序一处保证。
///
/// 与 ChatGroupStore 的分工：Store 只管「清单读写 + 锁 + 通知」，
/// 本类管「业务语义」（查重、删组时把会话放回未分组、悬空引用自愈）。
/// </summary>
public static class ChatGroupService
{
    /// <summary>分组指令长度上限。超长截断（防止用户把整篇文档贴进去撑爆每次请求的上下文）。</summary>
    public const int MaxInstructionChars = 2000;

    /// <summary>新建分组的结果</summary>
    public enum CreateResult
    {
        /// <summary>新建成功</summary>
        Created,
        /// <summary>名字为空</summary>
        EmptyName,
        /// <summary>已存在同名分组 —— **拒绝**，不静默复用</summary>
        NameExists,
    }

    /// <summary>
    /// 新建分组。成功返回新分组对象，失败返回 null 并给出原因。
    ///
    /// 撞名策略（2026-09-23 定）：**拒绝并提示**，不做静默复用 ——
    /// 静默复用会让用户以为建了新组、实际把会话塞进了老组，而且没有任何提示，是纯粹的陷阱。
    /// </summary>
    public static ChatGroup? Create(string name, out CreateResult result)
    {
        name = (name ?? "").Trim();
        if (name.Length == 0) { result = CreateResult.EmptyName; return null; }

        ChatGroup? created = null;
        var duplicated = false;

        ChatGroupStore.Mutate(groups =>
        {
            if (groups.Any(g => string.Equals(g.Name, name, StringComparison.Ordinal)))
            {
                duplicated = true;
                return false;
            }
            created = new ChatGroup
            {
                Id = Guid.NewGuid().ToString(),
                Name = name,
                CreatedAt = DateTime.Now,
                NameUpdatedAt = DateTime.Now,   // 跨端 LWW 判方向
            };
            groups.Add(created);
            return true;
        });

        result = duplicated ? CreateResult.NameExists : CreateResult.Created;
        return duplicated ? null : created;
    }

    /// <summary>
    /// 重命名分组。收藏保留分区不可改名。成功 / 无变化返回 true，失败返回 false 并给出原因。
    /// </summary>
    public static bool Rename(string id, string newName, out string error)
    {
        newName = (newName ?? "").Trim();
        if (newName.Length == 0) { error = "分组名不能为空"; return false; }
        if (ChatGroupStore.IsFavorite(id)) { error = "收藏分区不能重命名"; return false; }

        var ok = false;
        string? failure = null;

        ChatGroupStore.Mutate(groups =>
        {
            var target = groups.FirstOrDefault(g => g.Id == id);
            if (target == null)
            {
                failure = "分组不存在（可能已在另一台设备上删除）";
                return false;
            }
            if (string.Equals(target.Name, newName, StringComparison.Ordinal))
            {
                ok = true;          // 名字没变，不算失败也无需落盘
                return false;
            }
            if (groups.Any(g => g.Id != id && string.Equals(g.Name, newName, StringComparison.Ordinal)))
            {
                failure = "已存在同名分组";
                return false;
            }
            target.Name = newName;
            target.NameUpdatedAt = DateTime.Now;   // 跨端 LWW 判方向：没有它，改名会在另一台设备上被回滚
            ok = true;
            return true;
        });

        error = failure ?? "";
        return ok;
    }

    /// <summary>
    /// 删除分组：**先把组内会话放回未分组，再删分组记录** —— 顺序不可颠倒。
    /// 旧实现是先删记录、再逐个改会话（ChatGroupsWindow.cs:139-154），中途失败会留下
    /// 「分组没了但会话还挂着失效 GroupId」的状态，界面上表现为冒出一个「（未知分组）」幽灵分区。
    /// 收藏保留分区不可删除。
    /// </summary>
    public static bool DeleteGroup(string id, out string error)
    {
        if (ChatGroupStore.IsFavorite(id)) { error = "收藏分区不能删除"; return false; }

        // ① 先清会话引用：指向该分组的会话全部回到未分组。
        //    逐个 Load→改→Save：Save 会自增 Rev 并触发 SessionChanged，删除结果随同步管道传到他端。
        var moved = 0;
        try
        {
            var dir = FocusCapturePaths.Combine("chat_history");
            if (Directory.Exists(dir))
            {
                foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
                {
                    try
                    {
                        var svc = ChatSessionService.Load(file);
                        if (svc == null || !string.Equals(svc.GroupId, id, StringComparison.Ordinal)) continue;
                        svc.GroupId = "";
                        svc.Save();
                        moved++;
                    }
                    catch (Exception ex)
                    {
                        // 单个会话文件失败不影响其余 —— 剩下的靠 RepairDanglingGroupIds 兜底
                        Debug.WriteLine($"[FocusCapture] 分组删除时重置会话分组失败: {file}: {ex.Message}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FocusCapture] 分组删除遍历会话失败: {ex.Message}");
        }

        // ② 再删分组记录
        var removed = false;
        ChatGroupStore.Mutate(groups =>
        {
            removed = groups.RemoveAll(g => g.Id == id) > 0;
            return removed;
        });

        if (removed) AppLog.Info("ChatGroup", $"已删除分组 {id}，{moved} 个会话回到未分组");
        error = removed ? "" : "分组不存在（可能已在另一台设备上删除）";
        return removed;
    }

    /// <summary>
    /// 写入分组指令（Agent.md 等价物）。收藏分区不支持指令。超长自动截断到 <see cref="MaxInstructionChars"/>。
    /// </summary>
    public static bool SetInstruction(string id, string instruction)
    {
        instruction = instruction ?? "";
        if (instruction.Length > MaxInstructionChars) instruction = instruction[..MaxInstructionChars];
        if (ChatGroupStore.IsFavorite(id)) return false;

        var ok = false;
        ChatGroupStore.Mutate(groups =>
        {
            var target = groups.FirstOrDefault(g => g.Id == id);
            if (target == null) return false;
            if (string.Equals(target.Instruction, instruction, StringComparison.Ordinal))
            {
                ok = true;
                return false;   // 内容没变，不落盘（避免每次点确定都刷新同步时间戳）
            }
            target.Instruction = instruction;
            target.InstructionUpdatedAt = DateTime.Now;   // 跨端 LWW 判方向
            ok = true;
            return true;
        });
        return ok;
    }

    /// <summary>
    /// 自愈：把 GroupId 指向「清单里已不存在的分组」的会话清回未分组，返回修好的会话数。
    ///
    /// 什么情况会产生悬空引用：① 旧版本删分组的非原子操作留下的残留
    /// ② 另一台设备删了分组，本机同步到"分组清单少了一条"但会话文件还没轮到更新。
    /// 不修的话界面上会出现一个「（未知分组）」幽灵分区。
    /// </summary>
    public static int RepairDanglingGroupIds()
    {
        var valid = ChatGroupStore.Load()
            .Select(g => g.Id)
            .ToHashSet(StringComparer.Ordinal);

        var fixedCount = 0;
        try
        {
            var dir = FocusCapturePaths.Combine("chat_history");
            if (!Directory.Exists(dir)) return 0;

            foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
            {
                try
                {
                    var svc = ChatSessionService.Load(file);
                    if (svc == null || svc.GroupId.Length == 0) continue;
                    if (valid.Contains(svc.GroupId)) continue;

                    svc.GroupId = "";
                    svc.Save();
                    fixedCount++;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[FocusCapture] 悬空分组引用自愈跳过文件: {file}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FocusCapture] 悬空分组引用自愈失败: {ex.Message}");
        }

        if (fixedCount > 0) AppLog.Info("ChatGroup", $"已把 {fixedCount} 个悬空分组引用的会话放回未分组");
        return fixedCount;
    }
}
