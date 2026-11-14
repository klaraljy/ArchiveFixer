using System;
using System.IO;

namespace ArchiveFixer.Detection;

/// <summary>
/// 内容解码（第一层证据）：只读文件头 8 字节，回答"这份内容是什么归档"与"它该叫什么后缀"。
///
/// <para><b>为什么靠内容</b>（用户 2026-09-28 真机）：三个 7z 卷被改成 <c>amb909.7.01</c> /
/// <c>amb909.z.2</c> / <c>amb909..3</c>，后缀与卷号全不可信；只有魔数没有骗人。
/// ⛔ 本类只看魔数：不解析归档、不看名字、不信后缀。</para>
///
/// <para><b>物理事实</b>：7z 的 <c>-v</c> 分卷**只有第一卷**带魔数，之后的卷是裸字节流 ——
/// 所以"认不出格式"本身就是"它可能是续卷"的必要条件，这个判断被候选池与形状读数直接引用。</para>
///
/// <para><b>失败模型</b>：读不出来（不存在 / 被占 / 权限 / 目录）一律
/// <see cref="VolumeContentFormat.Unknown"/>、后缀给空串，**绝不抛** ——
/// 调用方由此退回"按名字"的老行为，不产生副作用。</para>
///
/// <para>生命周期：纯静态、无状态；每次调用现开现关（<see cref="FileShare.ReadWrite"/> +
/// <see cref="FileShare.Delete"/>，不挡住别的进程改名/删除）。</para>
/// </summary>
internal static class VolumeContentSniffer
{
    /// <summary>
    /// 读文件头判断格式。读不出来（不存在 / 被占 / 权限）一律 <see cref="VolumeContentFormat.Unknown"/>，**绝不抛**。
    /// </summary>
    public static VolumeContentFormat SniffFormat(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return VolumeContentFormat.Unknown;
        }

        try
        {
            using var stream = new FileStream(
                filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

            Span<byte> head = stackalloc byte[8];
            int read = stream.Read(head);

            if (read >= 6
                && head[0] == 0x37 && head[1] == 0x7A && head[2] == 0xBC
                && head[3] == 0xAF && head[4] == 0x27 && head[5] == 0x1C)
            {
                return VolumeContentFormat.SevenZip;
            }

            if (read >= 7
                && head[0] == (byte)'R' && head[1] == (byte)'a' && head[2] == (byte)'r'
                && head[3] == (byte)'!' && head[4] == 0x1A && head[5] == 0x07)
            {
                return VolumeContentFormat.Rar;
            }

            if (read >= 4
                && head[0] == (byte)'P' && head[1] == (byte)'K'
                && (head[2] == 0x03 || head[2] == 0x05 || head[2] == 0x07))
            {
                return VolumeContentFormat.Zip;
            }
        }
        catch
        {
            // 读不到头部就是"看不出来"：调用方会退回原来的按名字行为，不会有副作用。
        }

        return VolumeContentFormat.Unknown;
    }

    /// <summary>内容对应的标准后缀（不含点）；认不出来返回空串。</summary>
    public static string ExtensionFor(VolumeContentFormat format)
    {
        switch (format)
        {
            case VolumeContentFormat.SevenZip:
                return "7z";
            case VolumeContentFormat.Rar:
                return "rar";
            case VolumeContentFormat.Zip:
                return "zip";
            default:
                return string.Empty;
        }
    }
}
