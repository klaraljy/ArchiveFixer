using ArchiveFixer.Models;
using ArchiveFixer.Password;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ArchiveFixer.Services.Password;

/// <summary>
/// **密码候选**：按固定顺序把"某个任务该试哪些密码"排出来，并给日志一句脱敏的来源说明。
///
/// <para>顺序按 AGENTS.md §9.2 定死，只有一处被用户明确要求调整过（2026-09-24 第 14 条，
/// 2026-09-26 第 45 条改位置）：空密码 → **本批已成功的密码（复用）** → 本任务最近成功 →
/// 密码本映射命中 → 单任务密码 → 统一密码 → 密码列表 → 密码本整行候选 → 同目录说明文件。</para>
///
/// <para>越靠前的越可能命中、也越"便宜"；旁路说明文件是猜的，放最后，避免把一句噪声排到
/// 用户明确给的密码前面。复用只是**提前**、绝不**删减**：不同包的密码可能不同，
/// 原来的候选链在后面原样保留。</para>
/// </summary>
internal sealed class PasswordCandidateBuilder
{
    private readonly PasswordAttemptStats _stats;
    private readonly PasswordBookLibrary _books;
    private readonly PasswordSidecarCandidateSource _sidecar;

    internal PasswordCandidateBuilder(
        PasswordAttemptStats stats,
        PasswordBookLibrary books,
        PasswordSidecarCandidateSource sidecar)
    {
        _stats = stats;
        _books = books;
        _sidecar = sidecar;
    }

    /// <summary>按归档文件名匹配密码本里的映射式条目（命中优先于遍历整表）。</summary>
    internal IReadOnlyList<PasswordEntry> MatchMappedEntries(string? archiveFileName)
    {
        if (string.IsNullOrWhiteSpace(archiveFileName) || _books.BookEntries.Count == 0)
        {
            return Array.Empty<PasswordEntry>();
        }

        return PasswordBookParser.MatchByName(_books.BookEntries, archiveFileName);
    }

    /// <summary>
    /// 组装某个任务的密码候选（顺序见类注释与下面每一档的说明）。
    /// </summary>
    internal List<PasswordItem> GetPasswordCandidates(
        ArchiveTask task,
        string globalPassword,
        IEnumerable<PasswordItem> passwordList,
        bool tryEmptyFirst,
        bool includeSidecarCandidates = false)
    {
        var result = new List<PasswordItem>();
        var used = new HashSet<string>(StringComparer.Ordinal);

        void AddCandidate(string value, string source, string remark)
        {
            value ??= string.Empty;

            if (!used.Add(value))
            {
                return;
            }

            result.Add(new PasswordItem
            {
                Value = value,
                Source = source,
                IsEnabled = true,
                Remark = remark
            });
        }

        string archivePath = task?.CurrentPath ?? string.Empty;
        string archiveFileName = string.IsNullOrWhiteSpace(archivePath)
            ? string.Empty
            : Path.GetFileName(archivePath);

        /*
         * ==================== 顺序：**本批已成功的密码排在最前**（用户 2026-09-26 第 45 条）====================
         *
         * 他的原话："我这个在用特定解压，是多相同文件，密码都是一样的，你这个复用本批次已成功的密码，
         * 不就是在放屁吗，每次都是重新一轮密码检测" —— 真机日志（38 个同源包）逐字对应：
         * 每个包都是 `候选 1/10，尝试空密码` → `密码错误` → `候选 2/10，复用本批已成功的密码` 才解开。
         *
         * ⛔ 保留的边界：本批**还没有**成功过密码时，顺序与从前逐字相同（空密码仍在最前，`tryEmptyFirst` 说了算）——
         * 第一条候选怎么来的这件事不许变，否则"第一条就是密码本第一条"这类现场会再次说不清。
         *
         * ⚠ 2026-10-04（用户当天第二次口述）：这一档只保留**本批内**的复用（`BeginBatch` 每批清一次），
         * ⛔ 不再按历史成功次数排序（那正是跨任务重排）。
         */
        IReadOnlyList<string> verifiedPasswords = _stats.BatchVerifiedPasswords
            .Where(value => !string.IsNullOrEmpty(value))
            .ToList();

        bool hasVerifiedBatchPassword = verifiedPasswords.Count > 0;

        if (tryEmptyFirst && !hasVerifiedBatchPassword)
        {
            AddCandidate(string.Empty, "Empty", "空密码");
        }

        /*
         * 顺序 = **同一次导入内按成功频率从多到少**（读本批计数，不读跨批次历史次数）。
         * 口径出处（用户 2026-10-16 当面澄清逐字）：「我说过**同一次的解压可以按照频率次序来弄**，
         * 但是**不同批次的**就是密码本里面的，**同一次就是指同一次导入的**」。
         *
         * `OrderByDescending` 是**稳定**排序 ⇒ 次数相同仍然保持"首次成功的先后"。
         */
        foreach (string verified in verifiedPasswords
                     .OrderByDescending(_stats.GetBatchSuccessCount))
        {
            AddCandidate(verified, "BatchSuccess", "复用本批已成功的密码");
        }

        if (!string.IsNullOrWhiteSpace(archivePath) &&
            _stats.TryGetRecentSuccess(archivePath, out string? recentPassword))
        {
            AddCandidate(recentPassword ?? string.Empty, "RecentSuccess", "本任务最近成功的密码");
        }

        /*
         * 密码本里按归档名命中的那几条：**按密码本自己的顺序**（用户 2026-10-04 的口径：
         * 不同次文件一律严格按列表顺序，⛔ 不许按历史成功次数重排）。
         */
        foreach (PasswordEntry entry in MatchMappedEntries(archiveFileName))
        {
            string remark = string.IsNullOrWhiteSpace(entry.Name)
                ? "密码本命中"
                : $"密码本命中：{entry.Name}";

            AddCandidate(entry.Password, "BookMapped", remark);
        }

        if (task != null && !string.IsNullOrEmpty(task.Password))
        {
            AddCandidate(task.Password, "TaskPassword", "单任务密码");
        }

        if (!string.IsNullOrEmpty(globalPassword))
        {
            AddCandidate(globalPassword, "GlobalPassword", "统一密码");
        }

        if (passwordList != null)
        {
            /*
             * 密码列表这一层：**严格按用户在④页排的顺序**逐条加进候选。
             *
             * ⚠ 2026-10-04（用户当天第二次口述）：这里过去是"先按成功次数从多到少排"，
             * 用户原话：「谁规定你可以在**不同次任务**中降序排列的，**同一次任务中是可以这样，
             * 不同次文件一律严格按我列表顺序**」⇒ **不同次任务之间一个字都不许重排**。
             * （历史成功次数仍然记账、仍然落盘、`GetSuccessCount` 仍然可查 —— 只是不再拿它改顺序。）
             */
            var indexed = new List<(PasswordItem Item, int Index)>();

            int index = 0;

            foreach (PasswordItem item in passwordList)
            {
                index++;

                if (item == null || !item.IsEnabled)
                {
                    continue;
                }

                indexed.Add((item, index));
            }

            foreach ((PasswordItem item, int originalIndex) in indexed)
            {
                /*
                 * 注意：
                 * 密码允许是空格开头、空格结尾。
                 * 不要 Trim。
                 */
                string value = item.Value ?? string.Empty;

                AddCandidate(value, "ImportedList", $"密码列表第 {originalIndex} 项");
            }
        }

        /*
         * 密码本里那些"按映射式切过"的行：再把**整行**试一遍（2026-09-25 第 30 条）。
         *
         * 为什么（真会咬人的形态）：映射式与列表式只能靠"行里有没有冒号"猜，而列表式的密码本身
         * 完全可能带冒号（`abc:123`、`www.xxx.com:8888`）。那种书里，我们一直在拿冒号**右侧**当密码试，
         * 正确的整行**从来没有被试过** —— 用户看到的就是"密码本第一条就是这个包的密码，却一直密码错误"。
         *
         * 位置：排在显式给的密码（统一密码 / 密码列表）**之后**。
         */
        foreach (PasswordEntry entry in _books.BookEntries)
        {
            if (entry == null || entry.Kind != PasswordEntryKind.Mapped || string.IsNullOrEmpty(entry.RawLine))
            {
                continue;
            }

            AddCandidate(
                entry.RawLine!,
                "BookRawLine",
                $"密码本（第 {entry.LineNumber} 行整行）");
        }

        /*
         * 同目录说明文件里的密码：**显式开启才用**。
         * 它是"猜"出来的候选，所以排在用户明确给的密码之后（AGENTS.md §9.4）。
         */
        if (includeSidecarCandidates && !string.IsNullOrWhiteSpace(archivePath))
        {
            foreach ((string value, string remark) in _sidecar.Read(archivePath))
            {
                AddCandidate(value, "Sidecar", remark);
            }
        }

        /*
         * 如果用户关闭了空密码优先，并且没有任何密码，
         * 仍然补一个空密码，保证普通无密码压缩包能解压。
         */
        if (result.Count == 0)
        {
            AddCandidate(string.Empty, "Empty", "空密码");
        }

        return result;
    }

    /// <summary>
    /// 一次密码尝试的日志文案（来源 + 脱敏占位符，**绝不出现明文**）。
    /// 这是候选来源措辞的唯一出口：别处（比如递归层的候选描述）都复用它，
    /// 两处的措辞才会逐字一致。
    /// </summary>
    internal string BuildTryPasswordLogText(PasswordItem candidate, int index)
    {
        if (candidate == null)
        {
            return $"尝试密码候选第 {index} 项：******";
        }

        string source = candidate.Source ?? string.Empty;

        return source switch
        {
            "Empty" => "尝试空密码",

            // 用户 2026-09-24 第 14 条：本批已经试出来的密码会被提到候选最前面。
            // 日志必须能看出"这一条是复用来的"（否则排障时看不出顺序为什么变了）。
            "BatchSuccess" => "复用本批已成功的密码：******",
            "RecentSuccess" => "尝试本任务最近成功的密码：******",
            "BookMapped" => "尝试密码本命中项：******",
            "TaskPassword" => "尝试单任务密码：******",
            "GlobalPassword" => "尝试统一密码：******",
            "ImportedList" => $"尝试密码列表第 {index} 项：******",
            // 密码本里按映射式切过的那一行，按"整行就是密码"再试一次（列表式密码可能自带冒号）。
            "BookRawLine" => $"尝试密码本整行（第 {index} 项）：******",
            "ManualList" => $"尝试手动密码第 {index} 项：******",
            // 一键处理/批量开始前那一次询问得到的密码（ExtractionCoordinator.ManualPasswordSource）：
            // 以前它走 default 分支，日志里写成"密码候选第 N 项"，看不出这个密码是用户当场给的。
            "ManualBatch" => "尝试本次运行手动输入的密码：******",
            "Sidecar" => "尝试同目录说明文件里的密码：******",
            _ => $"尝试密码候选第 {index} 项：******"
        };
    }
}
