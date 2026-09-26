using ArchiveFixer.Storage;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace ArchiveFixer.Packing
{
    /// <summary>
    /// 一个分卷（7-Zip 自己切出来的那一个文件）。
    ///
    /// <para>名字由 7-Zip 产生（<c>-v</c> → <c>名字.7z.001/.002/…</c>），
    /// 这里只**认**磁盘上的结果，不自己拼 —— 自己拼会在"内容物恰好是分卷大小的整数倍"
    /// 这种边界上多切一卷出来（docs/打包功能.md §2）。</para>
    /// </summary>
    public sealed class PackingVolume
    {
        /// <summary>文件名（含 <c>.7z.001</c> 这一段，不带目录）。</summary>
        public string FileName { get; init; } = string.Empty;

        /// <summary>全路径。</summary>
        public string Path { get; init; } = string.Empty;

        /// <summary>分卷序号（<c>.001</c> → 1）；认不出来时是 0。</summary>
        public int Index { get; init; }

        /// <summary>字节数（读不到时是 0）。</summary>
        public long Bytes { get; init; }

        public override string ToString()
        {
            return $"{FileName}（{TaskSpaceEstimate.FormatSize(Bytes)}）";
        }
    }

    /// <summary>
    /// 一次打包的**规划**：源文件夹量出来的大小 → 分卷大小 → 预计几个分卷 → 需要多少空间。
    ///
    /// <para>纯计算 + 落点校验，**不碰进程、不写盘**（落点校验只读文件系统的元数据）。
    /// 之所以把它从 <see cref="PackingService"/> 里分出来：这些数字是界面上要给用户看的摘要
    /// （内容大小 / 预计分卷数 / 需要空间），必须在按下「开始打包」之前就能算出来 ——
    /// 而真正开跑时要用的判断与界面用的是**同一份**，不能各算一套。</para>
    ///
    /// <para><b>分卷数为什么是"预计"</b>：7z 先压缩再切卷，卷数取决于**压缩后**的大小。
    /// 内容是不可压缩的资源（视频 / 图片 / 已压过的包）时与 <see cref="ContentBytes"/> 同量级，
    /// 所以按内容大小算出来的是**上界**（用户给的两个例子 1 GB → 2 卷、1.5 GB → 3 卷，
    /// 正是这个口径）。可压缩的内容会切出更少的卷 —— 这是对的方向，不是出错。</para>
    /// </summary>
    public sealed class PackingPlan
    {
        /// <summary>默认分卷大小：512 MiB（用户两个例子的唯一自洽解，docs/打包功能.md §2）。</summary>
        public const long DefaultVolumeSizeBytes = 512L * 1024 * 1024;

        /// <summary>界面允许的分卷大小下限。</summary>
        public const long MinVolumeSizeBytes = 64L * 1024 * 1024;

        /// <summary>界面允许的分卷大小上限。</summary>
        public const long MaxVolumeSizeBytes = 4096L * 1024 * 1024;

        /// <summary>
        /// 服务层的硬下限（1 MiB）。比界面下限松，理由有两条，都不是"给测试开后门"：
        /// ① <c>7z -v</c> 的单位是 MiB，比 1 MiB 更小的值没有意义（<c>-v0m</c> 是无效参数）；
        /// ② 64 MiB 是**给人的推荐范围**（挡住"手滑填 1"），而"这台机器上真的能不能跑通"
        /// 要靠端到端验证 —— 验收要求用 1 MiB 跑一次真 7z（docs/打包功能.md §9 第 3 条），
        /// 服务层如果按 64 MiB 硬拒，那条验收就永远跑不了。
        /// </summary>
        public const long HardMinVolumeSizeBytes = 1024 * 1024;

        /// <summary>
        /// 空间需求系数：内容物自己一份（×1.02 的压缩后估算）＋ 外层 rar 里再存一份。
        ///
        /// <para>与 docs/打包功能.md §6 的最后一行同一口径：<c>内容大小 × 1.02 + 内容大小</c> = ×2.02。
        /// 取 1.02 是因为 7z 建出来的分卷总量**可能略大于**内容（容器头 + 分卷边界），
        /// 空间判断宁可高估（与 <c>SpaceChecker</c> 同一条价值观：不敢说够）。</para>
        /// </summary>
        public const double SpaceFactor = 2.02d;

        /// <summary>
        /// 不做外层容器时的系数：只算分卷那一份（内容 × 1.02）。
        /// 与 <see cref="SpaceFactor"/> 同一个 1.02 的由来（容器头 + 分卷边界可能略大于内容）。
        /// </summary>
        public const double ContentOnlySpaceFactor = 1.02d;

        /// <summary>额外的固定余量（外层 rar 的头、目录项、以及别处同时在写的零碎）。</summary>
        public const long SpaceMarginBytes = 32L * 1024 * 1024;

        /// <summary>源文件夹 A（全路径）——**真正要打包的那个文件夹**（单文件时是新建的同名文件夹）。</summary>
        public string SourceFolder { get; init; } = string.Empty;

        /// <summary>A 的目录名（分卷名、rar 名都用它；由**源**的名字来，不是让位后的名字）。</summary>
        public string SourceFolderName { get; init; } = string.Empty;

        /// <summary>
        /// 用户选中的那个路径（文件夹或**单个文件**）与它的解析结果（用户 2026-09-26 第 46 条）。
        /// </summary>
        public PackingSourceResolution SourceResolution { get; init; } = new();

        /// <summary>
        /// 单文件时要**新建**的那个同名文件夹（其余物）；文件夹源时是空串。
        /// 服务层负责建它并把源文件放进去（同盘硬链接优先，跨盘才复制）。
        /// </summary>
        public string WrapperFolderToCreate => SourceResolution.WrapperFolderToCreate;

        /// <summary>落点目录（本地 = 源旁边；指定位置 = 用户选的目录）。</summary>
        public string TargetDirectory { get; init; } = string.Empty;

        /// <summary>原包操作 / 其余物操作 / 落点（第 46 条；⛔ 与解压那套完全独立）。</summary>
        public PackingRunOptions RunOptions { get; init; } = new();

        /// <summary>目标卷数（&lt;1 GiB → 2、≥1 GiB → 3）。</summary>
        public int VolumeTargetCount { get; init; } = PackingVolumeRule.SmallTargetCount;

        /// <summary>
        /// 分卷落在哪个目录（全路径）—— 与 <see cref="TargetDirectory"/> **同一个目录**。
        ///
        /// <para>⚠ **2026-09-26 追加改口径**（用户原话："7z 分卷文件外面好像不用再套一件文件夹了，
        /// 有点太多余了，这样也不用去考虑文件名重复的问题了"）：以前分卷会先落进一个**以源名命名的
        /// 中间文件夹**（也就是"其余物"），现在**不再套那一层** —— 分卷直接落在落点目录里，
        /// 外层容器里装的也是这些分卷（顶层，没有文件夹层）。
        /// 于是"其余物"= **切出来的那些分卷文件**（单文件时再加上那个临时建的同名文件夹）。</para>
        /// </summary>
        public string OutputFolder { get; init; } = string.Empty;

        /// <summary>
        /// 分卷的基路径（全路径，<c>&lt;落点&gt;\&lt;源名&gt;.7z</c>，撞名时让位成 <c>源名(1).7z</c>）；
        /// 实际的卷名由 7z 在它后面加 <c>.001</c>。
        ///
        /// <para>⛔ 撞名判据看的是**真正的第一卷**（<c>…001</c>）在不在 —— 只测 `源名.7z` 是测不出来的，
        /// 7z 会直接把已有的分卷覆盖掉。</para>
        /// </summary>
        public string VolumeBasePath { get; init; } = string.Empty;

        /// <summary>分卷基路径的文件名部分（<c>&lt;源名&gt;.7z</c>）。</summary>
        public string VolumeBaseName => string.IsNullOrWhiteSpace(VolumeBasePath)
            ? string.Empty
            : Path.GetFileName(VolumeBasePath);

        /// <summary>结果 rar 的全路径（默认与 B 同级：<c>&lt;B&gt;.rar</c>）；外层容器是 7z / 不做外层时不参与。</summary>
        public string RarPath { get; init; } = string.Empty;

        /// <summary>外层 7z 容器的全路径（默认与 B 同级：<c>&lt;B&gt;.7z</c>）；外层容器是 rar / 不做外层时不参与。</summary>
        public string SevenZipOuterPath { get; init; } = string.Empty;

        /// <summary>外层容器（用户 2026-09-23 决定：三选一；默认 rar）。</summary>
        public PackOuterContainer OuterContainer { get; init; } = PackOuterContainer.Rar;

        /// <summary>
        /// 本次**实际会产出**的外层容器路径（不做外层时是空串）。
        /// 界面摘要、落点提示、校验与失败正文都读这一个 —— 免得三处各推一遍、推歪了没人发现。
        /// </summary>
        public string OuterPath => OuterContainer switch
        {
            PackOuterContainer.SevenZip => SevenZipOuterPath,
            PackOuterContainer.None => string.Empty,
            _ => RarPath
        };

        /// <summary>分卷大小（字节）。</summary>
        public long VolumeSizeBytes { get; init; } = DefaultVolumeSizeBytes;

        /// <summary>内容物字节数（递归扫 A 加起来；读不到大小的文件按 0 计但会如实报数）。</summary>
        public long ContentBytes { get; init; }

        /// <summary>内容物文件数。</summary>
        public int FileCount { get; init; }

        /// <summary>扫不动 / 读不到大小的条目数（进日志，不拦）。</summary>
        public int UnreadableCount { get; init; }

        /// <summary>预计分卷数（<c>ceil(内容 / 分卷大小)</c>，内容为 0 时是 0 —— 那种请求会被拒）。</summary>
        public long PlannedVolumeCount => ComputeVolumeCount(ContentBytes, VolumeSizeBytes);

        /// <summary>
        /// 需要多少空余空间。**按外层容器算**：有外层时是"分卷一份 + 外层再存一份"（×2.02），
        /// 不做外层时只有分卷那一份（×1.02）—— 后者正是"不做外层"这条出路省空间的地方，
        /// 摘要行里写"不需要多一份空间"就必须真的不按两份要。
        /// </summary>
        public long RequiredSpaceBytes => ComputeRequiredSpace(ContentBytes, OuterContainer.HasOuterArtifact());

        /// <summary>分卷大小的人读写法（<c>512 MiB</c>）。</summary>
        public string VolumeSizeText => FormatVolumeSize(VolumeSizeBytes);

        /// <summary>
        /// 分卷数的人读一句话（日志 / 结果区用）。
        ///
        /// <para>⚠ 2026-09-26 第 46 条改口径：分卷**按卷数**（&lt;1 GiB → 2 卷、≥1 GiB → 3 卷），
        /// 每卷上限由 <see cref="PackingVolumeRule"/> 算 —— 所以这里说的是"目标几卷、上限多少、预计切出几卷"。</para>
        /// </summary>
        public string DescribeVolumePlan()
        {
            return PackingVolumeRule.Describe(ContentBytes, VolumeSizeBytes);
        }

        /// <summary>外层容器的人读一句话（日志 / 摘要行用；把"落在哪个文件"一起说清）。</summary>
        public string DescribeOuterContainer()
        {
            return DescribeOuterContainer(OuterContainer);
        }

        /// <summary>
        /// 按**实际生效的**容器说一句话（第 46 条：没装 WinRAR 时 rar 会自动降级成 7z，
        /// 那句"外层容器：…"必须说降级之后的那个，⛔ 不许照抄请求里那一档）。
        /// </summary>
        public string DescribeOuterContainer(PackOuterContainer effectiveContainer)
        {
            string path = effectiveContainer switch
            {
                PackOuterContainer.SevenZip => SevenZipOuterPath,
                PackOuterContainer.None => string.Empty,
                _ => RarPath
            };

            return effectiveContainer.HasOuterArtifact()
                ? $"外层容器：{effectiveContainer.Describe()} → {path}"
                : $"外层容器：{effectiveContainer.Describe()} → 不产出外层文件，结果就是 {OutputFolder} 里的分卷";
        }

        /// <summary>
        /// 界面输入框的校验（64 MiB – 4096 MiB，且必须是整数 MiB）。
        /// 返回空串 = 通过；否则是给用户看的那句话。
        /// </summary>
        public static string ValidateVolumeSize(long bytes)
        {
            if (bytes <= 0)
            {
                return $"分卷大小必须是正数（{FormatVolumeSize(MinVolumeSizeBytes)} – {FormatVolumeSize(MaxVolumeSizeBytes)}）。";
            }

            if (bytes % (1024 * 1024) != 0)
            {
                return "分卷大小必须是整数 MiB（7z 的 -v 参数以 MiB 为单位）。";
            }

            if (bytes < MinVolumeSizeBytes || bytes > MaxVolumeSizeBytes)
            {
                return $"分卷大小要在 {FormatVolumeSize(MinVolumeSizeBytes)} – {FormatVolumeSize(MaxVolumeSizeBytes)} 之间"
                     + $"（当前 {FormatVolumeSize(bytes)}）。";
            }

            return string.Empty;
        }

        /// <summary>预计分卷数：<c>ceil(内容 / 分卷大小)</c>；内容为 0 或分卷大小无效时是 0。</summary>
        public static long ComputeVolumeCount(long contentBytes, long volumeSizeBytes)
        {
            if (contentBytes <= 0 || volumeSizeBytes <= 0)
            {
                return 0;
            }

            // 先除再补：content + size - 1 在两者都接近 long.MaxValue 时会溢出成负数，
            // 于是"要切几万卷"被算成"一卷也不用"。饱和加法在这里比公式省事。
            long quotient = contentBytes / volumeSizeBytes;

            return contentBytes % volumeSizeBytes == 0
                ? quotient
                : TaskSpaceEstimate.SaturatingSum(quotient, 1);
        }

        /// <summary>空间需求：内容 × 2.02 + 32 MiB（饱和加法，绝不回绕成负数）。</summary>
        public static long ComputeRequiredSpace(long contentBytes)
        {
            return ComputeRequiredSpace(contentBytes, includeOuterCopy: true);
        }

        /// <summary>
        /// 空间需求：内容 ×（1.02 或 2.02）+ 32 MiB。
        /// <paramref name="includeOuterCopy"/> = false 表示"不做外层容器"，只算分卷那一份。
        /// </summary>
        public static long ComputeRequiredSpace(long contentBytes, bool includeOuterCopy)
        {
            if (contentBytes <= 0)
            {
                return SpaceMarginBytes;
            }

            double factor = includeOuterCopy ? SpaceFactor : ContentOnlySpaceFactor;

            double scaled = contentBytes * factor;

            long content = scaled >= long.MaxValue ? long.MaxValue : (long)Math.Ceiling(scaled);

            return TaskSpaceEstimate.SaturatingSum(content, SpaceMarginBytes);
        }

        /// <summary>默认的输出文件夹 B：与 A 同级，名字加后缀 <c>_打包</c>。</summary>
        public static string DefaultOutputFolder(string sourceFolder)
        {
            if (string.IsNullOrWhiteSpace(sourceFolder))
            {
                return string.Empty;
            }

            string full = SafeGetFullPath(sourceFolder);
            string? parent = Path.GetDirectoryName(full);

            if (string.IsNullOrWhiteSpace(parent))
            {
                return string.Empty;
            }

            string name = Path.GetFileName(full);

            if (string.IsNullOrWhiteSpace(name))
            {
                return string.Empty;
            }

            // 根目录（D:\）下的文件夹没有父目录名可用时，退回落点仍与它同级。
            return Path.Combine(parent, name + "_打包");
        }

        /// <summary>默认的结果 rar：与 B 同级，<c>&lt;B 名&gt;.rar</c>（B 是 <c>X_打包</c> 时就是 <c>X_打包.rar</c>）。</summary>
        public static string DefaultRarPath(string outputFolder)
        {
            return DefaultOuterPath(outputFolder, ".rar");
        }

        /// <summary>
        /// 默认的外层 7z 容器：与 B 同级，<c>&lt;B 名&gt;.7z</c>（与 <see cref="DefaultRarPath"/> 一一对应）。
        /// </summary>
        public static string DefaultSevenZipOuterPath(string outputFolder)
        {
            return DefaultOuterPath(outputFolder, ".7z");
        }

        private static string DefaultOuterPath(string outputFolder, string extension)
        {
            if (string.IsNullOrWhiteSpace(outputFolder))
            {
                return string.Empty;
            }

            string full = SafeGetFullPath(outputFolder);
            string? parent = Path.GetDirectoryName(full);
            string name = Path.GetFileName(full);

            if (string.IsNullOrWhiteSpace(parent) || string.IsNullOrWhiteSpace(name))
            {
                return string.Empty;
            }

            return Path.Combine(parent, name + extension);
        }

        /// <summary>
        /// 校验请求并算出规划。返回 false 时 <paramref name="error"/> 是给用户看的那句话
        /// （**必须具体**：说清哪一条不合法、为什么）。
        /// </summary>
        public static bool TryCreate(PackingRequest request, out PackingPlan? plan, out string error)
        {
            plan = null;
            error = string.Empty;

            if (request == null)
            {
                error = "没有打包请求。";
                return false;
            }

            /*
             * ① 先把"用户选的东西"翻成"要打包哪个文件夹"（用户 2026-09-26 第 46 条）：
             * 文件夹 → 就是它自己；单文件 → 在它旁边生成同名文件夹，把文件放进去再打包，
             * 那个新建的文件夹算**其余物**。
             */
            PackingSourceResolution sourceResolution = PackingSourceResolver.Resolve(request.SourceFolder);

            if (!sourceResolution.Success)
            {
                error = sourceResolution.Reason;
                return false;
            }

            string source = sourceResolution.FolderToPack;

            // 单文件时源文件夹还没建 —— 规划阶段**不建盘**，只记下"要建它"。
            if (sourceResolution.Kind == PackingSourceKind.Folder && !Directory.Exists(source))
            {
                error = $"源文件夹不存在：{source}";
                return false;
            }

            // 显式指定分卷大小时才校验它；自动档（0）由 PackingVolumeRule 算。
            if (request.VolumeSizeBytes > 0 &&
                (request.VolumeSizeBytes < HardMinVolumeSizeBytes ||
                 request.VolumeSizeBytes > MaxVolumeSizeBytes ||
                 request.VolumeSizeBytes % (1024 * 1024) != 0))
            {
                error = $"分卷大小不合法：{FormatVolumeSize(request.VolumeSizeBytes)}。"
                      + $"要在 {FormatVolumeSize(HardMinVolumeSizeBytes)} – {FormatVolumeSize(MaxVolumeSizeBytes)} 之间，"
                      + "且是整数 MiB（7z 的 -v 参数以 MiB 为单位）。";
                return false;
            }

            // 单文件时那层文件夹里此刻还没有东西 —— 内容大小按**源文件**量。
            ScanResult scan = sourceResolution.Kind == PackingSourceKind.File
                ? ScanFile(sourceResolution.SourcePath)
                : Scan(source);

            if (scan.FileCount == 0 || scan.Bytes <= 0)
            {
                error = $"没有可打包的内容（{sourceResolution.SourcePath}）：扫到 {scan.FileCount} 个文件、"
                      + $"{TaskSpaceEstimate.FormatSize(scan.Bytes)}。请选一个有东西的文件夹或文件。";
                return false;
            }

            /*
             * ② 落点与命名（用户 2026-09-26 第 5/7 条 + 当天追加）：
             * · 落点目录 = 源旁边（默认）或指定位置；
             * · **分卷直接落在落点目录里**（追加改口径：不再套一个以源名命名的中间文件夹），
             *   基名 = 源名 + `.7z`，撞名（含"真正的第一卷已存在"）让位成 `源名(1).7z`；
             * · 最终 rar / 7z 外层 = **永远以源名命名**（`源名.rar`），重名同样让位。
             */
            string target = PackingPaths.ResolveTargetDirectory(
                sourceResolution,
                request.RunOptions ?? new PackingRunOptions());

            if (string.IsNullOrWhiteSpace(target))
            {
                error = "推不出落点目录，请手动指定一个（或确认源所在盘可用）。";
                return false;
            }

            string output = string.IsNullOrWhiteSpace(request.OutputFolder)
                ? target
                : SafeGetFullPath(request.OutputFolder);

            if (string.IsNullOrWhiteSpace(output))
            {
                error = "推不出分卷落在哪个目录，请手动指定一个。";
                return false;
            }

            string volumeBasePath = PackingPaths.ResolveUniqueVolumeBasePath(output, sourceResolution.SourceName);

            if (string.IsNullOrWhiteSpace(volumeBasePath))
            {
                error = $"推不出分卷的名字（源：{sourceResolution.SourceName}）。";
                return false;
            }

            string rarPath = string.IsNullOrWhiteSpace(request.RarPath)
                ? PackingPaths.ResolveRarPath(target, sourceResolution.SourceName)
                : SafeGetFullPath(request.RarPath);

            if (string.IsNullOrWhiteSpace(rarPath))
            {
                error = "推不出结果 rar 的路径，请手动指定一个。";
                return false;
            }

            // 外层 7z 容器与 rar 一一对应（同一个落点、同一个名字），不给用户第二个输入框。
            string sevenZipOuterPath = PackingPaths.ResolveSevenZipOuterPath(target, sourceResolution.SourceName);

            if (request.OuterContainer == PackOuterContainer.SevenZip && string.IsNullOrWhiteSpace(sevenZipOuterPath))
            {
                error = "推不出外层 7z 容器的路径，请手动指定一个落点目录。";
                return false;
            }

            string name = sourceResolution.SourceName;

            if (string.IsNullOrWhiteSpace(name))
            {
                error = $"源的名字取不出来（{sourceResolution.SourcePath}），没法给分卷命名。";
                return false;
            }

            string outerPath = request.OuterContainer == PackOuterContainer.SevenZip ? sevenZipOuterPath : rarPath;
            string? reject = ValidatePlacement(source, output, outerPath, request.OuterContainer);

            if (reject != null)
            {
                error = reject;
                return false;
            }

            // ③ 分卷：自动档按"目标卷数"算每卷上限（第 46 条）；显式值照用。
            int targetCount = PackingVolumeRule.TargetCount(scan.Bytes);

            long volumeSize = request.VolumeSizeBytes > 0
                ? request.VolumeSizeBytes
                : PackingVolumeRule.ComputeVolumeSize(scan.Bytes, targetCount);

            plan = new PackingPlan
            {
                SourceFolder = source,
                SourceFolderName = name,
                SourceResolution = sourceResolution,
                TargetDirectory = target,
                RunOptions = request.RunOptions ?? new PackingRunOptions(),
                VolumeTargetCount = targetCount,
                OutputFolder = output,
                VolumeBasePath = volumeBasePath,
                RarPath = rarPath,
                SevenZipOuterPath = sevenZipOuterPath,
                OuterContainer = request.OuterContainer,
                VolumeSizeBytes = volumeSize,
                ContentBytes = scan.Bytes,
                FileCount = scan.FileCount,
                UnreadableCount = scan.UnreadableCount
            };

            return true;
        }

        /// <summary>单文件源的内容大小（内容就是这一个文件；读不到大小按 0 计并如实计数）。</summary>
        private static ScanResult ScanFile(string filePath)
        {
            try
            {
                var info = new FileInfo(filePath);

                return info.Exists
                    ? new ScanResult(1, info.Length, 0)
                    : new ScanResult(0, 0, 0);
            }
            catch
            {
                return new ScanResult(1, 0, 1);
            }
        }

        /// <summary>
        /// 落点校验（docs/打包功能.md §4）。返回 null = 通过，否则是拒绝的那句话。
        ///
        /// <para>四条，每一条都对应一个真实会出事的情形：</para>
        /// <list type="number">
        /// <item><description><b>B 在 A 里面</b>：分卷会落进被扫描的源文件夹里 —— 越打越多，
        /// 而且 7z 正在读的目录正在被自己写（用户点名要拒的那一条）。</description></item>
        /// <item><description><b>外层容器在 B 里面</b>：外层容器会把自己装进去（用户点名要拒的那一条）。</description></item>
        /// <item><description><b>外层容器在 A 里面</b>：不变量 12 —— 源目录里一个字节都不许写。</description></item>
        /// <item><description><b>A 在 B 里面</b>：外层容器装的是整个 B，于是源文件被原样再存一份
        /// （结果比预期大一倍，且"结果 = 压缩包 B"这句话就不成立了）。</description></item>
        /// </list>
        ///
        /// <para><b>按容器分岔</b>：不做外层（<see cref="PackOuterContainer.None"/>）时没有外层产物，
        /// 那三条"外层容器落在哪"的判据自然都不适用 —— 但"B 在 A 里面"与"A 在 B 里面"照旧拒绝：
        /// 前者让源目录越打越多，后者让源文件与产物混在同一个目录里（不变量 12：源目录、工作区、
        /// 最终输出三者相互独立）。</para>
        /// </summary>
        /// <param name="sourceFolder">源文件夹 A。</param>
        /// <param name="outputFolder">输出文件夹 B。</param>
        /// <param name="outerPath">外层产物路径（不做外层时传空串）。</param>
        /// <param name="container">外层容器（默认 rar，与早先只有 rar 时的口径一致）。</param>
        public static string? ValidatePlacement(
            string sourceFolder,
            string outputFolder,
            string outerPath,
            PackOuterContainer container = PackOuterContainer.Rar)
        {
            string source = SafeGetFullPath(sourceFolder);
            string output = SafeGetFullPath(outputFolder);
            bool hasOuter = container.HasOuterArtifact();
            string outer = hasOuter ? SafeGetFullPath(outerPath) : string.Empty;
            string noun = container.Noun();

            if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(output))
            {
                return "路径推不出来（源文件夹 / 输出文件夹两样都要有）。";
            }

            if (hasOuter && string.IsNullOrWhiteSpace(outer))
            {
                return $"推不出外层容器的路径（{container.Describe()}），请手动指定一个。";
            }

            if (PathEquals(source, output))
            {
                return $"落点不能就是源文件夹本身：{output}。请在⑤页把落点改回「默认（源旁边）」，或另选一个目录。";
            }

            if (IsInside(output, source))
            {
                return $"落点不能落在源文件夹里面（源：{source}；落点：{output}）——"
                     + "分卷会落进正在打包的目录，越打越多。请在⑤页把落点改到源文件夹外面"
                     + "（默认的「源旁边」就在源的同级）。";
            }

            /*
             * ⚠ 2026-09-26 追加改口径：这里原来有两条判据（"外层容器不能放在装分卷的文件夹里面"、
             * "源文件夹不能落在落点目录里面"），都是"B 会被整个装进外层容器"那个旧模型的前提。
             * 现在分卷**直接落在落点目录里**、外层容器装的是**逐个点名的分卷文件**，
             * 而"默认落点 = 源旁边"本来就让源文件夹位于落点目录里 —— 那两条按字面会把**默认档自己**
             * 一网打尽（实测：改完之后默认落点直接被拒）。所以它们整块退役，
             * 只保留真正的红线：落点不能是源文件夹本身、不能落在源文件夹里面、外层容器不能写进源目录。
             */

            if (hasOuter && IsInside(outer, source))
            {
                return $"{noun}不能放在源文件夹里面（源：{source}；{noun}：{outer}）——"
                     + "源目录里不写任何东西（避免把打包结果又打进去一次）。请把落点改到源文件夹外面。";
            }

            if (hasOuter && Directory.Exists(outer))
            {
                return $"{noun}的路径被一个同名文件夹占着：{outer}。请换一个名字。";
            }

            if (hasOuter && File.Exists(outer))
            {
                /*
                 * 与不变量 3 同一口径：冲突**不默认覆盖**。
                 *
                 * ⚠ 这是**防御性**判据（2026-09-26 同步审计核过口径）：正常路径下走不到 ——
                 * `PackingPlan.TryCreate` 算外层路径时用的是 `PackingNaming.ResolveUniqueFilePath`
                 * （撞名自动让位成 `源名(1).rar`），所以走到这里时那个文件通常不存在。
                 * 保留它有两个理由：①`ValidatePlacement` 是对外的纯函数，有测试直接调它钉"绝不覆盖"；
                 * ②万一哪天上游算路径那一步被绕过，这里必须拦住，而不是让外层容器把旧文件顶掉。
                 * 措辞也不写"请先把旧的改名"（那会与"程序自动让位"互相矛盾）—— 只有绕过了上游才看得到这句。
                 */
                return $"同名结果文件已经存在（不会默认覆盖）：{outer}。"
                     + "请换一个落点目录再试（正常路径下程序会先把名字让位成「名字(1)」）。";
            }

            /*
             * ⚠ 追加改口径：原来这里还要求"输出文件夹必须是空的"（旧模型里 B 会被整个装进外层容器）。
             * 现在落点目录**就是源所在的那个目录**（默认档），它当然不是空的 —— 判据整条退役。
             * 撞名由"分卷基名 / 外层产物各自让位"负责（见 `ResolveUniqueVolumeBasePath` 与上面的 File.Exists）。
             */

            return null;
        }

        /// <summary>
        /// <paramref name="candidate"/> 是不是落在 <paramref name="directory"/> 里面（同级 / 自身不算）。
        /// 纯路径比较，OrdinalIgnoreCase（Windows 口径），末尾分隔符先归一。
        /// </summary>
        public static bool IsInside(string candidate, string directory)
        {
            string child = SafeGetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string parent = SafeGetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            if (child.Length == 0 || parent.Length == 0 || PathEquals(child, parent))
            {
                return false;
            }

            return child.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>两个路径是不是同一个（Windows 口径）。</summary>
        public static bool PathEquals(string left, string right)
        {
            string a = SafeGetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string b = SafeGetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>目录是不是空的（取不到 → 当作"不空"，宁可拦下也不要静默覆盖别人的东西）。</summary>
        public static bool IsDirectoryEmpty(string directory)
        {
            try
            {
                using IEnumerator<string> entries = Directory.EnumerateFileSystemEntries(directory).GetEnumerator();

                return !entries.MoveNext();
            }
            catch
            {
                return false;
            }
        }

        /// <summary>把字节数说成 MiB/GiB（分卷大小专用：它不是"内容物大小"，用整数 MiB 更好读）。</summary>
        public static string FormatVolumeSize(long bytes)
        {
            if (bytes <= 0)
            {
                return "0 MiB";
            }

            long mib = bytes / (1024 * 1024);

            if (mib < 1024)
            {
                return mib.ToString(CultureInfo.InvariantCulture) + " MiB";
            }

            return (bytes / 1024d / 1024d / 1024d).ToString("0.##", CultureInfo.InvariantCulture) + " GiB";
        }

        private static string SafeGetFullPath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            try
            {
                return Path.GetFullPath(path.Trim());
            }
            catch
            {
                // 非法路径（含非法字符 / 过长）：返回原样，由调用方按"推不出来"处理。
                return string.Empty;
            }
        }

        /// <summary>递归扫一遍：文件数 + 总字节数。**不抛** —— 扫不动就如实记数。</summary>
        private static ScanResult Scan(string folder)
        {
            long bytes = 0;
            int files = 0;
            int unreadable = 0;

            var pending = new Stack<string>();
            pending.Push(folder);

            while (pending.Count > 0)
            {
                string current = pending.Pop();
                string[] entries;

                try
                {
                    entries = Directory.GetFileSystemEntries(current);
                }
                catch
                {
                    // 权限不足 / 目录刚被删：记一笔，继续扫别的分支。
                    unreadable++;
                    continue;
                }

                foreach (string entry in entries)
                {
                    try
                    {
                        FileAttributes attributes = File.GetAttributes(entry);

                        if ((attributes & FileAttributes.Directory) != 0)
                        {
                            /*
                             * 不跟重解析点（符号链接 / 联接）：跟进去可能成环，也可能扫到源文件夹外面，
                             * 于是"要打包的内容"与用户以为的不一样。7z 默认也不跟链接。
                             */
                            if ((attributes & FileAttributes.ReparsePoint) != 0)
                            {
                                continue;
                            }

                            pending.Push(entry);
                            continue;
                        }

                        bytes = TaskSpaceEstimate.SaturatingSum(bytes, new FileInfo(entry).Length);
                        files++;
                    }
                    catch
                    {
                        unreadable++;
                    }
                }
            }

            return new ScanResult(files, bytes, unreadable);
        }

        private readonly struct ScanResult
        {
            public ScanResult(int fileCount, long bytes, int unreadableCount)
            {
                FileCount = fileCount;
                Bytes = bytes;
                UnreadableCount = unreadableCount;
            }

            public int FileCount { get; }

            public long Bytes { get; }

            public int UnreadableCount { get; }
        }
    }
}
