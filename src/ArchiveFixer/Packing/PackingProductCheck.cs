using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ArchiveFixer.Packing;

/// <summary>
/// 打包的**产物校验**（<see cref="PackingService"/> 的协作类，职责：校验）。
///
/// <para>两条判据都在这里，且只有这一份：</para>
/// <list type="bullet">
/// <item><description><b>分卷</b>：只认磁盘上真有的 <c>&lt;源名&gt;.7z.NNN</c>（不自己拼名字），
/// 并检查连续性 —— 缺卷要报"缺哪几个"，不是笼统说一句（不变量 7）。</description></item>
/// <item><description><b>外层容器</b>：把列条目的结论翻成"装的是不是那几个分卷"（不变量 6：
/// 对不上就**不是成功**）。</description></item>
/// </list>
///
/// <para>⛔ 这里不删任何东西：核对不通过时外层容器**刻意留着**（它确实存在、也可能对用户有用，
/// 删掉不可逆），由用户在看到结论后自己决定。</para>
/// </summary>
internal static class PackingProductCheck
{
    /// <summary>
    /// 把外层容器的列条目结论翻成校验结论。
    ///
    /// <para>列不出来 ⇒ 直接判不通过（"列不出条目"不等于"装对了"）；列得出来才去比对。
    /// ⚠ 追加改口径：没有中间文件夹了 → 归档里就是**顶层的那几个分卷**
    /// （<c>folderName</c> 传空 = 不要求目录层）。</para>
    /// </summary>
    /// <param name="listing">列条目那一步的结论。</param>
    /// <param name="outerName">句子里指代外层容器的名字（<c>外层 rar</c> / <c>外层 7z</c>）。</param>
    /// <param name="outerTool">干这件事的工具名（<c>Rar.exe</c> / <c>7-Zip</c>）。</param>
    /// <param name="outerIsRar">外层是不是 rar（决定用哪个解析器与哪个产物名）。</param>
    /// <param name="volumes">磁盘上实际切出来的分卷。</param>
    internal static PackingVerification FromListingStep(
        PackStepResult listing,
        string outerName,
        string outerTool,
        bool outerIsRar,
        IReadOnlyList<PackingVolume> volumes)
    {
        if (!listing.Success)
        {
            return new PackingVerification
            {
                Ok = false,
                Detail = $"列不出{outerName}的条目（{outerTool} 退出码 {listing.ExitCode}），没法确认它装的是什么。"
                         + PackingEngineSteps.DescribeOutputTail(listing),
                Problems = new[] { "列条目失败" }
            };
        }

        return PackingVerifier.Verify(
            outerIsRar
                ? PackingVerifier.ParseBareList(listing.StandardOutput)
                : PackingVerifier.ParseSevenZipSltList(listing.StandardOutput),
            string.Empty,
            volumes,
            outerIsRar ? "rar" : "7z");
    }

    /// <summary>按 <c>&lt;源名&gt;.7z.NNN</c> 从落点目录里数出实际切出来的分卷（不自己拼名字）。</summary>
    internal static List<PackingVolume> CollectVolumes(PackingPlan plan)
    {
        var volumes = new List<PackingVolume>();

        try
        {
            if (!Directory.Exists(plan.OutputFolder))
            {
                return volumes;
            }

            foreach (string file in Directory.EnumerateFiles(plan.OutputFolder))
            {
                string fileName = Path.GetFileName(file);

                if (!fileName.StartsWith(plan.VolumeBaseName + ".", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string suffix = fileName[(plan.VolumeBaseName.Length + 1)..];

                if (suffix.Length == 0 || !suffix.All(char.IsDigit))
                {
                    continue;
                }

                long bytes = 0;

                try
                {
                    bytes = new FileInfo(file).Length;
                }
                catch
                {
                    bytes = 0;
                }

                volumes.Add(new PackingVolume
                {
                    FileName = fileName,
                    Path = file,
                    Index = int.TryParse(suffix, out int index) ? index : 0,
                    Bytes = bytes
                });
            }
        }
        catch
        {
            // 数不出来就返回已经数到的那些：这里只影响"报告里写几个"，不该把成功判成失败。
        }

        return volumes
            .OrderBy(v => v.FileName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// 分卷连续性检查（不变量 7 的口径：缺卷要报"缺哪几个"，不是笼统说一句）。
    /// 返回空串 = 通过。
    /// </summary>
    internal static string ValidateVolumes(IReadOnlyList<PackingVolume> volumes, PackingPlan plan)
    {
        if (volumes.Count == 0)
        {
            return $"7-Zip 报成功，但 {plan.OutputFolder} 里一个分卷都没有 —— 产物不对，这次不算成功。";
        }

        var missing = new List<string>();

        for (int expected = 1; expected <= volumes.Count; expected++)
        {
            if (volumes.All(v => v.Index != expected))
            {
                missing.Add($"{plan.VolumeBaseName}.{expected:000}");
            }
        }

        return missing.Count == 0
            ? string.Empty
            : $"7-Zip 切出来的分卷不连续，缺少：{string.Join('、', missing)}。"
              + "产物不完整，这次不算成功。";
    }

    /// <summary>
    /// 中途停下时如实报"现在切出来几个卷、重试前该清哪些文件"。
    ///
    /// <para>⛔ 这里以前写"重试前请先清空这个文件夹" —— 分卷现在直接落在落点目录里，
    /// 清整个目录会把用户自己放在那里的文件一起删掉。只能点名清掉本次生成的未完成分卷。</para>
    /// </summary>
    internal static string DescribePartialVolumes(int count, PackingPlan plan) =>
        $"分卷情况：落点目录里现在有 {count} 个分卷（可能不完整）；"
        + "重试前请先删掉这几个未完成的分卷文件，落点目录里的其他文件不要动。";

    /// <summary>装分卷的那个文件夹的名字（给用户看的短名字；取不到时给一句人话）。</summary>
    internal static string FolderName(PackingPlan plan)
    {
        string name = Path.GetFileName(
            (plan.OutputFolder ?? string.Empty).TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        return string.IsNullOrWhiteSpace(name) ? "落点目录" : name;
    }
}
