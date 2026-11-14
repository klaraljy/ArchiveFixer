using System.Globalization;

namespace ArchiveFixer.Detection;

/// <summary>
/// 一个文件名的解析结果：属于哪一族、第几卷、基名是什么，以及"这一族的第 N 卷该叫什么名字"的唯一写法。
///
/// <para><b>为什么独立成层</b>：卷序、基名与规范卷名是三条互相咬合的事实（<c>.z01</c> / <c>.r00</c> 的卷序
/// 都不是后缀里的数字、<c>x.zip</c> / <c>x.part1.rar</c> 的"第 1 卷"形状也不同），而缺卷清单、名字重建、
/// 改名这几条路都要用**同一套族规则**。把族规则钉在一份解析结果上，消费方就不会各写一份
/// （各写一份的后果：缺卷名说 <c>x.001</c>、盘上却叫 <c>x.7z.001</c>）。</para>
///
/// <para><b>失败模型</b>：解析端认不出 ⇒ 返回 <c>null</c>（判不出，不猜）；
/// <see cref="TryFormat"/> 只在"这一族表示不了这个卷号"时返回 <c>false</c> —— 绝不抛、绝不写一个假名字出去。</para>
///
/// <para>生命周期：一次判定内的不可变值对象（<c>init</c> 只写），无状态；只做字符串运算，
/// 不碰文件系统、不读内容、不调引擎。</para>
/// </summary>
internal sealed class VolumeNameInfo
{
    public string BaseName { get; init; } = string.Empty;

    public VolumeNameFamily Family { get; init; }

    /// <summary>绝对卷序，1 起。</summary>
    public int Index { get; init; }

    /// <summary>是不是"本体"（<c>x.zip</c> / <c>x.rar</c>）。本体只有在本族的后续卷存在时才算数。</summary>
    public bool IsBody { get; init; }

    /// <summary>分卷段后面固定挂的尾巴（目前只有 <c>.rar</c>，例如 <c>x.part1.rar</c> / <c>x.001.rar</c>）。</summary>
    public string Tail { get; init; } = string.Empty;

    /// <summary><c>partN</c> 的数字位宽（<c>part01</c> → 2），用来让补出来的缺失名保持同样的补零风格。</summary>
    public int DigitWidth { get; init; } = 1;

    /// <summary>是不是"名字被伪装过"的分卷标记（夹了垃圾 `001删除`、或标记后面挂点段 `001.txt`）。</summary>
    public bool IsDisguised { get; init; }

    /// <summary>
    /// 这个名字是不是"**本体后缀被塞了垃圾**"（<c>222.zi删除p</c> / <c>222.zscip</c>）。
    ///
    /// <para>它与"干净的本体"（<c>222.zip</c>）**一样算分卷成员**：真机里那一组的另一个成员
    /// 恰恰就是 <c>222.zip</c>，靠它才认得出"这是一组"（只有一对光杆本体时不许成组 ——
    /// 一份单独的 <c>.zip</c> 后面还有没有卷，名字给不出答案）。</para>
    /// </summary>
    public bool IsDisguisedBody { get; init; }

    /// <summary>
    /// 这个名字是不是**这一族的规范名、一字不差**（<c>x.zip</c> / <c>x.rar</c> / <c>x.z01</c> / <c>x.part1.rar</c>）。
    ///
    /// <para>⛔ 靠容错/骨架**还原**出来的（<c>222.zi删除p</c> → <c>zip</c>、<c>222.z0sc1</c> → <c>z01</c>）
    /// 一律为 false —— 它只用来决定"同一个卷号上有两个文件时先认谁"（规范名优先，见
    /// <see cref="VolumeGroupBucket.Add"/>），⛔ 不许拿它当"可以删"的资格（那个唯一出口是
    /// <c>VolumeGroupResolver.CanEnterDeletableRestItems</c>）。</para>
    /// </summary>
    public bool HasCanonicalName { get; init; }

    /// <summary>按本族命名规则写出"第 index 卷"的文件名；该族表示不了这个卷号时返回 false。</summary>
    public bool TryFormat(int index, out string fileName)
    {
        fileName = string.Empty;

        if (index < 1)
        {
            return false;
        }

        switch (Family)
        {
            case VolumeNameFamily.Numeric:
                if (index > 999)
                {
                    return false;
                }

                fileName = BaseName + "." + index.ToString("D3", CultureInfo.InvariantCulture) + Tail;
                return true;

            case VolumeNameFamily.ZipSpanned:
                if (index == 1)
                {
                    fileName = BaseName + ".zip";
                    return true;
                }

                if (index - 1 > 99)
                {
                    return false;
                }

                fileName = BaseName + ".z" + (index - 1).ToString("D2", CultureInfo.InvariantCulture);
                return true;

            case VolumeNameFamily.RarOld:
                if (index == 1)
                {
                    fileName = BaseName + ".rar";
                    return true;
                }

                if (index - 2 > 99)
                {
                    return false;
                }

                fileName = BaseName + ".r" + (index - 2).ToString("D2", CultureInfo.InvariantCulture);
                return true;

            default:
                fileName = BaseName + ".part" + index.ToString("D" + DigitWidth, CultureInfo.InvariantCulture) + Tail;
                return true;
        }
    }
}
