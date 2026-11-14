using ArchiveFixer.Detection;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;

namespace ArchiveFixer.Extraction;

/// <summary>
/// **试开证据**：把候选按假设名做成硬链接、备好试开目录、读文件大小、收工清场。
///
/// <para><b>⛔ 绝不复制文件</b>：一卷 2 GiB，复制一组要几十 GiB 和几分钟。这里一律用
/// <c>CreateHardLinkW</c> 在**同一卷**上建第二个名字（零字节、瞬时）。硬链接不能跨卷 ——
/// 这条物理事实决定了"能不能试开"只能由工作区根与第一卷是否同卷回答（见 <see cref="VolumeProbeCandidates"/>）。</para>
///
/// <para><b>本层只碰文件系统里"证据物"</b>：试开目录、硬链接、文件大小；⛔ 不调引擎、⛔ 不判结果、
/// ⛔ 不改源文件/源目录。删除只删试开目录里的链接，源文件一个字节不动。</para>
///
/// <para>生命周期：纯静态、无状态；失败一律返回 null / false + 原话，绝不抛给试开流程。</para>
/// </summary>
internal static class VolumeProbeEvidence
{
    /// <summary>试拼用的跨盘 zip 基名（与 <c>VolumeContentInference.ProbeFileName</c> 同一用意：名字只是给引擎看的）。</summary>
    private const string ProbeZipNamePrefix = "volprobe.z";

    private const string ProbeZipTailName = "volprobe.zip";

    /// <summary>
    /// 试开用的硬链接名：**按卡上的族拼**（用户 2026-10-17：「底层识别先，名字配合行动」）。
    ///
    /// <para>现场（新样本 `1111`）：RAR 组的试开一律用 `ProbeFileName`（7z 数字族 `.001`）建链接，
    /// 引擎自然报「分卷压缩包缺少必要分卷」⇒ 整组永远判成"试开没通过"。
    /// ⇒ RAR 族按 `<probe>.partN.rar` 建链接；7z/认不出照旧用数字族。</para>
    /// </summary>
    private static string ProbeLinkName(VolumeContentFormat family, int index, int count)
    {
        return family switch
        {
            // RAR partN 族：WinRAR 的口径 = 补零宽度跟着**总卷数的位数**走（1~9 卷 part1，10~99 卷 part01）。
            VolumeContentFormat.Rar =>
                "volprobe.part" + index.ToString(
                    new string('0', Math.Max(1, count.ToString(CultureInfo.InvariantCulture).Length)),
                    CultureInfo.InvariantCulture) + ".rar",

            // 通用分片族（对归档再分片）：引擎认 `<基名>.zip.001` 这种形状（设计文档 §6.6 A）。
            VolumeContentFormat.Zip => "volprobe.zip." + index.ToString("D3", CultureInfo.InvariantCulture),

            // 7z 数字族（`volprobe.7z.001`）与判不出族的兜底：沿用既有出口。
            _ => VolumeContentInference.ProbeFileName(index)
        };
    }

    /// <summary>
    /// 建试开目录；返回 false 时 <paramref name="error"/> = 原始异常消息（由失败模型拼成给人看的一句话）。
    /// </summary>
    internal static bool TryCreateDirectory(string directory, out string error)
    {
        try
        {
            Directory.CreateDirectory(directory);
            error = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// 按假设名把这几卷硬链接进 <paramref name="directory"/>（目录必须已存在）；
    /// 返回 false 时 <paramref name="failure"/> = 给人看的一句话。
    /// </summary>
    internal static bool TryLinkVolumes(
        IReadOnlyList<VolumeCandidate> volumes,
        string directory,
        VolumeContentFormat family,
        out string firstLink,
        out string failure)
    {
        firstLink = string.Empty;
        failure = string.Empty;

        for (int i = 0; i < volumes.Count; i++)
        {
            string linkPath = Path.Combine(directory, ProbeLinkName(family, i + 1, volumes.Count));

            if (!CreateHardLinkW(linkPath, volumes[i].Path, IntPtr.Zero))
            {
                int error = Marshal.GetLastWin32Error();

                failure = $"做不了硬链接（Win32 错误 {error}）—— 源目录与试开目录必须在同一个卷上，且文件系统要支持硬链接";
                return false;
            }

            if (i == 0)
            {
                firstLink = linkPath;
            }
        }

        return true;
    }

    /// <summary>
    /// 把整组按"第 1 片 … 末片"硬链接成**通用分片名**（<c>volprobe.zip.001…</c>），
    /// 返回入口链接（第 1 片）。做不出链接返回 <c>null</c>。
    ///
    /// <para>重复调用会先把上一次的链接删掉 —— 试第二个顺序时，同一个名字必须换成另一片。</para>
    /// </summary>
    internal static string? LinkAsNumberedZip(IReadOnlyList<string> orderedVolumes, string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }

            Directory.CreateDirectory(directory);
        }
        catch
        {
            return null;
        }

        string entry = string.Empty;

        for (int i = 0; i < orderedVolumes.Count; i++)
        {
            string linkPath = Path.Combine(
                directory,
                "volprobe.zip." + (i + 1).ToString("D3", CultureInfo.InvariantCulture));

            if (!CreateHardLinkW(linkPath, orderedVolumes[i], IntPtr.Zero))
            {
                return null;
            }

            if (i == 0)
            {
                entry = linkPath;
            }
        }

        return entry;
    }

    /// <summary>
    /// 把整组按"第 1 片 … 末片"硬链接成**标准跨盘卷名**（<c>volprobe.z01…volprobe.zip</c>），
    /// 返回末片那个链接（引擎要拿它当入口）。做不出链接返回 <c>null</c>。
    ///
    /// <para>重复调用会先把上一次的链接删掉 —— 试第二个顺序时，同一个名字必须换成另一片。</para>
    /// </summary>
    internal static string? LinkAsSpannedZip(IReadOnlyList<string> orderedVolumes, string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }

            Directory.CreateDirectory(directory);
        }
        catch
        {
            return null;
        }

        string tail = string.Empty;

        for (int i = 0; i < orderedVolumes.Count; i++)
        {
            bool isTail = i == orderedVolumes.Count - 1;
            string linkPath = Path.Combine(
                directory,
                isTail
                    ? ProbeZipTailName
                    : ProbeZipNamePrefix + (i + 1).ToString("D2", CultureInfo.InvariantCulture));

            if (!CreateHardLinkW(linkPath, orderedVolumes[i], IntPtr.Zero))
            {
                return null;
            }

            if (isTail)
            {
                tail = linkPath;
            }
        }

        return tail;
    }

    /// <summary>文件字节数（读不到给 0，绝不抛）。</summary>
    internal static long SizeOf(string? path)
    {
        try
        {
            return string.IsNullOrWhiteSpace(path) ? 0 : new FileInfo(path).Length;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>删试开目录（含里面所有硬链接）。⛔ 删的是链接，源文件一个字节不动。</summary>
    internal static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch
        {
            // 留在盘上只是脏一点，绝不能因为清理失败把结论吞掉。
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLinkW(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);
}
