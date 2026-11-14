using ArchiveFixer.Helpers;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace ArchiveFixer.Detection;

/// <summary>
/// 卷名命名规则 ——「这一族里第 N 卷该叫什么 / 这个名字算不算标准卷名 / 它是不是同一卷的另一个名字」
/// 的**唯一出口**。
///
/// <para><b>为什么命名必须单独成层</b>：卷号判据（<see cref="VolumeGroupDetector"/>）只回答"这些文件像不像一组"，
/// 而"缺的那一卷叫什么名字"（给用户看的缺卷清单）、"这一片该还原成什么名字"（改名/接片那几条路）
/// 必须和它用**同一套族规则**。族规则一旦各写一份，就会出现"缺卷名说 <c>x.001</c>、盘上却叫 <c>x.7z.001</c>"
/// 这类永远凑不齐的形状（2026-10-14 环台实测）。</para>
///
/// <para><b>判据一个字都没改</b>：这里只是把原来内嵌在 <see cref="VolumeGroupResolver"/> 里的族/规范名/
/// 变体判定原样搬出来 —— 成员体、常量、文案、自验条件全部逐行保留。</para>
///
/// <para><b>失败模型</b>：判不出族 / 卷号解析不出 / 规范名自验不过 ⇒ 一律 <c>false</c> + 空串
/// （调用方按"判不出 ⇒ 什么都不做"处理），绝不抛、绝不猜一个名字出去。</para>
///
/// <para>生命周期：纯静态、无状态；只做字符串运算，不碰文件系统、不调引擎、不改名字。</para>
/// </summary>
internal static class VolumeGroupNaming
{
    /// <summary>第 <paramref name="index"/> 卷在**这一族里**的标准名字（补缺卷名用）。</summary>
    public static string CanonicalNameFor(VolumeNameFamily family, string baseName, int index)
    {
        switch (family)
        {
            case VolumeNameFamily.ZipSpanned when index == 1:
            case VolumeNameFamily.RarOld when index == 1:
                return baseName + (family == VolumeNameFamily.ZipSpanned ? ".zip" : ".rar");

            case VolumeNameFamily.ZipSpanned:
                return baseName + ".z" + (index - 1).ToString("D2", CultureInfo.InvariantCulture);

            case VolumeNameFamily.RarOld:
                return baseName + ".r" + (index - 2).ToString("D2", CultureInfo.InvariantCulture);

            case VolumeNameFamily.PartNumbered:
                return baseName + ".part" + index.ToString(CultureInfo.InvariantCulture) + ".rar";

            default:
                // 数字族的**基名**由调用方另算（保留归档后缀那一段，见 TryBuildCanonicalVolumeNameForIndex）。
                return baseName + "." + index.ToString("D3", CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// **同一族里、第 <paramref name="index"/> 卷该叫什么名字** —— 由**同族的一份样本名**（例：入口那一卷
    /// `111-L2.7z.001`）定族（2026-10-13 困难批"缺片按字节数补槽位"那一档用）。
    ///
    /// <para>⛔ 族规则**只有一处**（<see cref="CanonicalNameFor"/>），这里只把"从样本名定族"这一步接上，
    /// ⛔ 不新写第二套命名规则。推出来的名字**自己再验一遍**（<see cref="LegacyVolumeNames.TryGetIndex"/>
    /// 必须回同一个卷号）。判不出族 / 样本名推不出本族第 1 卷名 / 自验不过 ⇒ <c>false</c>（调用方什么都不做）。</para>
    /// </summary>
    public static bool TryBuildCanonicalVolumeNameForIndex(
        string sampleVolumeName,
        int index,
        out string canonical)
    {
        canonical = string.Empty;

        if (index < 1 || string.IsNullOrWhiteSpace(sampleVolumeName))
        {
            return false;
        }

        /*
         * ===== 通用分片族（对归档再分片）：`X.zip.001` 这一族**就往回写成 `X.zip.NNN`** =====
         * （设计文档 §6.6 A / §10 第 2 条；用户 2026-10-17 现场：真跑日志逐字
         *  `222-L1.zip.001：分卷名不标准，已按标准名改好（3 卷）：222-L1.zip.001 → 222-L1.z01；
         *   …003 → 222-L1.zip` —— 那是 7-Zip `-v` 切出来的**通用分片**，被当成跨盘 zip 族改名，
         *   之后引擎按 `.zip` 去读 EOCD ⇒ 盘号写的是 0 ⇒ 认成"单盘 zip" ⇒ 整组解不出来（①红）。
         *
         * ⚠ 名字里**夹了杂质也算这一族**（设计文档 §2 的容忍档；现场 `6666`：
         *   `打包111-L2.zip.删除001` —— 数字前面的「删除」必须剥掉再认，否则这一片永远改不回
         *   `.zip.001`、整组永远报「分卷压缩包缺少必要分卷」）。
         */
        System.Text.RegularExpressions.Match genericSplit = System.Text.RegularExpressions.Regex.Match(
            sampleVolumeName,
            @"(?i)^(?<base>.+)\.zip\.(?<tail>[^\\/]*)$");

        if (genericSplit.Success)
        {
            System.Text.RegularExpressions.Match trailing = System.Text.RegularExpressions.Regex.Match(
                genericSplit.Groups["tail"].Value,
                @"^(?<junk>.*?)(?<num>\d{1,3})$");

            if (trailing.Success)
            {
                string genericBase = genericSplit.Groups["base"].Value;

                if (genericBase.Length > 0)
                {
                    canonical = genericBase
                        + ".zip."
                        + index.ToString("D3", System.Globalization.CultureInfo.InvariantCulture);

                    return true;
                }
            }
        }

        string baseName = FileNameHelper.SecondStepBaseName(sampleVolumeName);
        string? first = LegacyVolumeNames.TryGetFirstVolumeName(sampleVolumeName);

        if (baseName.Length == 0 || string.IsNullOrWhiteSpace(first))
        {
            return false;
        }

        VolumeNameFamily family = ResolveFamilyFromFirstVolumeName(first!, baseName);

        /*
         * ⚠ 2026-10-14（夹具 + 环台两处实测，用户当场点破的"丢族段"）：**数字族的基名要另算** ——
         * 与 <see cref="TryGetCanonicalVolumeName"/> 里那条**既有规则同一套**（那儿写着：
         * `x.7z.001` 的规范名是 `x.7z.001` 而不是 `x.001`，因为 `GetArchiveBaseName` 会把 `.7z` 也剥掉）。
         * 这里原来没照做 ⇒ "补第 2 槽"给出 `pack111-L2.002`，可同组的片叫 `pack111-L2.7z.002`
         * ⇒ 引擎按名字找不到、组永远凑不齐（环台 `3333` 那次是 `111-L2.002` vs `111-L2.7z.001`）。
         */
        if (family == VolumeNameFamily.Numeric)
        {
            int markDot = first!.LastIndexOf('.');

            if (markDot > 0)
            {
                baseName = first![..markDot];
            }
        }

        string name = CanonicalNameFor(family, baseName, index);

        if (name.Length == 0 || LegacyVolumeNames.TryGetIndex(name) != index)
        {
            return false;
        }

        canonical = name;

        return true;
    }

    /// <summary>
    /// **这一片按它自己那一族写成的规范卷名** ——「还原成规范名」这件事在**单卷尺度**上的唯一出口。
    ///
    /// <para><b>为什么必须有它</b>（真机 FFFF 2026-10-10 夜，定案到行）：接片那一档
    /// （<c>ExtractionCoordinator.TryAdoptUnresolvedVolumePiece</c>）原来把规范名**写死成跨盘 ZIP 的盘名**
    /// （`盘序 = 卷号 - 1` ⇒ `.zip` / `.z0N`）。RAR 新式分卷的片一进那条路就被改成
    /// `111_.z01` / `111_.z02` …（真机与守门夹具的日志里逐字可见：「已按规范卷名 111_.z01 接到…」），
    /// 于是**族被改掉了**：入口那一单还在找 `111_.part1.rar`，盘上却只有 `111_.z01`
    /// ⇒ 整组永远凑不齐 ⇒ 四片各自去解 ⇒ 全批没有一个成功。
    /// 守门 `ChainInnerVolumeRestoreTests.真机布局_四个外层包各在一个子目录里_四片分处四地_整组照样凑齐并解开`。</para>
    ///
    /// <para>判据 = 既有那几把尺子的组合（<see cref="LegacyVolumeNames.TryGetFirstVolumeName"/> 定族、
    /// <see cref="LegacyVolumeNames.TryGetIndex"/> 取卷号、<see cref="CanonicalNameFor"/> 写名字），
    /// ⛔ 这里不新造第二套族规则。判不出族 / 卷号 ⇒ 返回 <c>false</c>（调用方照旧什么都不做）。</para>
    /// </summary>
    public static bool TryGetCanonicalVolumeName(string fileName, out string canonical)
    {
        canonical = string.Empty;

        string baseName = FileNameHelper.SecondStepBaseName(fileName);
        int? index = LegacyVolumeNames.TryGetIndex(fileName);
        string? first = LegacyVolumeNames.TryGetFirstVolumeName(fileName);

        if (baseName.Length == 0 || index == null || string.IsNullOrWhiteSpace(first))
        {
            return false;
        }

        VolumeNameFamily family = ResolveFamilyFromFirstVolumeName(first, baseName);

        /*
         * ⚠ **数字族（`x.7z.001` / `x.001`）的基名要另算**：`GetArchiveBaseName` 会把归档后缀也剥掉
         * （`x.7z.001` → `x`），而数字族的规范名是"在**归档名**后面接数字"（`x.7z.001`，不是 `x.001`）。
         * 探针实测（2026-10-10）：拿 `x` 当基名会算出 `x.001`，于是"规范名自比"对
         * **标准的 7z 分卷名判 false** —— 谁拿它当闸门，谁就会把好名字判成脏名字。
         * 数字族的基名 = **只去掉最后那一段（卷号段）**，归档后缀留着。
         */
        if (family == VolumeNameFamily.Numeric)
        {
            int dot = fileName.LastIndexOf('.');

            if (dot > 0)
            {
                baseName = fileName[..dot];
            }
        }

        canonical = CanonicalNameFor(family, baseName, index.Value);

        return canonical.Length > 0;
    }

    /// <summary>
    /// 这一卷的名字是不是"7-Zip 按原名认得的标准卷名"。
    ///
    /// <para>⛔ 卷标记段里**一点垃圾都不许有**：<c>x.7z.001删除</c> 与 <c>111</c> 都不算 ——
    /// 它们按原名交给 7-Zip 就是打不开，删了就是不可逆的数据丢失。</para>
    /// </summary>
    public static bool IsCanonicalVolumeName(string fileName, string baseName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        if (string.Equals(fileName, baseName + ".zip", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(fileName, baseName + ".rar", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string[] parts = fileName.Split('.');

        if (parts.Length < 2)
        {
            return false;
        }

        // x.part1.rar：卷标记在倒数第二段
        if (parts.Length >= 3 &&
            parts[^1].Equals("rar", StringComparison.OrdinalIgnoreCase) &&
            IsCleanVolumeSegment(parts[^2]))
        {
            return true;
        }

        return IsCleanVolumeSegment(parts[^1]);
    }

    /// <summary>这一段是不是"整段就是卷标记、后面一点垃圾都没有"。</summary>
    private static bool IsCleanVolumeSegment(string segment) =>
        ExtensionHelper.TrySplitVolumeSegment(segment, out string mark, out string junk)
        && junk.Length == 0
        && mark.Length > 0
        && mark.Equals(segment, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 这个名字是不是**已在位的那一卷的另一个名字**（用户 2026-09-27 真机：`222.zip` 与 `222.zi删除p`）。
    ///
    /// <para>判据只有一条：把"被塞了杂质的后缀"还原（唯一出口
    /// <see cref="ExtensionHelper.TryRecoverDisguisedArchiveBody"/>），还原结果与某一卷**规范名逐字相等**
    /// ⇒ 它就是那一卷的名字变体，**不占独立卷号、也不构成"缺了一卷"的证据**
    /// （老判据把它当"没有卷号的候选"推定进边界槽，于是同一句话里既说"缺 `222.zip`"、
    /// 又说那个文件"就是第 1 卷"，整层定稿作废）。</para>
    ///
    /// <para>⛔ 变体**不会**让那一卷变成"可以删"：它进 <c>GroupFilePaths</c> 与
    /// <c>VariantFilePaths</c>，而可删资格仍由磁盘上的真名字回答（<see cref="IsCanonicalVolumeName"/>）
    /// —— 名字不标准就一个字节都不许删。</para>
    /// </summary>
    public static bool TryMatchVolumeVariant(
        string? fileName,
        IReadOnlyList<ResolvedVolume> located,
        out bool disguisedName)
    {
        disguisedName = false;

        if (string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        // ⛔ 只喂**末尾那一段**（`222.zi删除p` 的 `zi删除p`）：还原函数回答的是"这一段是什么后缀"，
        //    整条文件名进去只会把它当成一个含点的名字，永远还原不出来（写成这样踩过一次）。
        string segment = Path.GetExtension(fileName).TrimStart('.');

        /*
         * ⛔ 只有"归档本体那一档"（不带卷标记的名字）才走这条：带卷标记的照旧走老路 ——
         * 那一档的名字变体（`x.7z.001删除`）本来就是"缺卷证据"的来源，动它等于拆红线。
         */
        if (ExtensionHelper.IsVolumeSegment(segment))
        {
            return false;
        }

        if (!ExtensionHelper.TryRecoverDisguisedArchiveBody(segment, out string suffix, out _))
        {
            return false;
        }

        /*
         * ⛔ 只认**本体那一族**（`zip` / `rar`）：它们的卷号 1 就是"名字里带本体后缀"的那一份，
         * 所以"同基名段 + 同后缀"只可能指同一个逻辑卷。其余后缀（`7z` / `tar` / 数字族…）的本体
         * 是"卷号 1"，而 `x.7z.002` 这种**带卷标记**的名字本来就不走这条（上面已经挡掉），
         * 万一漏进来也不是"另一个名字"，不许当变体（认错组比不认糟得多）。
         */
        if (!suffix.Equals("zip", StringComparison.OrdinalIgnoreCase)
            && !suffix.Equals("rar", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        foreach (ResolvedVolume volume in located)
        {
            string volumeName = Path.GetFileName(volume.Path);
            string volumeStem = StemOf(volumeName);

            if (!string.Equals(volumeStem, StemOf(fileName), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            /*
             * 只比"本体那一档"的规范名（`x.zip` / `x.rar`）：还原出来的后缀是 `zip` 时，
             * 只有 `…zip` 这一位算它的家；`x.z01` / `x.7z.001` 各有各的族，⛔ 不许跨族认亲
             * （跨族认亲会让"另一套命名的第一卷"被误判成"同名的变体"，那比不认糟得多）。
             *
             * ⚠ 在位的这一卷**自己的名字也可能不标准**（真机里那一卷叫 `222.zi删除p`）——
             * 那也照样认：判据是"两个文件是同一个卷号上的两个名字"，不是"在位那个必须标准"。
             * 它们两个都留在保护名单里，可删资格仍由磁盘上的真名字回答（都不标准 ⇒ 一个都不删）。
             */
            if (volumeName.Equals(volumeStem + "." + suffix, StringComparison.OrdinalIgnoreCase))
            {
                // 这个名字本身是不是这一族的规范写法（`222.zip` 是、`222.zi删除p` 不是）。
                disguisedName = !string.Equals(fileName, volumeStem + "." + suffix, StringComparison.Ordinal);
                return true;
            }
        }

        return false;
    }

    /// <summary>这个文件的"基名段"：去掉最后一段后缀。无后缀时就是原名本身。</summary>
    public static string StemOf(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return string.Empty;
        }

        string stem = Path.GetFileNameWithoutExtension(fileName);

        return stem.Length == 0 ? fileName : stem;
    }

    /// <summary>组名族（决定卷序怎么算、缺失名怎么补、哪个位子的规范名不带卷号）。</summary>
    public static VolumeNameFamily ResolveFamily(VolumeGroup group, string baseName)
    {
        foreach (VolumeCandidate volume in group.Volumes)
        {
            string? first = LegacyVolumeNames.TryGetFirstVolumeName(Path.GetFileName(volume.Path));

            if (string.IsNullOrWhiteSpace(first))
            {
                continue;
            }

            return ResolveFamilyFromFirstVolumeName(first, baseName);
        }

        return VolumeNameFamily.Numeric;
    }

    /// <summary>
    /// 由"**这一族第一卷的名字** + 基名"定族 —— 族规则的唯一一处
    /// （<see cref="ResolveFamily"/> 与 <see cref="TryGetCanonicalVolumeName"/> 都转调它）。
    /// </summary>
    private static VolumeNameFamily ResolveFamilyFromFirstVolumeName(string first, string baseName)
    {
        if (first.Equals(baseName + ".zip", StringComparison.OrdinalIgnoreCase))
        {
            return VolumeNameFamily.ZipSpanned;
        }

        if (first.Equals(baseName + ".rar", StringComparison.OrdinalIgnoreCase))
        {
            return VolumeNameFamily.RarOld;
        }

        if (first.StartsWith(baseName + ".part", StringComparison.OrdinalIgnoreCase))
        {
            return VolumeNameFamily.PartNumbered;
        }

        return VolumeNameFamily.Numeric;
    }
}
