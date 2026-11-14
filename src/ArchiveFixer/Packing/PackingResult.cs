using ArchiveFixer.Models;
using ArchiveFixer.Storage;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ArchiveFixer.Packing
{
    /// <summary>
    /// 一次打包的结果。
    ///
    /// <para><b>不变量 6 的落点</b>：只有 <see cref="PackingState.Succeeded"/> 才算成功。
    /// 失败 / 取消 / 产物校验不通过一律不是成功，界面上也就不会显示成功。</para>
    /// </summary>
    public sealed class PackingResult
    {
        public PackingState State { get; init; } = PackingState.Failed;

        /// <summary>成功（只有 Succeeded 才是 true）。</summary>
        public bool Success => State == PackingState.Succeeded;

        /// <summary>取消。</summary>
        public bool Cancelled => State == PackingState.Cancelled;

        /// <summary>结论那一句话（成功时是"打包成功"，失败时是原因）。</summary>
        public string Message { get; init; } = string.Empty;

        /// <summary>失败 / 取消时给用户看的那句更具体的话（成功时为空）。</summary>
        public string FailureReason { get; init; } = string.Empty;

        /// <summary>本次用的外层容器（<see cref="PackOuterContainer.None"/> = 按要求没做外层）。</summary>
        public PackOuterContainer OuterContainer { get; init; } = PackOuterContainer.Rar;

        /// <summary>
        /// 外层产物的全路径（rar 或 7z）—— **实际**产出的那一个；
        /// 不做外层（或失败 / 取消）时为 null。
        /// </summary>
        public string? OuterPath { get; init; }

        /// <summary>外层产物的字节数（拿不到时 0）。</summary>
        public long OuterBytes { get; init; }

        /// <summary>有没有外层产物（界面据此决定显不显示那一行路径）。</summary>
        public bool HasOuterArtifact => !string.IsNullOrWhiteSpace(OuterPath);

        /// <summary>产物校验的说明（列了几条、对不对得上）。</summary>
        public string VerificationDetail { get; init; } = string.Empty;

        /// <summary>
        /// 收尾那一句（第 46 条）：原包怎么处理了、其余物怎么处理了
        /// （例如 <c>原包：不动；其余物：已彻底删除</c>）。没走收尾时是空串。
        /// </summary>
        public string CleanupNote { get; init; } = string.Empty;

        /// <summary>分卷清单（名字 + 各自大小）。失败与取消时是"已经切出来的那几卷"。</summary>
        public IReadOnlyList<PackingVolume> Volumes { get; init; } = Array.Empty<PackingVolume>();

        /// <summary>逐步骤日志行（界面结果区与主日志共用；**保证不含密码明文**）。</summary>
        public IReadOnlyList<string> LogLines { get; init; } = Array.Empty<string>();

        /// <summary>耗时。</summary>
        public TimeSpan Elapsed { get; init; }

        /// <summary>分卷总字节数。</summary>
        public long TotalVolumeBytes => Volumes.Sum(v => v.Bytes);

        /// <summary>第一个分卷的全路径（用来在结论里给出"实际产物在哪"）；没有分卷时是空串。</summary>
        public string FirstVolumePath => Volumes.Count > 0 ? Volumes[0].Path : string.Empty;

        /// <summary>
        /// 装分卷的那个文件夹（= 「其余物」）的全路径；没走到那一步时是空串。
        ///
        /// <para>只用来把结论里的路径说清楚 —— ⛔ 第 46 条之前结论里写的是旧模型的说法
        /// "结果就是 B 里的 N 个分卷"，而界面上早就没有 "B" 了。</para>
        /// </summary>
        public string VolumesFolder { get; init; } = string.Empty;

        /// <summary>那个文件夹的名字（结论里给人看的短名字）。</summary>
        public string VolumesFolderName
        {
            get
            {
                if (string.IsNullOrWhiteSpace(VolumesFolder))
                {
                    return "落点目录";
                }

                try
                {
                    string name = Path.GetFileName(
                        VolumesFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

                    return string.IsNullOrWhiteSpace(name) ? "落点目录" : name;
                }
                catch
                {
                    return "落点目录";
                }
            }
        }

        /// <summary>
        /// 结果区那一句话：**实际产物路径 + 容器类型** + 分卷数（失败时给出原因）。
        ///
        /// <para>为什么把容器类型写进结论：外层容器现在由程序按本机工具自动定（`rar` 或 `7z`），
        /// 只说一句"完成"会让他分不清拿到的是 <c>.rar</c> 还是 <c>.7z</c>，也分不清"没做外层"
        /// 是按要求做的还是出错了 —— 结论必须能被机器与人都一眼判定（不变量 6、14）。</para>
        /// </summary>
        public string Describe()
        {
            if (State == PackingState.Succeeded)
            {
                if (!OuterContainer.HasOuterArtifact())
                {
                    return $"打包成功（外层容器：{OuterContainer.ShortName()}）：结果就是「{VolumesFolderName}」里的 "
                         + $"{Volumes.Count} 个 7z 加密分卷（共 {TaskSpaceEstimate.FormatSize(TotalVolumeBytes)}），"
                         + $"第一个是 {FirstVolumePath}；按要求没有外层容器文件。";
                }

                return $"打包成功（外层容器：{OuterContainer.ShortName()}）：{OuterPath}"
                     + $"（{TaskSpaceEstimate.FormatSize(OuterBytes)}），里面是 {Volumes.Count} 个 7z 加密分卷"
                     + (string.IsNullOrWhiteSpace(VerificationDetail) ? "。" : $"；{VerificationDetail}");
            }

            if (State == PackingState.Cancelled)
            {
                return string.IsNullOrWhiteSpace(FailureReason)
                    ? StatusText.PackCancelled
                    : StatusText.PackCancelled + "：" + FailureReason;
            }

            return string.IsNullOrWhiteSpace(FailureReason) ? Message : FailureReason;
        }
    }
}
