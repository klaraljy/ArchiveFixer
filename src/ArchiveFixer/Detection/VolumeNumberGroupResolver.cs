using System;
using System.Collections.Generic;
using System.Linq;

namespace ArchiveFixer.Detection;

/// <summary>
/// **整组定序**：把同一格式的内容读数定序成 1..N 的一组（主类
/// <c>VolumeNumberFromContent.ResolveGroup</c> 的唯一实现）。
///
/// <para><b>RAR 组</b>：RAR5 的内容里就是绝对卷号；RAR 1.5–4.x（RAR4）的字段基数待定 ⇒
/// 用"整组必须连成 1..N"判，再用主头的"这是第 1 卷"标记交叉核对（<see cref="VolumeFirstVolumeJudge"/>）。</para>
///
/// <para><b>跨盘 zip 组</b>：只有末片的内容里有盘号（= 总片数）。非末片内容里没有盘号，
/// 所以**只有"恰好剩一个缺口"时**才能用消去法定位 —— 也就是 N=2 才能直接定序；
/// N ≥ 3 时中间几片的先后只能靠"末片自述的 n + 名字里 zNN 的受约束唯一指派 + 尺寸规律"，
/// 超范围（如 z11）、撞号、名字读不出 ⇒ 判不出、什么都不做（用户 2026-09-29 明确的规矩）。</para>
///
/// <para>生命周期：纯静态、无状态；只读调用方给的事实清单，不枚举目录、不碰文件系统、不调引擎。</para>
/// </summary>
internal static class VolumeNumberGroupResolver
{
    /// <summary>
    /// 把"同目录这一批文件"的内容读数**定序成一组**（内容级的唯一出口）。
    ///
    /// <para>只拿与 <paramref name="currentPath"/> **同格式**的读数参与：目录里同时躺着 RAR 与跨盘 zip 时
    /// 不混组（混起来就没有"连成 1..N"可言，那正是最该拒绝的情形）。</para>
    /// </summary>
    /// <param name="requireCurrentFirstVolume">
    /// 手上这一卷**必须**是第 1 卷吗？默认 <c>true</c>（老口径，一个字节没放宽）。
    /// <c>false</c> 只给**改名计划**那一处用（方案《分卷族系统化识别》§4 阶段 C）：
    /// RAR <c>partN</c> 族的族标记 <c>partN</c> 本身就是"我是第 1 卷"的同义语，所以
    /// 「谁拿着这一组」这一层判据照旧（<c>OneClickCoordinator</c> 与归组按名字算），
    /// 而"要不要改名"是用户点的那一下 —— 他手上可能是 <c>.part2.rar</c>。
    /// ⛔ 这一档**不是**放宽结论：卷号仍由内容给出、仍要求连成 1..N、改名仍只由
    /// <c>TryApply</c> 全成或全不成地执行（判不出 ⇒ 什么都不做）。</param>
    public static VolumeGroupOrder ResolveGroup(
        string? currentPath,
        IEnumerable<VolumeNumberReading>? readings,
        bool requireCurrentFirstVolume = true)
    {
        var list = (readings ?? Array.Empty<VolumeNumberReading>())
            .Where(r => r != null && !string.IsNullOrWhiteSpace(r.Path))
            .ToList();

        string path = currentPath ?? string.Empty;

        // 调用方给的那一卷必须在里面（枚举同目录时通常已经含它；不含就自己补一次）。
        if (!list.Any(r => SamePath(r.Path, path)))
        {
            VolumeNumberReading self = VolumeContentFormatProbe.Read(path);
            list.Add(self);
        }

        VolumeNumberReading? current = list.FirstOrDefault(r => SamePath(r.Path, path));

        if (current == null)
        {
            return Refuse(VolumeNumberFail.NotAnArchive, 0);
        }

        switch (current.Format)
        {
            case VolumeContentFormat.Rar:
                return ResolveRarGroup(list, path, requireCurrentFirstVolume);

            case VolumeContentFormat.Zip:
                return ResolveZipGroup(list, path);

            default:
                return Refuse(VolumeNumberFail.NotAnArchive, 0);
        }
    }

    /// <summary>
    /// RAR 组：RAR5 的内容里就是绝对卷号；RAR 1.5–4.x（RAR4）的字段基数待定 →
    /// 用"整组必须连成 1..N"判，再用主头的"这是第 1 卷"标记交叉核对。
    /// </summary>
    /// <param name="requireCurrentFirstVolume">
    /// 见 <see cref="ResolveGroup"/> 的同名参数：<c>false</c> = 改名计划那一档（用户手上可能是
    /// <c>.part2.rar</c>，卷号照样只由内容回答）。⛔ 卷号的证据一个字没放宽。
    /// </param>
    private static VolumeGroupOrder ResolveRarGroup(
        IReadOnlyList<VolumeNumberReading> list,
        string currentPath,
        bool requireCurrentFirstVolume = true)
    {
        List<VolumeNumberReading> members = list
            .Where(r => r.Format == VolumeContentFormat.Rar && r.IsVolumeMember)
            .ToList();

        if (members.Count < 2)
        {
            return Refuse(VolumeNumberFail.GroupIncomplete, members.Count);
        }

        bool hasRar4Legacy = members.Any(m => m.Number == null);
        bool hasRar5 = members.Any(m => m.Number != null);

        if (hasRar4Legacy && hasRar5)
        {
            // 同一个目录里混着 RAR 1.5–4.x（RAR4）与 RAR5 的分卷：不是一组，绝不猜。
            return Refuse(VolumeNumberFail.GroupNotContiguous, members.Count);
        }

        if (members.Any(m => m.Fail == VolumeNumberFail.RarOldNumbering))
        {
            return Refuse(VolumeNumberFail.RarOldNumbering, members.Count);
        }

        var numbers = new List<int>(members.Count);

        if (hasRar4Legacy)
        {
            /*
             * RAR 1.5–4.x 的卷号字段基数文档没写。两种解释各自要求"整组连成 1..N"：
             * 字段本身就是 1 起的 → 原样；字段是 0 起的 → 全体 +1。
             * 两种都成立（或都不成立）= 没有任何证据能定基数 → 不认（用户 2026-09-29 明确的规矩）。
             */
            bool zeroBased = IsContiguousRun(members.Select(m => m.RawField!.Value + 1), members.Count);
            bool oneBased = IsContiguousRun(members.Select(m => m.RawField!.Value), members.Count);

            if (zeroBased == oneBased)
            {
                return Refuse(VolumeNumberFail.GroupBaseAmbiguous, members.Count);
            }

            numbers.AddRange(members.Select(m => zeroBased ? m.RawField!.Value + 1 : m.RawField!.Value));

            /*
             * 交叉核对：RAR 1.5–4.x 主头有"这一卷就是第 1 卷"标记（MHD_FIRSTVOLUME）。
             * 少了这一步，"0 起的整组里恰好缺了第 1 卷"会被读成"1 起的一组"——
             * 那就会把第 2 卷改名叫 part1.rar（改错名字比不改更糟）。
             */
            if (!VolumeFirstVolumeJudge.RarHeaderFlagsMatchNumbers(members, numbers))
            {
                return Refuse(VolumeNumberFail.RarFirstVolumeMismatch, members.Count);
            }
        }
        else
        {
            numbers.AddRange(members.Select(m => m.Number!.Value));

            if (!IsContiguousRun(numbers, members.Count))
            {
                // 绝对卷号却连不成 1..N = 缺卷或有一卷被改坏后认不出来：整组都不动。
                return Refuse(VolumeNumberFail.GroupNotContiguous, members.Count);
            }

            /*
             * ⛔ RAR 族：主头那个"这一卷就是第 1 卷"标记必须与内容卷号互相印证
             * （2026-10-22：RAR4 也读得出卷号之后，这一格不能只对"卷号判不出"那半边生效 ——
             * 否则把第 1 卷标记长在第 3 份上的残组照样被认成 1..N）。
             * 判据只读两条内容事实：标记个数、标记那一份的卷号。
             */
            if (members.All(m => m.Format == VolumeContentFormat.Rar && m.RarNumbering == RarNumberingFamily.New))
            {
                if (!VolumeFirstVolumeJudge.RarHeaderFlagsMatchNumbers(members, numbers))
                {
                    return Refuse(VolumeNumberFail.RarFirstVolumeMismatch, members.Count);
                }
            }
        }

        var slots = new List<VolumeGroupSlot>(members.Count);

        for (int index = 0; index < members.Count; index++)
        {
            slots.Add(new VolumeGroupSlot { Path = members[index].Path, Number = numbers[index] });
        }

        slots.Sort((a, b) => a.Number.CompareTo(b.Number));

        if (!VolumeFirstVolumeJudge.GroupStartsAtFirstVolume(slots))
        {
            return Refuse(VolumeNumberFail.CurrentNotFirstVolume, members.Count);
        }

        /*
         * 第 1 卷不在手上（老口径）⇒ 不认。
         *
         * ⚠ 这一档从 2026-10-04（方案 §4 阶段 C）起**只对"谁拿着这一组"那一层成立**，
         * 改名计划那一档走 `requireCurrentFirstVolume: false` —— 用户点的是他自己手上那一卷
         * （可能就是 `.part2.rar`），而"要不要改名"由内容卷号 + 全成或全不成两把尺子回答，
         * 与"入口是不是第 1 卷"无关。⛔ 判据本身一个字没放宽：卷号仍只由内容给出，
         * 仍要求连成 1..N；`OneClickCoordinator` 照旧只让第 1 卷那一单负责整组。
         */
        if (!VolumeFirstVolumeJudge.IsEntryPointFirstVolume(currentPath, slots[0].Path, requireCurrentFirstVolume))
        {
            return Refuse(VolumeNumberFail.CurrentNotFirstVolume, members.Count);
        }

        return new VolumeGroupOrder
        {
            Confirmed = true,
            Slots = slots,
            Fail = VolumeNumberFail.None,
            Detail = slots.Count
        };
    }

    /// <summary>
    /// 跨盘 zip 组：只有末片的内容里有盘号（= 总片数）。非末片内容里没有盘号，
    /// 所以**只有"恰好剩一个缺口"时**才能用消去法定位 —— 也就是 N=2 才能定序；
    /// N ≥ 3 时中间几片的先后无从判断，一律不认（用户 2026-09-29 明确的规矩）。
    /// </summary>
    private static VolumeGroupOrder ResolveZipGroup(IReadOnlyList<VolumeNumberReading> list, string currentPath)
    {
        List<VolumeNumberReading> members = list
            .Where(r => r.Format == VolumeContentFormat.Zip && r.IsVolumeMember)
            .ToList();

        List<VolumeNumberReading> tails = members.Where(m => m.Number != null).ToList();
        List<VolumeNumberReading> segments = members.Where(m => m.Number == null).ToList();

        if (tails.Count == 0)
        {
            if (members.Count == 0 && list.Any(r => r.Format == VolumeContentFormat.Zip))
            {
                return Refuse(VolumeNumberFail.ZipSingleDisk, 1);
            }

            /*
             * ===== 通用分片族 `X.zip.001..00N` 的**内容证据**（设计文档 §6.6 A / 工单 #10）=====
             * 这一族的末片 EOCD 写的是"盘号 0"（7-Zip `-tzip -v` 就是这么切的）⇒ 它算不上跨盘成员
             * （`IsVolumeMember = false`），但"**尾部有 EOCD**"这条内容事实成立，而且**整族里只有它一份有**。
             * ⇒ 认末片用它（⛔ 不再只靠名字序号），其余片按名字序号 1..n-1 做受约束唯一指派；
             * 序号读不出 / 撞号 / 越界 / 序号判断不过 ⇒ 判不出（什么都不做）。
             */
            List<VolumeNumberReading> endRecords = list
                .Where(r => r.Format == VolumeContentFormat.Zip && r.HasZipEndRecord)
                .ToList();

            if (endRecords.Count == 1)
            {
                VolumeNumberReading end = endRecords[0];

                /*
                 * ===== 通用分片族：**纯内容定序**（名字全烂、还分处不同文件夹时的唯一出路）=====
                 * 现场（新样本 `6666` 的 `recipe.json` 逐字）：三片分别在三个文件夹，名字是
                 * `打包111-W0-0` / `…W1-0.png` / `…L2.zip.000003` —— 名字一点忙都帮不上。
                 * 内容给三条事实：① 尾部有 EOCD 的是**末片**；② 开头是**本地文件头** `PK\x03\x04`
                 * 的是**第 1 片**；③ 剩下的就是中间片 —— **恰好 3 片**时中间片唯一，顺序才定得下来
                 * （多于 3 片 ⇒ 判不出，⛔ 不猜）。
                 */
                List<VolumeNumberReading> localHeaderPieces = members
                    .Where(member => member.HasZipLocalHeader)
                    .ToList();
                List<VolumeNumberReading> middlePieces = members
                    .Where(member => !member.HasZipLocalHeader)
                    .ToList();

                if (localHeaderPieces.Count == 1 && middlePieces.Count == 1 && members.Count == 2)
                {
                    return new VolumeGroupOrder
                    {
                        Confirmed = true,
                        Slots = new List<VolumeGroupSlot>
                        {
                            new() { Path = localHeaderPieces[0].Path, Number = 1 },
                            new() { Path = middlePieces[0].Path, Number = 2 },
                            new() { Path = end.Path, Number = 3 }
                        },
                        Fail = VolumeNumberFail.None,
                        Detail = 3
                    };
                }

                string endName = System.IO.Path.GetFileName(end.Path);

                /*
                 * 末片的名字形如 `X.zip.00N`（三位数字段）。取基名（去掉最后那段数字），
                 * 再把**同前缀 + 数字段连续 1..N** 的那几份按序号定序 —— 这是"序号"证据；
                 * "拼接合法性"由**末片尾部真有 EOCD** 这条内容证据提供（`HasZipEndRecord`）。
                 * ⛔ 序号读不出 / 不连续 / 末片不在 N / 非末片比末片还小 ⇒ 判不出。
                 */
                int lastDot = endName.LastIndexOf('.');
                string genericStem = lastDot > 0 ? endName.Substring(0, lastDot) : string.Empty;
                string lastMark = lastDot > 0 ? endName.Substring(lastDot + 1) : string.Empty;

                int endIndex = IsThreeDigitNumber(lastMark)
                    ? int.Parse(lastMark, System.Globalization.CultureInfo.InvariantCulture)
                    : 0;

                if (genericStem.Length > 0 && endIndex >= 2)
                {
                    var orderedGeneric = new List<(int Index, VolumeNumberReading Reading)>();

                    foreach (VolumeNumberReading piece in list)
                    {
                        string pieceName = System.IO.Path.GetFileName(piece.Path);

                        if (!pieceName.StartsWith(genericStem + ".", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        string mark = pieceName.Substring(genericStem.Length + 1);

                        if (!IsThreeDigitNumber(mark))
                        {
                            continue;
                        }

                        orderedGeneric.Add((
                            int.Parse(mark, System.Globalization.CultureInfo.InvariantCulture),
                            piece));
                    }

                    if (orderedGeneric.Count == endIndex
                        && orderedGeneric.Select(item => item.Index).Distinct().Count() == endIndex
                        && orderedGeneric.Min(item => item.Index) == 1)
                    {
                        var genericSlots = new List<VolumeGroupSlot>();

                        foreach ((int index, VolumeNumberReading piece) in orderedGeneric)
                        {
                            if (index < endIndex && piece.Size > 0 && end.Size > 0 && piece.Size < end.Size)
                            {
                                return Refuse(VolumeNumberFail.ZipPartsMissing, orderedGeneric.Count);
                            }

                            genericSlots.Add(new VolumeGroupSlot { Path = piece.Path, Number = index });
                        }

                        genericSlots.Sort((left, right) => left.Number.CompareTo(right.Number));

                        if (!genericSlots.Any(slot => SamePath(slot.Path, currentPath)))
                        {
                            return Refuse(VolumeNumberFail.CurrentNotInGroup, genericSlots.Count);
                        }

                        return new VolumeGroupOrder
                        {
                            Confirmed = true,
                            Slots = genericSlots,
                            Fail = VolumeNumberFail.None,
                            Detail = endIndex
                        };
                    }
                }
            }

            return Refuse(VolumeNumberFail.ZipPartsMissing, members.Count);
        }

        if (tails.Count > 1)
        {
            // 一个目录里出现两个"末片"：说不清哪一片才是末片，不认。
            return Refuse(VolumeNumberFail.ZipPartsMissing, tails.Count);
        }

        int total = tails[0].Total ?? tails[0].Number!.Value;

        if (total < 2)
        {
            return Refuse(VolumeNumberFail.ZipSingleDisk, total);
        }

        /*
         * ⛔ **不再限制"只能 2 片"**（2026-10-07 真机 EEEE）：老写法在这里一句
         * 	total > 2 ⇒ Refuse(ZipTooManyDisks) 就把 4 片那一组整条内容路放弃掉，
         * 于是退回名字路、产出没有证据的目标名（111.z11 → 111.z03），
         * 而末片 gather 仍报缺 z01/z02/z03。⇒ 改成"片数由末片自述的 n 决定"：
         * 非末片必须恰好 total - 1 片（下面按名字做受约束唯一指派）。
         */
        if (segments.Count != total - 1)
        {
            return Refuse(VolumeNumberFail.ZipPartsMissing, segments.Count);
        }

        if (!members.Any(m => SamePath(m.Path, currentPath)))
        {
            // 手上的这一片不在这一组里（同目录还躺着别的跨盘组）。
            return Refuse(VolumeNumberFail.CurrentNotInGroup, members.Count);
        }

        /*
         * 尺寸规律：跨盘 zip 的**非末片**都是切分上限那么大的满片，末片是余量 ——
         * 所以"该接在末片前面"的那一片必定**不小于**末片。比末片还小说明这两个文件不是一组，
         * 认了就等于把一个不相干的文件改成 `.z01`（改错名字比不改更糟）。
         * ⚠ 只在两边都取到字节数时判；取不到（被占 / 权限）时不拿 0 去比，免得把真组挡在门外。
         */
        if (segments[0].Size > 0 && tails[0].Size > 0 && segments[0].Size < tails[0].Size)
        {
            return Refuse(VolumeNumberFail.ZipPartsMissing, 1);
        }

        if (total == 2)
        {
            // 2 片这一档**逐字保持老口径**（一片无自述⇒盘 1、末片⇒盘 2），避免动到既有行为。
            return new VolumeGroupOrder
            {
                Confirmed = true,
                Slots = new List<VolumeGroupSlot>
                {
                    new() { Path = segments[0].Path, Number = 1 },
                    new() { Path = tails[0].Path, Number = 2 }
                },
                Fail = VolumeNumberFail.None,
                Detail = 2
            };
        }

        /*
         * **n ≥ 3：受约束唯一指派**（判据只用能证的事实）：
         * ① 末片自述的 n 把合法盘号夹死在 1..n-1（末片自己 = n）；
         * ② 其余片内容里没有盘号（裸切块），只能按**名字**里的 zNN 定盘号
         *    （跨盘 ZIP 口径：zNN ⇒ 卷序 NN+1 ⇒ 盘号 NN）；
         * ③ 非末片都是满片、末片是余量 ⇒ 比末片还小的不可能是这一组的非末片；
         * ⛔ 超范围（如 z11）、撞号、名字读不出 ⇒ 判不出、什么都不做。
         */
        var slots = new List<VolumeGroupSlot>
        {
            new() { Path = tails[0].Path, Number = total }
        };

        var usedDisks = new List<int> { total };

        foreach (VolumeNumberReading segment in segments)
        {
            int? nameIndex = LegacyVolumeNames.TryGetIndex(
                System.IO.Path.GetFileName(segment.Path));

            if (nameIndex == null)
            {
                return Refuse(VolumeNumberFail.ZipPartsMissing, segments.Count);
            }

            int disk = nameIndex.Value - 1;

            if (disk < 1 || disk >= total || usedDisks.Contains(disk))
            {
                return Refuse(VolumeNumberFail.ZipPartsMissing, segments.Count);
            }

            if (segment.Size > 0 && tails[0].Size > 0 && segment.Size < tails[0].Size)
            {
                return Refuse(VolumeNumberFail.ZipPartsMissing, segments.Count);
            }

            usedDisks.Add(disk);
            slots.Add(new() { Path = segment.Path, Number = disk });
        }

        // `Slots` 的契约是**按盘号升序**（2 片那一档就是 [盘1, 盘2]）⇒ 末片排到最后。
        slots.Sort((left, right) => left.Number.CompareTo(right.Number));

        if (!IsContiguousRun(usedDisks, total))
        {
            return Refuse(VolumeNumberFail.ZipPartsMissing, segments.Count);
        }

        return new VolumeGroupOrder
        {
            Confirmed = true,
            Slots = slots,
            Fail = VolumeNumberFail.None,
            Detail = total
        };
    }

    /// <summary>这一批卷号是不是**恰好** 1..N（各不重复、最小 1、最大 N）。</summary>
    private static bool IsThreeDigitNumber(string? mark)
        => mark is { Length: 3 }
           && mark.All(character => character >= '0' && character <= '9');

    private static bool IsContiguousRun(IEnumerable<int> numbers, int count)
    {
        var sorted = numbers.OrderBy(v => v).ToList();

        if (sorted.Count != count || count == 0)
        {
            return false;
        }

        for (int index = 0; index < count; index++)
        {
            if (sorted[index] != index + 1)
            {
                return false;
            }
        }

        return true;
    }

    private static VolumeGroupOrder Refuse(VolumeNumberFail fail, int detail) => new()
    {
        Confirmed = false,
        Fail = fail,
        Detail = detail
    };

    private static bool SamePath(string? a, string? b) =>
        !string.IsNullOrWhiteSpace(a)
        && !string.IsNullOrWhiteSpace(b)
        && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
