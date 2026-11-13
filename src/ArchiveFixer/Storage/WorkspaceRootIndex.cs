using System;
using System.Collections.Generic;

namespace ArchiveFixer.Storage
{
    /// <summary>
    /// **本次会话用过的工作区根**的小账本（内存里的一张表，进程退出就没了）。
    ///
    /// <para><b>为什么要记</b>：工作区落在**这一单的目标目录**里之后，根会**随目标目录变**
    /// （这次 <c>D:\out\包A\.ArchiveFixer.work</c>、下一次 <c>E:\别的\包B\.ArchiveFixer.work</c>）。
    /// 而"③ 清理与删除"页要能列得出**这一趟真的产生过**的残留 —— 不记的话，换过目标目录之后
    /// 上一批的残留就再也列不出来，用户只能自己去翻隐藏目录。</para>
    ///
    /// <para>⛔ <b>它只记本次会话（用户 2026-09-30 明确收口）</b>：以前它是一份**落盘的历史清单**
    /// （<c>&lt;数据根&gt;\workspace-roots.txt</c>，记得住 8 个），于是"要扫哪些根"里混进了
    /// **上个星期、另一块盘**上的老目录 —— 那些目录还存不存在、是不是用户自己的东西，谁也答不上来。
    /// "记住上次用过的根"正是用户不要的那种"固定一个位置"的思想，所以：</para>
    /// <list type="bullet">
    /// <item><description>**不落盘**：没有文件、没有键、没有历史；重启之后只剩"当前生效的根 + 老位置"。</description></item>
    /// <item><description>**只进不退**：本进程里用过的根都留在表里（③ 页这一趟都要能列出它们的残留），
    /// 但**绝不**把表里的条目当成"下一批该用哪个根"的候选 —— 那是求解器的事，只有目标目录说了算。</description></item>
    /// </list>
    /// </summary>
    public static class WorkspaceRootIndex
    {
        private static readonly object Gate = new();

        private static readonly List<string> Roots = new();

        /// <summary>本次会话用过的工作区根（去重、最近的排最前）。返回的是**副本**，改它不影响账本。</summary>
        public static List<string> SessionRoots
        {
            get
            {
                lock (Gate)
                {
                    return new List<string>(Roots);
                }
            }
        }

        /// <summary>
        /// 记住一个**本次会话用过**的根（放最前）。
        /// </summary>
        /// <returns>记完之后那一份（调用方照样能用；改它不影响账本）。</returns>
        public static List<string> Remember(string? rootDirectory)
        {
            lock (Gate)
            {
                if (!string.IsNullOrWhiteSpace(rootDirectory))
                {
                    string trimmed = rootDirectory.Trim();

                    // 先把它从旧位置摘掉：同一个根重复出现只会让表里全是同一个。
                    Roots.RemoveAll(existing => string.Equals(
                        existing,
                        trimmed,
                        StringComparison.OrdinalIgnoreCase));

                    Roots.Insert(0, trimmed);
                }

                return new List<string>(Roots);
            }
        }

        /// <summary>清空账本（只给测试用：会话状态是进程级的静态，用例之间必须能回到干净状态）。</summary>
        internal static void ResetForTests()
        {
            lock (Gate)
            {
                Roots.Clear();
            }
        }
    }
}
