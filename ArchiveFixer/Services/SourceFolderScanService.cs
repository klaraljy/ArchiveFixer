using System;
using System.IO;
using ArchiveFixer.Detection;
using ArchiveFixer.Extraction;
using ArchiveFixer.Helpers;

namespace ArchiveFixer.Services
{
    /// <summary>
    /// 场景 B 判定所需的那一次目录扫描的结论（规格 <c>docs/输出与整理模型.md</c> §3.3）。
    ///
    /// <para>
    /// <see cref="IsCandidate"/> = "包基名 == 所在目录名"，这一步**不碰磁盘**（纯字符串），
    /// 不成立时连目录都不用扫 —— 场景 A（<c>111\222\</c> 下几十上百个包，包名与目录名不同）
    /// 是最常见的形态，不该为它付出一次目录枚举。
    /// </para>
    /// </summary>
    public sealed class SourceFolderScanResult
    {
        /// <summary>包基名与所在目录名相同（塌缩规则的**前提**）。</summary>
        public bool IsCandidate { get; init; }

        /// <summary>是否真的扫了目录（只有候选才扫）。</summary>
        public bool Scanned { get; init; }

        /// <summary>目录里除本包（含它自己的分卷）之外的归档数。</summary>
        public int OtherArchiveCount { get; init; }

        /// <summary>第一层条目总数（只用于日志与排障）。</summary>
        public int EntryCount { get; init; }

        /// <summary>目录里是不是只有这一个归档（塌缩规则的**结论**）。</summary>
        public bool ContainsOnlyThisArchive { get; init; }

        /// <summary>中文说明，可直接进日志。</summary>
        public string Message { get; init; } = string.Empty;
    }

    /// <summary>
    /// 「包所在目录里是不是只有这一个归档」的扫描（规格 §3.3 场景 B 的调用方一侧）。
    ///
    /// <para>
    /// 为什么要单独一个服务、而不是塞进 <see cref="OutputPlacement"/>：
    /// <see cref="OutputPlacement"/> 是**纯函数**（只做字符串推理，不碰文件系统，这是它的硬约束），
    /// 而"目录下还有没有别的包"只有真去列目录才知道。所以事实由这里查出来，
    /// 再作为一个布尔告知 <c>ResolveDestinationDirectory</c> —— 那边只负责按规则算落点。
    /// </para>
    /// <para>
    /// ⚠ <b>本类会枚举目录，属于磁盘活</b>：调用方必须放在后台线程上跑
    /// （用户场景里一个目录可能有几百个条目，网络盘上更慢）。见
    /// <c>ExtractionCoordinator.ExtractSingleTaskAsync</c> 里的 <c>Task.Run</c>。
    /// </para>
    /// <para>
    /// 「别的包」怎么认：按后缀（归档后缀 / 分卷标记 / 伪装后缀里套着归档后缀）。
    /// 认不出的文件（说明 txt、视频等）**不算**别的包 —— 规格 §3.3 的判据是"目录下只有这一个**包**"，
    /// 不是"只有这一个文件"；把说明文件也算进去会让这条规则在真实目录里永远不生效。
    /// 本包**自己的分卷**（<c>名字.r00</c> / <c>名字.7z.002</c> / <c>名字.part2.rar</c>）也不算别的包，
    /// 判据直接复用既有且已测的 <see cref="VolumeGroupDetector.BelongsToSameGroup"/>，不另写一套分卷命名规则。
    /// </para>
    /// </summary>
    public static class SourceFolderScanService
    {
        /// <summary>
        /// 包基名是否等于它所在目录的名字（纯字符串判断，不碰磁盘）。
        ///
        /// 包基名取 <see cref="OutputPlacement.ResolveArchiveBaseName"/> 的结果 ——
        /// <c>名字.rar</c> / <c>名字.7z.001</c> / <c>名字.part1.rar</c> 都得到 <c>名字</c>
        /// （规格 §7 决策 D-6：包基名只有一处实现）。
        /// </summary>
        public static bool IsRepeatedFolderNameCandidate(string? sourceArchivePath)
        {
            string directory = FileNameHelper.GetDirectoryName(SafeFullPath(sourceArchivePath));

            if (string.IsNullOrWhiteSpace(directory))
            {
                return false;
            }

            string directoryName = FileNameHelper.GetFileName(directory);
            string baseName = OutputPlacement.ResolveArchiveBaseName(sourceArchivePath);

            return !string.IsNullOrWhiteSpace(directoryName)
                   && !string.IsNullOrWhiteSpace(baseName)
                   && string.Equals(directoryName, baseName, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 扫一遍包所在目录，回答"是不是只有这一个归档"。
        ///
        /// 目录读不到（权限 / 被占用 / 不存在）时返回 <c>ContainsOnlyThisArchive = false</c>：
        /// 塌缩是"少一层目录"的优化，读不到就**不塌缩**（多一层是安全的默认，规格 §3.1 判定表 2）。
        /// </summary>
        public static SourceFolderScanResult Inspect(string? sourceArchivePath)
        {
            if (!IsRepeatedFolderNameCandidate(sourceArchivePath))
            {
                return new SourceFolderScanResult
                {
                    IsCandidate = false,
                    Message = "包基名与所在目录名不同，不适用塌缩规则"
                };
            }

            string sourceFull = SafeFullPath(sourceArchivePath);
            string directory = FileNameHelper.GetDirectoryName(sourceFull);
            string sourceFileName = FileNameHelper.GetFileName(sourceFull);

            int otherArchives = 0;
            int entries = 0;

            try
            {
                foreach (string file in Directory.EnumerateFiles(directory))
                {
                    entries++;

                    string fileName = FileNameHelper.GetFileName(file);

                    if (string.Equals(fileName, sourceFileName, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (!LooksLikeArchive(fileName))
                    {
                        continue;
                    }

                    // 本包自己的分卷不算"别的包"（分卷命名只由 VolumeGroupDetector 判定）。
                    if (VolumeGroupDetector.BelongsToSameGroup(sourceFileName, fileName))
                    {
                        continue;
                    }

                    otherArchives++;

                    // 只要发现一个别的包就够了，不必把大目录走完。
                    break;
                }
            }
            catch (Exception ex)
            {
                return new SourceFolderScanResult
                {
                    IsCandidate = true,
                    Scanned = false,
                    ContainsOnlyThisArchive = false,
                    Message = $"无法枚举源包所在目录（{ex.Message}），本次不塌缩重复的一层"
                };
            }

            bool onlyThisArchive = otherArchives == 0;

            return new SourceFolderScanResult
            {
                IsCandidate = true,
                Scanned = true,
                OtherArchiveCount = otherArchives,
                EntryCount = entries,
                ContainsOnlyThisArchive = onlyThisArchive,
                Message = onlyThisArchive
                    ? $"包名与目录同名，且目录里只有这一个包，将塌缩重复的一层：{directory}"
                    : $"包名与目录同名，但目录里还有别的包（{otherArchives} 个），保留完整一层以免产物混淆：{directory}"
            };
        }

        /// <summary>按后缀判断一个文件像不像归档（见类注释里"别的包怎么认"）。</summary>
        private static bool LooksLikeArchive(string fileName)
        {
            string extension = Path.GetExtension(fileName);

            if (ExtensionHelper.IsKnownArchiveExtension(extension)
                || ExtensionHelper.IsVolumePartExtension(extension))
            {
                return true;
            }

            // 伪装后缀（222.rar.jpg / 222.7z.001.bak）：再往里看一层，剥掉伪装段后还是归档才算。
            if (ExtensionHelper.IsSuspiciousFakeExtension(extension))
            {
                string innerExtension = Path.GetExtension(Path.GetFileNameWithoutExtension(fileName));

                return ExtensionHelper.IsKnownArchiveExtension(innerExtension)
                       || ExtensionHelper.IsVolumePartExtension(innerExtension);
            }

            return false;
        }

        private static string SafeFullPath(string? path)
        {
            return SafePathHelper.GetFullPathSafe(path);
        }
    }
}
