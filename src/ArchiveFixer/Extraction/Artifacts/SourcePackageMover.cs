using ArchiveFixer.Detection;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using ArchiveFixer.Security;
using ArchiveFixer.Storage;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ArchiveFixer.Extraction;
/// <summary>
/// 把**本任务的源包（整组）**搬进其余物目录（决策 D-9/D-11/D-12，2026-09-22 用户拍板）。
///
/// <para>
/// 为什么源包也算「其余物」：用户在"其余物"里一次删掉就干净了 ——
/// 内容物留在原地，源包和过程物一起进同一层，整理完只需删一个目录。
/// </para>
/// <para>
/// 四条硬约束（都在这里落地，调用方不许绕过）：
/// ① **整组一起移**：分卷组取 <see cref="ArchiveTask.VolumePaths"/> 全卷，单文件任务取它自己；
///    清单只来自任务自身，**不扫目录**（与 <c>SourceCleanupService</c> 同一口径）；
/// ② **绝不覆盖**：目标同名加 <c>(1)(2)</c>；
/// ③ **跨盘 = 先复制成功、再删原件**：复制失败源包一个字节都不动，绝不"先删后移"；
/// ④ **搬不动就如实报失败**（只读 / 被占用），由调用方把任务标成"部分完成"——
///    内容物已经好了，这件事不该影响内容物的结论。
/// </para>
/// <para>
/// 本类**不判断该不该搬**：那由 <c>ExtractionCoordinator</c> 按"内容物已定稿 + 校验通过 + 未取消 + 一键处理路径"决定（D-11）。
/// </para>
/// </summary>
public sealed class SourcePackageMover
{
    private readonly ISourceMoveFileSystem _fileSystem;

    public SourcePackageMover(ISourceMoveFileSystem? fileSystem = null)
    {
        _fileSystem = fileSystem ?? FileSystemSourceMoveFileSystem.Instance;
    }

    /// <summary>
    /// 本任务的源包清单：分卷组 = 整组各卷；单文件任务 = 它自己。
    ///
    /// 与 <c>SourceCleanupService.BuildTargetList</c> 同一口径（那边是删、这边是移，清单必须一致）：
    /// 分卷组只认 <see cref="ArchiveTask.VolumePaths"/>，清单为空时**不回退**到 CurrentPath ——
    /// 那说明分组信息不完整，少搬一卷比"整组散在两个目录里"更糟，所以宁可什么都不搬。
    ///
    /// <para>
    /// ⚠ 2026-10-01 补一条（`AaaReplayPipelineTests` 逮到）：清单**非空、却不含任务自己的文件**时，
    /// 这一单按单文件办。现场形状：夹具里 `222.zscip`（真 7z）被「修正后缀」改成 `222.7z`，
    /// 认组时按名字把旁边的 `222.z删除01` 当成同一族的第 2 卷（基名都是 `222`）⇒
    /// `VolumePaths` 里只有兄弟卷、**没有任务自己**。老口径在"清单不完整"这一档什么都不搬，
    /// 结果：成功任务报「已把 1 个源包移入其余物」，而**它自己那份 `222.7z` 一个字节都没动**，
    /// 留在源目录里被下一轮又扫一遍（真机上就是"同一份东西反复解"）。
    /// 判据只用事实：清单非空 + `CurrentPath` 在盘上 + 它不在清单里 ⇒ 这份清单代表不了这一单。
    /// </para>
    /// <para>
    /// ⛔ 清单**为空**那一档一个字不改：分组信息根本没有时一律不搬（老红线，
    /// 用例 `计划_分卷清单不完整时什么都不搬` 钉着）。
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> ResolveSourceGroup(ArchiveTask? task) =>
        ResolveSourceGroup(task, out _);

    /// <summary>
    /// 同上，另外回答"账上不一致"这一档：**分卷清单非空、却不含任务自己**。
    ///
    /// <para>⛔ 这一档现在**什么都不搬**（用户 2026-10-02 改；老口径是"静默降级成只搬它自己一份"）：
    /// 那正是用户 2026-10-01 第四 / 第五报抱怨的形状 —— 一组分卷只搬走一份、其余留在源目录里
    /// 等他手工清（「**`222\222.z01` 依旧还在，你浪费了我两次操作**」）。清单不含自己 =
    /// **账上已经过期**（现实里就是"改了名没同步"那一类），按红线「判不出 ⇒ 什么都不做」，
    /// 整组留在原地、并让调用方把原因写进日志（⛔ 不再静默）。</para>
    /// </summary>
    public static IReadOnlyList<string> ResolveSourceGroup(ArchiveTask? task, out bool groupListInconsistent)
    {
        groupListInconsistent = false;

        var targets = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (task == null)
        {
            return targets;
        }

        void Add(string? path)
        {
            if (!string.IsNullOrWhiteSpace(path) && seen.Add(path!))
            {
                targets.Add(path!);
            }
        }

        if (!task.IsVolumeGroup)
        {
            // 本来就不是分卷组：只搬它自己（老口径，一个字没改）。
            Add(task.CurrentPath);
            return targets;
        }

        if (task.VolumePaths.Count == 0)
        {
            // 分组信息根本不完整（老红线）：一份都不搬。见 计划_分卷清单不完整时什么都不搬。
            return targets;
        }

        bool selfListed = !string.IsNullOrWhiteSpace(task.CurrentPath)
            && task.VolumePaths.Any(
                path => string.Equals(path, task.CurrentPath, StringComparison.OrdinalIgnoreCase));

        if (!selfListed && File.Exists(task.CurrentPath))
        {
            // 清单非空、却不含自己，而自己确实还在盘上 ⇒ 账上过期：整组不搬（见上面那段说明）。
            groupListInconsistent = true;
            return targets;
        }

        /*
         * ⛔ **"这一单自己那一份"必须按盘上的真实名字加进来**（真机 2026-10-07 08:20 逮到）：
         * 起点被改写到入口包上之后 `CurrentPath` 指向别处、`OriginalPath` 还停在改名前的脏名上
         * ⇒ 盘上那一片（`…\111(4)\111.z03`）两条都不沾 ⇒ 清单里没有它 ⇒ 整组搬运搬不到它、
         * 源片永远留在用户目录里。同批里**没被改写起点**的那一单（`111.z02`）清单命中 ⇒ 搬走了
         * —— 这就是"同样两片、结局不同"的唯一变量。
         * ⛔ 这是"这一单自己那一份在盘上"的**唯一出口**（`SourcePackageMover.ResolveOwnFileOnDisk`）—— 协调器那边原来的两份同名实现已删。
         */
        Add(ResolveOwnFileOnDisk(task));

        foreach (string path in task.VolumePaths)
        {
            Add(path);
        }

        return targets;
    }

    /// <summary>
    /// 规划"把这一组源包搬进其余物目录"。纯规划：一个字节都不动。
    /// </summary>
    /// <param name="task">要处理的源包任务。</param>
    /// <param name="artifactDirectory">其余物目录（决策 D-10 算出来的那个）。</param>
    /// <param name="probe">目标占用探测；不传时查真实文件系统。</param>
    public SourcePackageMovePlan Plan(
        ArchiveTask? task,
        string? artifactDirectory,
        IArtifactTargetProbe? probe = null)
    {
        IArtifactTargetProbe targetProbe = probe ?? FileSystemArtifactTargetProbe.Instance;

        if (task == null)
        {
            return new SourcePackageMovePlan { Message = "任务为空，没有规划任何源包搬运" };
        }

        IReadOnlyList<string> sources = ResolveSourceGroup(task, out bool groupListInconsistent);

        if (sources.Count == 0)
        {
            return new SourcePackageMovePlan
            {
                ArtifactDirectory = artifactDirectory ?? string.Empty,
                Message = groupListInconsistent
                    ? "任务账上的分卷清单里没有它自己（清单可能过期，例如改名之后没同步）—— "
                      + "分卷组整组一律不搬，源包全部留在原地"
                    : "任务没有可搬运的源包路径（分卷清单可能不完整），源包一律不动"
            };
        }

        if (string.IsNullOrWhiteSpace(artifactDirectory))
        {
            return new SourcePackageMovePlan
            {
                Skipped = SkipAll(sources, ArtifactSkipReason.TargetDirectoryMissing, "其余物目录算不出来，源包留在原地"),
                Message = "其余物目录算不出来，没有规划任何源包搬运"
            };
        }

        string artifactRoot = artifactDirectory!;

        /*
         * ⛔ 2026-10-09 真机 `EEEE`（19:14 那一趟）：**续解出来的内层包，它的"源"是我们自己产出的内容物，
         * 不是用户的原包** ⇒ 整组不搬（用户口令：「**原包就是原包**」）。
         *
         * 现场（逐条有出处）：`111.rar` 解出来的组入口包 `111.zip` 落在 `…\111\111\111.zip`（行 154）；
         * 下一轮它自己成了两单（行 218/220「内层包，产物归入父任务输出目录」）⇒ `ResolveSourceGroup`
         * 把**我们刚产出的入口包**当成"源包"搬进其余物（行 287/289 两条 `源包移入其余物`，
         * 第三条还把 `111.z01` 一起搬走）⇒ 批末「其余物 = 彻底删除」全删（行 292
         * `彻底删除：111\其余物（5 项 / 545.1 MB）`）⇒ 组入口包没了、**第一大步永远闭不了环**。
         *
         * ⚠ **落在这里、而不是某个调用者的理由**：本方法就是"哪些算源包"的**唯一决策处**，
         * 而调用者有好几个（每单收尾 / 链尾补搬 / 手动档）—— 加在调用者上必然漏一个
         * （2026-10-09 我先加在 `ExtractionCoordinator` 的链尾循环里，真机复跑**完全没拦住**）。
         * 判据唯一出口 = `ArchiveTask.IsContinuationTask`（`ParentOutputDirectory` 非空即内层包）。
         * ⛔ 不影响正当场景：真正要按档处理的是**用户导入的那份原包**，它不是续解任务。
         */
        if (task.IsContinuationTask)
        {
            return new SourcePackageMovePlan
            {
                ArtifactDirectory = artifactRoot,
                Skipped = SkipAll(
                    sources,
                    ArtifactSkipReason.SourceIsOurOwnContent,
                    "这一单是续解出来的内层包：它的“源”是我们自己产出的内容物，不是用户的原包"),
                Message = "续解出来的内层包不参与源包处理 —— 源包一个字节都不搬（我们自己产出的内容物不是原包）"
            };
        }

        var moves = new List<SourcePackageMove>();
        var skipped = new List<ArtifactSkip>();
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long totalSize = 0;

        foreach (string source in sources)
        {
            if (!_fileSystem.FileExists(source))
            {
                skipped.Add(new ArtifactSkip
                {
                    SourcePath = source,
                    Reason = ArtifactSkipReason.SourceMissing,
                    Message = "源文件不存在（可能已被移动或删除），跳过"
                });

                continue;
            }

            /*
             * ⛔ **分卷成员不搬进其余物**（真机 8888：`111-L1.7z.001` 在其余物里 ⇒ 第二大步
             * 组入口落在其余物、产物落进原包树，① ② ③ 全红）。
             * 判据只读内容事实（`FileNatureCard`）：其它一概不猜；判不出 ⇒ 照旧老口径。
             * 留在原地后由第二大步组认领/收片（Delete 档也在组成功后由既有收片出口处理）。
             */
            try
            {
                FileNatureCard sourceCard = FileNature.Inspect(source);

                if (sourceCard.Membership == VolumeNumberFromContent.VolumeMembershipVerdict.VolumeMember
                    || sourceCard.BodyKind == ArchiveBodyKind.VolumeFirstPiece
                    || sourceCard.BodyKind == ArchiveBodyKind.VolumePiece)
                {
                    skipped.Add(new ArtifactSkip
                    {
                        SourcePath = source,
                        Reason = ArtifactSkipReason.VolumeGroupMemberLeaveInPlace,
                        Message = "这一份是分卷成员（内容自证）：其余物不能当第二大步入口，原样留在原地等组认领"
                    });

                    continue;
                }
            }
            catch
            {
                // 卡读不出 ⇒ 判不出 ⇒ 照旧老口径（不拦）。
            }

            if (IsAlreadyInsideRest(source, artifactRoot, out string restDirectory))
            {
                skipped.Add(new ArtifactSkip
                {
                    SourcePath = source,
                    Reason = ArtifactSkipReason.SourceInsideArtifactDirectory,
                    Message = $"源已经在其余物目录里（{restDirectory}），不需要再搬"
                });

                continue;
            }

            /*
             * ⛔ 源**落在工作区自己那棵树里** → 一个字节都不搬（用户 2026-09-30）。
             *
             * 工作区默认建在目标目录里面（<目标目录>\.ArchiveFixer.work），它和"其余物"在同一个父目录下。
             * 续解任务的源包路径是从产物目录扫出来的 —— 万一哪条岔路把一个中间产物当成了"内层包"，
             * 这一步就会把工作区里的东西搬进其余物、甚至进入可删清单：那是不可逆的事故。
             * 判据唯一出口 WorkspaceTree（名字 + 当前生效的根两条），兜底落在"什么都不做"那一档。
             */
            if (WorkspaceTree.ShouldSkipEntry(source, RecursiveExtractor.ConfiguredWorkspaceRoot))
            {
                skipped.Add(new ArtifactSkip
                {
                    SourcePath = source,
                    Reason = ArtifactSkipReason.SourceInsideWorkspace,
                    Message = "源在工作区目录里（那是程序自己的中间产物，不是用户的源包），一个字节都不搬"
                });

                continue;
            }

            string candidate = Path.Combine(artifactRoot, FileNameHelper.SanitizeFileName(Path.GetFileName(source)));
            bool renamed = false;

            if (reserved.Contains(candidate) || targetProbe.Exists(candidate))
            {
                string unique = ProcessArtifactLayout.MakeUniqueTarget(
                    candidate,
                    false,
                    reserved,
                    targetProbe);

                renamed = !string.Equals(unique, candidate, StringComparison.OrdinalIgnoreCase);
                candidate = unique;
            }

            reserved.Add(candidate);

            long size = _fileSystem.GetFileSize(source);
            totalSize += size;

            moves.Add(new SourcePackageMove
            {
                SourcePath = source,
                TargetPath = candidate,
                Size = size,
                Renamed = renamed
            });
        }

        return new SourcePackageMovePlan
        {
            ArtifactDirectory = artifactRoot,
            Moves = moves,
            Skipped = skipped,
            TotalSize = totalSize,
            Message = BuildPlanMessage(artifactRoot, moves, skipped)
        };
    }

    /// <summary>
    /// 执行搬运。**只允许在后台线程上跑**（跨盘时是整包拷贝）。
    ///
    /// 取消：调用方应当在**开始之前**查一次令牌；这里一旦开始搬这一组就把它搬完 ——
    /// 半组卷散在两个目录里，比"晚几百毫秒才停下来"糟得多（决策 D-12"整组一起移"）。
    /// </summary>
    public SourcePackageMoveResult Execute(SourcePackageMovePlan? plan)
    {
        if (plan == null || plan.Moves.Count == 0)
        {
            return new SourcePackageMoveResult
            {
                Attempted = false,
                SkippedCount = plan?.Skipped.Count ?? 0,
                Message = plan?.Message ?? "没有可搬运的源包"
            };
        }

        var moved = new List<SourcePackageMove>();
        var failures = new List<string>();
        var logLines = new List<string>();
        long movedBytes = 0;
        int crossVolume = 0;
        bool createdArtifactDirectory = false;

        foreach (SourcePackageMove move in plan.Moves)
        {
            string from = move.SourcePath;
            string to = move.TargetPath;

            try
            {
                if (!_fileSystem.DirectoryExists(plan.ArtifactDirectory))
                {
                    if (!_fileSystem.CreateDirectory(plan.ArtifactDirectory))
                    {
                        failures.Add($"{Path.GetFileName(from)}（其余物目录创建失败：{plan.ArtifactDirectory}）");
                        continue;
                    }

                    createdArtifactDirectory = true;
                }

                if (_fileSystem.FileExists(to) || _fileSystem.DirectoryExists(to))
                {
                    /*
                     * 计划之后目标被别人占了（并发解压 / 用户手动放了东西进来）。
                     * 计划阶段已经避过一次，这里是最后一道：**重新让名字，绝不覆盖**。
                     */
                    to = MakeFallbackTarget(to);
                }

                bool sameVolume = _fileSystem.IsSameVolume(from, to);

                if (sameVolume)
                {
                    // 同卷：原子改名。目标已存在时底层直接抛，绝不会覆盖。
                    _fileSystem.MoveFile(from, to);
                }
                else
                {
                    /*
                     * 跨盘：**先复制成功、再删原件**（决策 D-12）。
                     * 顺序绝不能反 —— "先删后移"在复制失败的瞬间就把用户的源包弄丢了。
                     */
                    _fileSystem.CopyFile(from, to);
                    crossVolume++;

                    try
                    {
                        _fileSystem.DeleteFile(from);
                    }
                    catch (Exception deleteError)
                    {
                        // 复制成功但原件删不掉：把副本清掉，宁可"源包没搬成"，也不要凭空多一份。
                        TryDeleteCopy(to, logLines);
                        failures.Add($"{Path.GetFileName(from)}（复制成功但原件删不掉：{deleteError.Message}）");
                        continue;
                    }
                }

                moved.Add(new SourcePackageMove
                {
                    SourcePath = from,
                    TargetPath = to,
                    Size = move.Size,
                    Renamed = move.Renamed
                });

                movedBytes += move.Size;

                // 每次搬运都写日志：从哪到哪 + 这个文件多大（决策 D-12）。
                logLines.Add($"源包移入其余物（{move.Size} 字节）：{from} → {to}"
                             + (move.Renamed ? "（目标同名，已加序号，未覆盖）" : string.Empty));
            }
            catch (Exception ex)
            {
                failures.Add($"{Path.GetFileName(from)}（{ex.Message}）");
            }
        }

        /*
         * 一个都没搬成、而且这个目录是我们刚建出来的 → 把空壳收掉。
         *
         * 用户的抱怨原话是"多弄了文件夹"：搬失败时（只读 / 被占用）留下一个空的
         * <c>其余物\</c> 正是这一类 —— 它里面一个字节都没有，用户却看到多了一层目录。
         * 只删**空**目录，而且只删我们这一次建的（非空 / 别人建的都不碰）。
         */
        if (moved.Count == 0 && createdArtifactDirectory)
        {
            TryDeleteEmptyArtifactDirectory(plan.ArtifactDirectory, logLines);
        }

        return new SourcePackageMoveResult
        {
            Attempted = true,
            MovedCount = moved.Count,
            FailedCount = failures.Count,
            SkippedCount = plan.Skipped.Count,
            CrossVolumeCount = crossVolume,
            MovedBytes = movedBytes,
            Moved = moved,
            Failures = failures,
            LogLines = logLines,
            Message = BuildResultMessage(plan.ArtifactDirectory, moved.Count, movedBytes, crossVolume, failures)
        };
    }

    /// <summary>尽力删掉"我们刚建出来、里面一个字节都没有"的其余物目录（非空一律不碰）。</summary>
    private void TryDeleteEmptyArtifactDirectory(string directory, List<string> logLines)
    {
        try
        {
            if (!_fileSystem.DirectoryExists(directory))
            {
                return;
            }

            if (Directory.EnumerateFileSystemEntries(directory).Any())
            {
                return;
            }

            Directory.Delete(directory, recursive: false);
            logLines.Add($"源包一个都没搬成，已收掉空目录：{directory}");
        }
        catch (Exception ex)
        {
            logLines.Add($"警告：没能收掉空的其余物目录（{directory}）：{ex.Message}");
        }
    }

    /// <summary>
    /// 源是不是**已经躺在其余物里**了（新旧两个名字都算，决策 D-8）。
    ///
    /// 判据有三条，缺一不可：
    /// ① 在其余物目录**之内或就是它**（共享根模式下源包会落在 <c>其余物\包名\</c>，
    ///    而这里传进来的就是那一层）；
    /// ② 在其余物目录下的工作区子目录里。
    /// </summary>
    /// <param name="restDirectory">命中的那个其余物目录（写日志用）。</param>
    private static bool IsAlreadyInsideRest(string source, string artifactRoot, out string restDirectory)
    {
        if (SafePathHelper.PathEquals(source, artifactRoot) ||
            source.StartsWith(artifactRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            source.StartsWith(artifactRoot + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            restDirectory = artifactRoot;
            return true;
        }

        if (ProcessArtifactLayout.IsInsideArtifactDirectory(source, artifactRoot))
        {
            restDirectory = artifactRoot;
            return true;
        }

        restDirectory = string.Empty;
        return false;
    }

    /// <summary>目标被临时占用时的兜底改名（<c>名字(1).ext</c>，绝不覆盖）。</summary>
    private static string MakeFallbackTarget(string target)
    {
        for (int index = 1; index < 10000; index++)
        {
            string candidate = Path.Combine(
                Path.GetDirectoryName(target) ?? string.Empty,
                $"{Path.GetFileNameWithoutExtension(target)}({index}){Path.GetExtension(target)}");

            if (!SafePathHelper.FileExists(candidate) && !SafePathHelper.DirectoryExists(candidate))
            {
                return candidate;
            }
        }

        return Path.Combine(
            Path.GetDirectoryName(target) ?? string.Empty,
            $"{Path.GetFileNameWithoutExtension(target)}_{Guid.NewGuid():N}{Path.GetExtension(target)}");
    }

    /// <summary>副本清不掉也要说清楚：用户目录里会多出一份，那件事不能被静默吞掉。</summary>
    private void TryDeleteCopy(string copyPath, List<string> logLines)
    {
        try
        {
            _fileSystem.DeleteFile(copyPath);
            logLines.Add($"已回滚跨盘复制出来的副本：{copyPath}");
        }
        catch (Exception ex)
        {
            logLines.Add($"警告：跨盘复制出来的副本没能回滚（{copyPath}）：{ex.Message}；原源包仍在原处，请自行核对");
        }
    }

    private static List<ArtifactSkip> SkipAll(
        IEnumerable<string> sources,
        ArtifactSkipReason reason,
        string message)
    {
        return sources
            .Select(source => new ArtifactSkip { SourcePath = source, Reason = reason, Message = message })
            .ToList();
    }

    private static string BuildPlanMessage(
        string artifactDirectory,
        List<SourcePackageMove> moves,
        List<ArtifactSkip> skipped)
    {
        var parts = new List<string>
        {
            moves.Count > 0
                ? $"计划把 {moves.Count} 个源包搬进 {artifactDirectory}"
                : "没有可搬运的源包"
        };

        int renamed = moves.Count(move => move.Renamed);

        if (renamed > 0)
        {
            parts.Add($"其中 {renamed} 个重名，将加序号（不覆盖）");
        }

        if (skipped.Count > 0)
        {
            parts.Add($"{skipped.Count} 个被跳过（原因见跳过清单）");
        }

        return string.Join("；", parts);
    }

    private static string BuildResultMessage(
        string artifactDirectory,
        int movedCount,
        long movedBytes,
        int crossVolumeCount,
        List<string> failures)
    {
        var parts = new List<string>();

        if (movedCount > 0)
        {
            parts.Add($"已把 {movedCount} 个源包移入其余物：{artifactDirectory}（共 {movedBytes} 字节）");

            if (crossVolumeCount > 0)
            {
                parts.Add($"其中 {crossVolumeCount} 个跨盘，已按“复制成功后再删原件”完成");
            }
        }
        else
        {
            parts.Add("没有源包被搬走");
        }

        if (failures.Count > 0)
        {
            parts.Add(failures.Count > 3
                ? $"{failures.Count} 个没能搬入其余物，前 3 个：{string.Join("；", failures.Take(3))}"
                : $"{failures.Count} 个没能搬入其余物：{string.Join("；", failures)}");
        }

        return string.Join("；", parts);
    }
    /// <summary>
    /// 这一单自己那一份**在盘上的真实路径**（改名之后也找得到）：最初导入那个目录里、**同包基名**的那个文件。
    /// 判不出 ⇒ 空串（调用方照旧按账上那几条走）。
    /// </summary>
    public static string ResolveOwnFileOnDisk(ArchiveTask task)
    {
        string original = task.OriginalPath ?? string.Empty;
        string directory = Path.GetDirectoryName(original) ?? string.Empty;

        if (original.Length == 0 || directory.Length == 0)
        {
            return string.Empty;
        }

        if (File.Exists(original))
        {
            return original;
        }

        string baseName = FileNameHelper.GetArchiveBaseName(FileNameHelper.GetFileName(original));

        if (baseName.Length == 0)
        {
            return string.Empty;
        }

        /*
         * ⛔ **判据必须是"同一组"那把尺子（族 + 基名），⛔ 不是只比包基名**（2026-10-11 补）。
         *
         * 只比基名时，同一目录里同时有 `X.rar` 与 `X.zip` 就会认到**另一个包** ——
         * 而这里返回的路径喂给 `SourcePackageMover.Plan`（把这一组源包搬进其余物）
         * ⇒ 会把**别人的包**搬进其余物、在删除档下还会被永久删掉（不可逆）。
         * 唯一尺子 = `LegacyVolumeNames.BelongsToSameGroup`（"同一组 = 族 + 基名"）：
         * 同族改名（`111.z0删除2` → `111.z02`）照样认得出，
         * 而跨族同基名（`111.rar` vs `111.zip`）**认不出 ⇒ 空串 ⇒ 什么都不做**（红线那一档）。
         */
        try
        {
            foreach (string file in Directory.EnumerateFiles(directory))
            {
                if (LegacyVolumeNames.BelongsToSameGroup(
                        FileNameHelper.GetFileName(file),
                        FileNameHelper.GetFileName(original)))
                {
                    return file;
                }
            }
        }
        catch
        {
            // 读不动 ⇒ 判不出。
        }

        return string.Empty;
    }
}
