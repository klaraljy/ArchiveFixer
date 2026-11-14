using System;
using System.Collections.Generic;
using System.Linq;

namespace ArchiveFixer.Services.Password;

/// <summary>
/// 试密码的**运行期账本**：每个归档最近成功的密码、每条密码的历史成功次数、
/// 以及"本次导入内已成功的密码 / 本批成功次数"。
///
/// <para>三本账的用途不同，别混：
/// <list type="bullet">
/// <item><description><b>历史次数</b>（<see cref="_successCounts"/>）跨重启、跨批次保留，只用于展示/落盘
/// （随「记住密码列表」加密存到 <c>PasswordListSnapshot.SuccessCounts</c>）；⛔ **候选顺序不读它**
/// （跨批次重排已被用户 2026-10-04 否掉）。</description></item>
/// <item><description><b>本批已成功</b>（<see cref="_batchVerifiedPasswords"/>）只记内存、<see cref="BeginBatch"/> 清零；
/// 它被提到后续任务候选的最前面（只提前、绝不删减原候选链），于是 N 个同源包从 O(N×k) 降到 O(N)。</description></item>
/// <item><description><b>本批次数</b>（<see cref="_batchSuccessCounts"/>）只给"这一档内部"排序用
/// （同一次导入内按频率排，用户 2026-10-16 当面许可）；下一次导入回到④页列表顺序。</description></item>
/// </list></para>
///
/// <para>⚠ 并行解压时多个任务会同时记账，所以对这个类的读写一律在 <see cref="_gate"/> 里 ——
/// <c>Dictionary</c> 不是线程安全的。空密码不算"一条密码"，因此不进任何一本账、也不进复用清单。</para>
/// </summary>
internal sealed class PasswordAttemptStats
{
    /// <summary>每个归档最近一次成功的密码：同一个包重试时先试它，省掉一轮无谓试错。</summary>
    private readonly Dictionary<string, string> _recentSuccess = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>每条密码成功解开过多少次（**大小写敏感**：密码就是字节，与密码本解析/去重口径一致）。</summary>
    private readonly Dictionary<string, int> _successCounts = new(StringComparer.Ordinal);

    /// <summary>本次运行里**已经成功试出来过**的密码（顺序 = 首次成功的先后）。</summary>
    private readonly List<string> _batchVerifiedPasswords = new();

    /// <summary>**本次导入内**每条密码成功过多少次（只给候选排序这一档用，<see cref="BeginBatch"/> 清零）。</summary>
    private readonly Dictionary<string, int> _batchSuccessCounts = new(StringComparer.Ordinal);

    private readonly object _gate = new();

    /// <summary>记了一条**非空**密码的成功（调用方据此决定要不要落盘；空密码不记账）。</summary>
    internal event Action? SuccessRecorded;

    /// <summary>记录某个归档刚刚用哪个密码成功过（最近成功只留内存；成功次数会触发落盘）。</summary>
    internal void RecordSuccess(string? archivePath, string? password)
    {
        if (string.IsNullOrWhiteSpace(archivePath))
        {
            return;
        }

        lock (_gate)
        {
            _recentSuccess[archivePath.Trim()] = password ?? string.Empty;
        }

        RecordVerifiedPassword(password);
    }

    /// <summary>这个归档最近一次成功的密码（没有就返回 false）。</summary>
    internal bool TryGetRecentSuccess(string archivePath, out string? password)
    {
        lock (_gate)
        {
            return _recentSuccess.TryGetValue(archivePath.Trim(), out password);
        }
    }

    /// <summary>
    /// **本批开工**：把"本批已成功的密码"清空（用户 2026-10-04 的口径）。
    ///
    /// <para>用户原话：「谁规定你可以在**不同次任务**中降序排列的，**同一次任务中是可以这样，
    /// 不同次文件一律严格按我列表顺序**」⇒ "复用本批已成功的密码"这一档**只在本批内生效**：
    /// ⛔ 不许写回设置、⛔ 不许影响下一批。</para>
    /// </summary>
    internal void BeginBatch()
    {
        lock (_gate)
        {
            _batchVerifiedPasswords.Clear();
            _batchSuccessCounts.Clear();
        }
    }

    /// <summary>
    /// 这条密码**本次导入内**成功过多少次（0 = 本批还没成功过）。
    /// 候选排序只读它，不读跨批次的 <see cref="GetSuccessCount"/>。
    /// </summary>
    internal int GetBatchSuccessCount(string? password)
    {
        if (string.IsNullOrEmpty(password))
        {
            return 0;
        }

        lock (_gate)
        {
            return _batchSuccessCounts.TryGetValue(password, out int count) ? count : 0;
        }
    }

    /// <summary>这条密码成功解开过多少次（0 = 没有记录）。给测试与排障用。</summary>
    internal int GetSuccessCount(string? password)
    {
        if (string.IsNullOrEmpty(password))
        {
            return 0;
        }

        lock (_gate)
        {
            return _successCounts.TryGetValue(password, out int count) ? count : 0;
        }
    }

    /// <summary>
    /// 本次运行里已经成功过的密码（副本，顺序 = 首次成功的先后）。
    ///
    /// <para>⚠ 2026-09-26 第 45 条：它们现在是候选表里的**第一条**（不再是"空密码之后"）——
    /// 有已知密码时，空密码**根本不再进候选**（加密包的空密码永远不可能对，见第 37 条实测）。
    /// 消费点 <see cref="PasswordCandidateBuilder.GetPasswordCandidates"/>
    /// 在这一档内部按 <see cref="GetBatchSuccessCount"/> 从多到少排（稳定排序，次数相同保持先后）。</para>
    /// </summary>
    internal IReadOnlyList<string> BatchVerifiedPasswords
    {
        get
        {
            lock (_gate)
            {
                return _batchVerifiedPasswords.ToList();
            }
        }
    }

    /// <summary>从记忆里读回历史成功次数（v1 旧文件没有这一项 → 空表，一切按原顺序）。</summary>
    internal void LoadSuccessCounts(IEnumerable<KeyValuePair<string, int>> counts)
    {
        lock (_gate)
        {
            _successCounts.Clear();

            foreach (KeyValuePair<string, int> pair in counts)
            {
                _successCounts[pair.Key] = pair.Value;
            }
        }
    }

    /// <summary>把历史成功次数复制进要落盘的快照（调用方持有快照，不把锁带出去）。</summary>
    internal void CopySuccessCountsTo(IDictionary<string, int> target)
    {
        lock (_gate)
        {
            foreach (KeyValuePair<string, int> pair in _successCounts)
            {
                target[pair.Key] = pair.Value;
            }
        }
    }

    /// <summary>
    /// 一条密码刚刚被验证成功：记进"本批已成功"清单（供后续任务复用）并累加成功次数。
    ///
    /// <para>空密码不算"一条密码"（它是 §9.2 的第一档"不需要密码"，本来就是每个包的第一步），
    /// 所以不记账、也不进复用清单 —— 否则"复用"这一档会变成一句"再试一次空密码"。</para>
    /// </summary>
    private void RecordVerifiedPassword(string? password)
    {
        if (string.IsNullOrEmpty(password))
        {
            return;
        }

        lock (_gate)
        {
            _successCounts[password] = _successCounts.TryGetValue(password, out int count) ? count + 1 : 1;
            _batchSuccessCounts[password] = _batchSuccessCounts.TryGetValue(password, out int batchCount)
                ? batchCount + 1
                : 1;

            if (!_batchVerifiedPasswords.Contains(password, StringComparer.Ordinal))
            {
                _batchVerifiedPasswords.Add(password);
            }
        }

        // 成功次数要跨重启（用户要的"按使用频率排序"第二次启动仍然成立）。
        // 关掉「记住密码列表」时订阅方自己会一个字节都不写。
        SuccessRecorded?.Invoke();
    }
}
