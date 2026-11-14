using ArchiveFixer.Helpers;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace ArchiveFixer.Detection;

/// <summary>
/// 失败模型 —— 一组分卷"缺哪几卷 / 完不完整 / 该怎么对用户说"，以及这三件事的唯一算法。
///
/// <para><b>卷总数推不出来，就不推</b>：这些命名都只有"第几卷"没有"共几卷"，
/// 所以 <see cref="VolumeGroup.ExpectedVolumeCount"/> 恒为 0、<see cref="VolumeGroup.IsComplete"/>
/// 只管"从 1 开始连续无缺号"。缺号（<c>.001</c>+<c>.003</c>）能确定，就列进
/// <see cref="MissingVolumeNames"/>；最大卷号之后的缺号无法得知，⛔ 不猜。</para>
///
/// <para><b>为什么独立成层</b>：缺卷清单、完整度与给用户的一句话必须是**同一份结论**的三面 ——
/// 清单说缺 1 卷、文案却说"按顺序找到 3 卷"这类打架，会让用户没法判断该不该动手。
/// 文案里"缺几卷 / 缺哪几卷 / 另有超出编号上限的缺号 / 没有第 1 卷本体"四个信息点全部由这里给。</para>
///
/// <para><b>失败模型</b>：该族表示不了某个卷号 ⇒ 记"有无法命名的缺号"（不编一个假名字）；
/// 没有第 1 卷本体 ⇒ 完整度 false，并在文案里点名"只能从哪一卷起手"。⛔ 绝不抛。</para>
///
/// <para>生命周期：一组一份不可变结果；只读桶事实与卷清单，不查盘、不调引擎。</para>
/// </summary>
internal sealed class VolumeGroupGap
{
    /// <summary>Note 里最多列几个缺卷名，超出的用"等"收尾（完整清单看 <see cref="MissingVolumeNames"/>）。</summary>
    private const int MaxMissingNamesInNote = 5;

    private VolumeGroupGap(
        IReadOnlyList<string> missingVolumeNames,
        bool startsAtFirstVolume,
        bool hasUnnamableHole,
        bool isComplete)
    {
        MissingVolumeNames = missingVolumeNames;
        StartsAtFirstVolume = startsAtFirstVolume;
        HasUnnamableHole = hasUnnamableHole;
        IsComplete = isComplete;
    }

    /// <summary>缺的卷文件名（只有文件名，不含目录）；空表示 1..最大卷号之间没有洞。</summary>
    public IReadOnlyList<string> MissingVolumeNames { get; }

    /// <summary>已找到的最小卷号是不是 1。</summary>
    public bool StartsAtFirstVolume { get; }

    /// <summary>有缺号超出该命名的编号上限（例如数字族超过 999），给不出文件名。</summary>
    public bool HasUnnamableHole { get; }

    /// <summary>卷序从 1 开始连续、没有缺号（总数未知，不代表"整包一定齐"）。</summary>
    public bool IsComplete { get; }

    /// <summary>按桶里的事实算出缺卷清单与完整度。</summary>
    public static VolumeGroupGap Assess(VolumeGroupBucket bucket, IReadOnlyList<int> indices)
    {
        // 补齐缺失文件名时用的命名模板（基名/族/尾巴/补零位宽都取自桶里的解析结果）。
        var naming = new VolumeNameInfo
        {
            BaseName = bucket.BaseName,
            Family = bucket.Family,
            Tail = bucket.Tail,
            DigitWidth = bucket.DigitWidth
        };

        // 缺号只需要在 [1, 最大已找到卷号] 里找：再往后有没有卷，命名里没有答案。
        var missing = new List<string>();
        bool hasUnnamableHole = false;

        for (int index = 1; index < indices[^1]; index++)
        {
            if (bucket.ContainsIndex(index))
            {
                continue;
            }

            if (naming.TryFormat(index, out string missingName))
            {
                missing.Add(missingName);
            }
            else
            {
                hasUnnamableHole = true;
            }
        }

        bool startsAtFirstVolume = indices[0] == 1;
        bool isComplete = startsAtFirstVolume && missing.Count == 0 && !hasUnnamableHole;

        return new VolumeGroupGap(missing, startsAtFirstVolume, hasUnnamableHole, isComplete);
    }

    /// <summary>
    /// 给用户的一句话：先说"总数不知道"，再说找到几卷 / 缺哪几卷，
    /// 最后在"只能用 .z01 / .r00 启动"这种危险情况下额外点名 —— 别指望用户自己去读字段。
    /// </summary>
    public string Describe(VolumeGroupBucket bucket, IReadOnlyList<VolumeCandidate> volumes)
    {
        var note = new StringBuilder();

        note.Append("无法确定分卷总数，");

        if (volumes.Count == 1)
        {
            note.Append("只找到 1 卷，可能不完整");
        }
        else
        {
            note.Append("按顺序找到 ").Append(volumes.Count).Append(" 卷");
        }

        if (MissingVolumeNames.Count > 0)
        {
            note.Append("，缺 ").Append(MissingVolumeNames.Count).Append(" 卷：");
            note.Append(string.Join("、", MissingVolumeNames.Take(MaxMissingNamesInNote)));

            if (MissingVolumeNames.Count > MaxMissingNamesInNote)
            {
                note.Append(" 等");
            }
        }

        if (HasUnnamableHole)
        {
            note.Append("；另有缺号超出该命名的编号上限，无法给出文件名");
        }

        // 本体缺失：x.zip / x.rar 才是第 1 卷，没有它整组解不开，必须说清"现在会从哪一卷起手"。
        if (!StartsAtFirstVolume
            && (bucket.Family == VolumeNameFamily.ZipSpanned || bucket.Family == VolumeNameFamily.RarOld))
        {
            string bodyName = bucket.Family == VolumeNameFamily.ZipSpanned
                ? bucket.BaseName + ".zip"
                : bucket.BaseName + ".rar";

            note.Append("；没找到第 1 卷本体 ").Append(bodyName)
                .Append("，只能从 ").Append(FileNameHelper.GetFileName(volumes[0].Path))
                .Append(" 起手");
        }

        return note.ToString();
    }
}
