using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace ArchiveFixer.Detection;

/// <summary>
/// **卷名拼法与名字重建的唯一出口**（用户 2026-09-29 要求：卷名拼法只留**一个**出口）：
/// 由"基名（不含归档后缀）+ 族 + 卷数"拼出整组标准名，以及从用户给的文件名推"名字前缀"。
///
/// <para>RAR 族带 <c>.rar</c> 尾巴（<c>part1.rar</c>……）；7z 是数字族（<c>.7z.001</c>……）；
/// 跨盘 zip 是 <c>.z01</c>……+ 末片 <c>.zip</c>（7-Zip 实测认这一族：改名成它之后
/// <c>7z l</c> 能报出 <c>Multivolume = +</c> 并把里面的文件列出来）。</para>
///
/// <para>⛔ 名字在这里只用于**重建**（改名计划与标准名快通道），⛔ 不参与"这一份是不是分卷、是第几卷"
/// 的判定 —— 那两条证据在内容读数（<see cref="VolumeNumberReading"/>）与
/// <see cref="VolumeNumberGroupResolver"/> 里；本类只回答"这一组叫成什么名字"。</para>
///
/// <para>生命周期：纯静态、无状态；只做字符串拼装，不枚举目录、不改名字、不碰文件系统。</para>
/// </summary>
internal static class VolumeStandardNames
{
    /// <summary>
    /// **卷名拼法的唯一出口**：由"基名（不含归档后缀）+ 族 + 卷数"拼出整组标准名。
    ///
    /// <para>RAR 族带 <c>.rar</c> 尾巴（<c>part1.rar</c>……）；7z 是数字族（<c>.7z.001</c>……）；
    /// 跨盘 zip 是 <c>.z01</c>……+ 末片 <c>.zip</c>（7-Zip 实测认这一族：改名成它之后
    /// <c>7z l</c> 能报出 <c>Multivolume = +</c> 并把里面的文件列出来）。</para>
    /// </summary>
    public static IReadOnlyList<string> BuildStandardNames(string? stem, VolumeNamingFamily family, int count)
    {
        if (string.IsNullOrWhiteSpace(stem) || count < 1)
        {
            return Array.Empty<string>();
        }

        var names = new List<string>(count);

        switch (family)
        {
            case VolumeNamingFamily.SevenZipNumbered:
                for (int index = 1; index <= count; index++)
                {
                    names.Add(stem + ".7z." + index.ToString("D3", CultureInfo.InvariantCulture));
                }

                break;

            /*
             * ===== 通用分片族（对归档再分片）：`<基名>.zip.001` …… =====
             * 用户 2026-10-17 现场（新样本 `1111` 日志逐字）：`-tzip -v` 切出来的 3 片被当成跨盘 zip 族
             * 改名成 `222-L1.z01 / .z02 / .zip` ⇒ 引擎按 `.zip` 读 EOCD、盘号写的是 0 ⇒ 认成单盘 zip
             * ⇒ 整组解不出来。⇒ 这一族的规范名**就是它自己的形状**（设计文档 §6.6 A / §10 第 2 条）：
             * 卷号三位补零、档案尾巴 `.zip` 留在基名里。
             */
            case VolumeNamingFamily.ZipNumbered:
                if (count < 2)
                {
                    return Array.Empty<string>();
                }

                for (int index = 1; index <= count; index++)
                {
                    names.Add(stem + ".zip." + index.ToString("D3", CultureInfo.InvariantCulture));
                }

                break;

            case VolumeNamingFamily.RarPart:
                /*
                 * 卷号补零宽度按"总卷数有几位"定：1~9 卷是 part1.rar，10~99 卷是 part01.rar……
                 * 这是 WinRAR 自己的口径（卷数多到要两位以上才补零），照着它写才不会造出它不认的名字。
                 */
                int width = count.ToString(CultureInfo.InvariantCulture).Length;

                for (int index = 1; index <= count; index++)
                {
                    names.Add(
                        stem + ".part" + index.ToString("D" + width.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture) + ".rar");
                }

                break;

            case VolumeNamingFamily.ZipSpanned:
                /*
                 * 跨盘 zip 的族：末片叫 .zip，之前的片叫 .z01/.z02……
                 * 只有 1 片时不存在"跨盘组"，返回空 —— 调用方因此拒绝，不会造出一个假的组。
                 */
                if (count < 2)
                {
                    return Array.Empty<string>();
                }

                for (int index = 1; index < count; index++)
                {
                    names.Add(stem + ".z" + index.ToString("D2", CultureInfo.InvariantCulture));
                }

                names.Add(stem + ".zip");

                break;

            default:
                return Array.Empty<string>();
        }

        return names;
    }

    /// <summary>
    /// 推**名字前缀**（写出来的规范卷名里卷标记之前的那一段）：<c>amb909.7.01</c> → <c>amb909</c>、
    /// <c>amb909.7z.001</c> → <c>amb909.7z</c>。
    ///
    /// <para>⚠ 2026-10-23（用户口径）：这是**名字重建**，不是基名 —— 基名只有两份
    /// （<see cref="Helpers.FileNameHelper.FirstStepBaseName"/> / <see cref="Helpers.FileNameHelper.SecondStepBaseName"/>，
    /// 第一个点之前）。判据整体在 <see cref="Helpers.ExtensionHelper.ResolveNameStem"/>（唯一实现）。</para>
    ///
    /// <para>⛔ 推不出来（名字里认不出或含非法字符）就返回 false，绝不硬剪。</para>
    /// </summary>
    public static bool TryDeriveStem(string? filePath, VolumeContentFormat format, out string stem)
    {
        stem = Helpers.ExtensionHelper.ResolveNameStem(
            Helpers.FileNameHelper.GetFileName(filePath),
            VolumeContentInference.ExtensionFor(format));

        return stem.Length > 0 && stem.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
    }
}
