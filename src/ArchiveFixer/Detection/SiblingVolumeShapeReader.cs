using ArchiveFixer.Helpers;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ArchiveFixer.Detection;

/// <summary>
/// 证据建模：读同目录这一组**兄弟卷的形状**（方案 §4 阶段 B 的 (e)）——
/// 基名逐字相同 / 卷标记连续 / 除末片外等大。
///
/// <para><b>为什么这是一层独立的证据</b>：一串文件里"有数字"从来不是证据（<c>风景01</c> 里也有 <c>01</c>）。
/// 只有当同目录真能配出一组**形状自洽**的兄弟卷时，后缀段里的数字才允许参与"谁是首卷 / 怎么排"的判断；
/// ⛔ 基名正文里的数字**永远不构成任何证据**（只看 <see cref="LegacyVolumeNames.TryGetIndex"/>
/// 认出来的卷标记段）。⛔ 而且 <see cref="SiblingVolumeShape.SelfConsistent"/> 只是**闸门**：
/// 它证明"这组名字自洽"，不证明"这几片就是一组" —— 成不成立仍然只由硬链接试开回答。</para>
///
/// <para><b>失败模型</b>：拿不到名字 / 同目录清单 / 成员不足 / 撞号 / 缺片 / 体积不像一组 ⇒
/// 返回 <see cref="SiblingVolumeShape.SelfConsistent"/> = <c>false</c> 的读数并在
/// <see cref="SiblingVolumeShape.Blocker"/> 写清哪一条没成立，**绝不抛**。
/// 本类只读名字与体积，不试开、不建目录、不改名字、不调引擎。</para>
///
/// <para>生命周期：纯静态、无状态；一次调用内自己收成员、自己判定，不缓存读数。</para>
/// </summary>
internal static class SiblingVolumeShapeReader
{
    /// <summary>
    /// 读同目录这一组**兄弟卷的形状**（方案 §4 阶段 B 的 (e)：它是一条**闸门**，不是结论）。
    ///
    /// <para>三条同时成立才算 <see cref="SiblingVolumeShape.SelfConsistent"/>：</para>
    /// <list type="number">
    /// <item><description><b>基名逐字相同</b>：每个成员的**基名**（<see cref="FileNameHelper.SecondStepBaseName"/>，
    /// 第一个点之前 —— 第二大步那份）
    /// 与锚点逐字相等，且成员至少两个（只有一个成员谈不上"一组"）。</description></item>
    /// <item><description><b>卷标记连续</b>：自报卷标记的成员互不撞号、从 <c>1</c> 起，
    /// 而名字丢了的那几片正好把剩下的卷位补齐（<c>N = 自报卷标记的个数 + 名字丢了的个数</c>）。</description></item>
    /// <item><description><b>除末片外等大</b>：至多只有一个成员的体积与其余所有成员不同，而且它更小
    /// （⛔ 不要求末片更小 —— 实测"末卷更小"只成立一半，见 <c>AGENTS.md</c> §11.4）。</description></item>
    /// </list>
    ///
    /// <para><b>为什么"名字丢了的成员"只能算基名逐字相同的那几个</b>：真机形状是
    /// <c>111.7z.001</c> / <c>111</c>（本该叫 <c>111.7z.002</c>）/ <c>111.7z.003</c> —— 名字整个丢了之后
    /// 剩下的正是**基名本身**。名字不沾边的文件（<c>readme.txt</c>）进不了这一组，
    /// 目录里躺着别的包也不会被拖进来。⛔ 这一条是纯名字 + 体积的事实，不试开、不调引擎。</para>
    ///
    /// <para>⛔ 与 <see cref="VolumeContentSniffer.SniffFormat"/> 的关系：名字丢了的成员必须**认不出归档魔数**
    /// （认得出就说明它是另一个独立的包，不是这一组的续卷）—— 候选池本来就按这一条筛过，
    /// 这里再筛一次是为了让"直接喂一个目录清单"的调用方也拿到同一个答案。</para>
    /// </summary>
    /// <param name="anchorPath">锚点（7z 里 = 带起始头的那一卷，也就是第 1 卷）。</param>
    /// <param name="filesInDirectory">同目录的候选（路径 + 大小；拿不到就传 null ⇒ 判不出）。</param>
    /// <param name="archiveExtension">该族的规范归档后缀（7z 数字族传 <c>7z</c>）。</param>
    public static SiblingVolumeShape ReadSiblingShape(
        string? anchorPath,
        IEnumerable<VolumeCandidate>? filesInDirectory,
        string archiveExtension)
    {
        var none = new SiblingVolumeShape
        {
            SelfConsistent = false,
            Blocker = "拿不到这一卷的信息（读不出名字或同目录清单）"
        };

        string? anchorBase = TryResolvePackageBaseName(anchorPath, archiveExtension);

        if (string.IsNullOrWhiteSpace(anchorPath) || anchorBase == null)
        {
            return none;
        }

        string directory = DirectoryOf(anchorPath);

        if (directory.Length == 0)
        {
            return none;
        }

        // 成员候选：同一层 + 包基名与锚点逐字相等。
        var members = new List<VolumeCandidate>();

        foreach (VolumeCandidate candidate in filesInDirectory ?? Array.Empty<VolumeCandidate>())
        {
            if (candidate == null || string.IsNullOrWhiteSpace(candidate.Path) || candidate.Size <= 0)
            {
                continue;
            }

            if (!string.Equals(DirectoryOf(candidate.Path), directory, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string? baseName = TryResolvePackageBaseName(candidate.Path, archiveExtension);

            if (baseName != null && string.Equals(baseName, anchorBase, StringComparison.OrdinalIgnoreCase))
            {
                members.Add(candidate);
            }
        }

        if (!members.Any(m => SamePath(m.Path, anchorPath)))
        {
            long anchorSize = VolumeFileFacts.SizeOf(anchorPath);

            if (anchorSize <= 0)
            {
                return none;
            }

            members.Add(new VolumeCandidate { Path = anchorPath!, Size = anchorSize });
        }

        if (members.Count < 2)
        {
            return new SiblingVolumeShape
            {
                BaseName = anchorBase,
                MemberCount = members.Count,
                SelfConsistent = false,
                Blocker = "同目录里只有这一份的基名对得上，配不成一组"
            };
        }

        var named = new List<(int Marker, VolumeCandidate Candidate)>();
        var fillers = new List<VolumeCandidate>();

        foreach (VolumeCandidate member in members)
        {
            int? marker = LegacyVolumeNames.TryGetIndex(VolumeFileFacts.SafeFileName(member.Path));

            if (marker is int number)
            {
                named.Add((number, member));

                continue;
            }

            // 名字丢了的那几片：必须认不出归档魔数（认得出 = 另一个独立的包，不是这一组的续卷）。
            if (VolumeContentSniffer.SniffFormat(member.Path) == VolumeContentFormat.Unknown)
            {
                fillers.Add(member);
            }
        }

        if (named.Count == 0)
        {
            return new SiblingVolumeShape
            {
                BaseName = anchorBase,
                MemberCount = members.Count,
                NamelessFillers = fillers,
                SelfConsistent = false,
                Blocker = "这一组里没有一份的名字自报卷标记（谁都不说自己排第几）"
            };
        }

        var markers = named.Select(n => n.Marker).OrderBy(m => m).ToList();

        if (markers.Distinct().Count() != markers.Count || markers[0] != 1)
        {
            return new SiblingVolumeShape
            {
                BaseName = anchorBase,
                MemberCount = members.Count,
                Markers = markers,
                NamelessFillers = fillers,
                SelfConsistent = false,
                Blocker = "这一组的卷标记不连续（撞号，或者不是从 1 起）"
            };
        }

        int slots = named.Count + fillers.Count;

        if (markers[^1] > slots)
        {
            return new SiblingVolumeShape
            {
                BaseName = anchorBase,
                MemberCount = members.Count,
                Markers = markers,
                NamelessFillers = fillers,
                SelfConsistent = false,
                Blocker = "自报卷标记比手上的片数还大（中间还缺着几卷，那几卷的名字无从知道）"
            };
        }

        var missing = Enumerable.Range(1, slots).Where(slot => !markers.Contains(slot)).ToList();

        // 除末片外等大：至多一份的体积与其余不同，而且它更小。
        // ⚠ 并列时取**更大**的那个当"满片尺寸"：满片是重复出现的那个尺寸，末片只会更小 ——
        // 两份各出现一次时（两卷形状）取小的会把满片当成"多余的那一份"。
        List<long> sizes = members.Select(m => m.Size).OrderBy(s => s).ToList();
        long common = sizes
            .GroupBy(s => s)
            .OrderByDescending(g => g.Count())
            .ThenByDescending(g => g.Key)
            .First()
            .Key;

        bool sizePattern = sizes.Count(s => s == common) >= sizes.Count - 1
                           && sizes.All(s => s == common || s < common);

        if (!sizePattern)
        {
            return new SiblingVolumeShape
            {
                BaseName = anchorBase,
                MemberCount = members.Count,
                Markers = markers,
                MissingMarkers = missing,
                NamelessFillers = fillers,
                SelfConsistent = false,
                Blocker = "这一组里有不止一份的体积与其余不同（看不出哪一份是末片）"
            };
        }

        return new SiblingVolumeShape
        {
            BaseName = anchorBase,
            MemberCount = members.Count,
            Markers = markers,
            MissingMarkers = missing,
            NamelessFillers = fillers,
            SelfConsistent = true
        };
    }

    /// <summary>包基名（**第二大步那份**：<see cref="FileNameHelper.SecondStepBaseName"/>）；判不出返回 null。</summary>
    private static string? TryResolvePackageBaseName(string? path, string archiveExtension)
    {
        _ = archiveExtension;

        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        string baseName = FileNameHelper.SecondStepBaseName(path);

        return baseName.Length > 0 ? baseName : null;
    }

    private static string DirectoryOf(string? path)
    {
        try
        {
            return string.IsNullOrWhiteSpace(path) ? string.Empty : Path.GetDirectoryName(path) ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool SamePath(string? a, string? b) =>
        !string.IsNullOrWhiteSpace(a)
        && !string.IsNullOrWhiteSpace(b)
        && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
