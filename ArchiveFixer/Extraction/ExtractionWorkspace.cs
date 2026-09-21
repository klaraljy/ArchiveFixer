using ArchiveFixer.Helpers;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ArchiveFixer.Extraction
{
    /// <summary>
    /// 工作区里的一层。
    ///
    /// 目录布局（设计.md §十三：中间产物不许写进源目录、也不许直接写最终目录）：
    /// <code>
    /// C:\t\ws\&lt;taskId&gt;\layer-000\output\...   第 0 层（最外层归档）的产物
    /// C:\t\ws\&lt;taskId&gt;\layer-001\output\...   第 1 层的产物（由 layer-000 里的内层归档解出）
    /// </code>
    /// 每层独立一个 output 目录，是为了让"失败定位到层"成为可能：
    /// 停在某一层时，前面每一层的产物都还在原地，用户能直接看、也能从那一层重试。
    /// </summary>
    public sealed class WorkspaceLayer
    {
        /// <summary>层号，从 0 开始（0 = 最外层归档）。</summary>
        public int Depth { get; init; }

        /// <summary>本层的目录（<c>layer-000</c>）。</summary>
        public string DirectoryPath { get; init; } = string.Empty;

        /// <summary>本层的产物目录（<c>layer-000\output</c>）。</summary>
        public string OutputPath { get; init; } = string.Empty;

        /// <summary>本层要解的那个归档（第 0 层是任务的 CurrentPath，之后是上一层产物里的内层归档）。</summary>
        public string InputPath { get; init; } = string.Empty;
    }

    /// <summary>
    /// 发布（把产物移出工作区）的结果。
    /// </summary>
    public sealed class WorkspacePublishResult
    {
        public bool Success { get; init; }

        public string DestinationPath { get; init; } = string.Empty;

        public int MovedFileCount { get; init; }

        /// <summary>给人看的一句话；**重命名过的文件也写在这里**，让用户知道产物不是原样落进去的。</summary>
        public string Message { get; init; } = string.Empty;
    }

    /// <summary>
    /// 递归解压的工作区：所有中间产物先落在这里，确认无误后再"发布"到最终目录。
    ///
    /// 为什么要这么一层（设计.md §十三/§十四、AGENTS.md §6 第 12 条）：
    /// ① 递归展开会解出**很多**中间文件，直接往最终目录写会让用户在失败时收到一堆半成品；
    /// ② 每层都在自己的目录里，失败能定位到层、能从那一层重试；
    /// ③ 只有"全部完成"才发布 —— 部分完成时产物留在工作区，用户能看、能接着处理，不会被半成品污染目标目录。
    ///
    /// 安全边界（本类会删目录，所以每一条都写死在这里）：
    /// 1. <c>RootDirectory</c> 为空直接抛 —— 那会把工作区建在进程当前目录，属于调用方的编程错误；
    /// 2. <c>taskId</c> 必须清洗成**单个目录名**，否则 <c>".."</c> / <c>"a\..\..\b"</c> 这类值
    ///    能让"工作区"落到 root 之外，后续 Cleanup 就会删掉用户的东西；
    /// 3. <c>Cleanup</c> 删之前再规范化比较一次，确认确实在 root 之下，不在就**什么都不删**；
    /// 4. <c>Publish</c> 移动时同名绝不覆盖（AutoRename），移动用的 File.Move 两参数版本
    ///    在目标存在时也会抛，是最后一道保险。
    /// </summary>
    public sealed class ExtractionWorkspace
    {
        /// <summary>工作区目录名前缀。带序号且补零，是为了在资源管理器里天然按层序排好。</summary>
        private const string LayerDirectoryPrefix = "layer-";

        /// <summary>每层的产物子目录名。固定叫 output，方便"某一层的产物"被直接指认。</summary>
        private const string OutputDirectoryName = "output";

        /// <summary>report.json 的文件名（崩溃后恢复时按这个约定去找）。</summary>
        private const string ReportFileName = "report.json";

        /// <summary>
        /// 摊平"无意义外壳"的最大层数。
        /// 不设成无限：外壳通常只有一层，真的套了十层说明这个包的目录结构本身有含义
        /// （例如按日期分层的备份包），继续摊只会把用户的目录结构拆散。
        /// </summary>
        private const int MaxWrapperStripDepth = 3;

        private readonly List<WorkspaceLayer> _layers = new();

        /// <summary>创建并准备一个工作区（<c>&lt;rootDirectory&gt;\&lt;清洗后的 taskId&gt;</c> 会被建出来）。</summary>
        /// <exception cref="ArgumentException">
        /// <paramref name="rootDirectory"/> 为空。这是**调用方的编程错误**，不是用户输入错误 ——
        /// 用户输入的空目录应该在更上层就被拦住并给出可读提示，走到这里说明上层漏了校验，
        /// 此时静默继续只会把产物写到一个没人知道的地方，所以必须当场炸出来。
        /// </exception>
        public ExtractionWorkspace(string rootDirectory, string taskId)
        {
            if (string.IsNullOrWhiteSpace(rootDirectory))
            {
                throw new ArgumentException("工作区根目录不能为空", nameof(rootDirectory));
            }

            RootDirectory = SafePathHelper.GetFullPathSafe(rootDirectory);

            // 只取文件名部分、并清洗非法字符与保留名，保证 TaskDirectory 一定是 root 的**直接子目录**。
            string safeTaskId = FileNameHelper.SanitizeFileName(taskId);

            TaskDirectory = Path.Combine(RootDirectory, safeTaskId);

            if (!SafePathHelper.EnsureDirectoryExists(TaskDirectory))
            {
                // 建不出来说明这个位置根本不可写（磁盘满 / 权限 / 路径非法）。
                // 与其让后面每一层都失败一遍，不如在这里一次性说清。
                throw new IOException($"无法创建工作区目录：{TaskDirectory}");
            }
        }

        /// <summary>工作区根目录（由调用方指定，通常是某个临时目录）。</summary>
        public string RootDirectory { get; }

        /// <summary>本任务的工作区目录：<c>&lt;root&gt;\&lt;清洗后的 taskId&gt;</c>。</summary>
        public string TaskDirectory { get; }

        /// <summary>已经创建的层，按层号升序。</summary>
        public IReadOnlyList<WorkspaceLayer> Layers => _layers;

        /// <summary>
        /// 创建下一层目录（layer-000、layer-001…）并返回。
        /// 层号取 <see cref="Layers"/>.Count，所以"层号连续、不跳号"是结构上保证的。
        /// </summary>
        public WorkspaceLayer CreateNextLayer(string inputArchivePath)
        {
            int depth = _layers.Count;

            string layerDirectory = Path.Combine(
                TaskDirectory,
                LayerDirectoryPrefix + depth.ToString("D3"));

            string outputDirectory = Path.Combine(layerDirectory, OutputDirectoryName);

            if (!SafePathHelper.EnsureDirectoryExists(outputDirectory))
            {
                throw new IOException($"无法创建工作区层目录：{outputDirectory}");
            }

            var layer = new WorkspaceLayer
            {
                Depth = depth,
                DirectoryPath = layerDirectory,
                OutputPath = outputDirectory,
                InputPath = inputArchivePath ?? string.Empty
            };

            _layers.Add(layer);

            return layer;
        }

        /// <summary>
        /// 把最终产物"发布"到 <paramref name="targetDirectory"/>（**移动**，绝不覆盖）。
        ///
        /// 发布内容 = 最后一层 output 目录里的东西；但如果它里面**恰好只有一个子目录、没有任何同级文件**，
        /// 那一层就是这个包自带的"无意义外壳"（典型：<c>out\pack\pack\文件</c>），
        /// 只去掉这一层外壳再发布，避免用户拿到 out\pack\pack\ 这种套娃目录。
        /// 摊平最多 <see cref="MaxWrapperStripDepth"/> 层，且**每层都要重新满足**
        /// "只有一个子目录、没有文件、不是符号链接"才继续摊 —— 有文件说明这一层本身就是有内容的产物层。
        ///
        /// <paramref name="targetDirectory"/> 必须由调用方给出**完整目标位置**
        /// （本方法不会替它再拼一层包名）：递归模式下"最后一层 output"与"统一目标目录"的关系
        /// 由调用方决定，工作区不替用户拍板。
        /// </summary>
        public WorkspacePublishResult Publish(string targetDirectory)
        {
            if (string.IsNullOrWhiteSpace(targetDirectory))
            {
                return PublishFailure(string.Empty, "未指定发布目标目录");
            }

            if (_layers.Count == 0)
            {
                return PublishFailure(string.Empty, "工作区里还没有任何一层，没有可发布的产物");
            }

            string sourceDirectory = _layers[^1].OutputPath;
            string destination = SafePathHelper.GetFullPathSafe(targetDirectory);

            if (!SafeDirectoryExists(sourceDirectory))
            {
                return PublishFailure(destination, $"最后一层的产物目录不存在：{sourceDirectory}");
            }

            try
            {
                string contentRoot = ResolveContentRoot(sourceDirectory);

                // 目标不存在就建；建不出来就没必要继续，否则每个文件都要失败一遍。
                if (!SafePathHelper.EnsureDirectoryExists(destination))
                {
                    return PublishFailure(destination, $"发布目标目录不存在且创建失败：{destination}");
                }

                var renamed = new List<string>();
                var errors = new List<string>();

                int movedCount = MoveContent(contentRoot, destination, renamed, errors);

                return new WorkspacePublishResult
                {
                    Success = movedCount > 0,
                    DestinationPath = destination,
                    MovedFileCount = movedCount,
                    Message = BuildPublishMessage(destination, movedCount, renamed, errors)
                };
            }
            catch (Exception ex)
            {
                // 发布跑在任务收尾阶段，任何意外都不能把已经解压好的产物带崩（AGENTS.md §6 第 9 条）。
                return PublishFailure(destination, $"发布过程中出现意外错误：{ex.Message}");
            }
        }

        /// <summary>
        /// 删除工作区。
        ///
        /// 只在调用方**确认后**调用（AGENTS.md §6 第 13 条：清工作区必须先经用户确认）。
        /// 两道防线：
        /// ① 删之前用规范化的完整路径确认 <see cref="TaskDirectory"/> 确实位于 <see cref="RootDirectory"/> 之下
        ///    （相等也不行 —— 那等于把整个 root 端掉），不满足就**什么都不删**直接返回；
        /// ② 目录不存在时直接返回，不去"顺手"删父目录。
        /// 删非空目录用递归删除（工作区本来就是自己造的，里面全是本任务的中间产物）；
        /// 删不掉（占用 / 权限）只吞掉不抛 —— 清工作区失败不该让已经成功的任务变成失败。
        /// </summary>
        public void Cleanup()
        {
            if (!IsInsideRoot(TaskDirectory))
            {
                // 注意：这里**不抛异常**。路径越界意味着工作区的身份本身可疑，
                // 此时最有价值的动作就是"一个字节都不动"，让用户自己去看。
                return;
            }

            try
            {
                if (Directory.Exists(TaskDirectory))
                {
                    Directory.Delete(TaskDirectory, recursive: true);
                }
            }
            catch
            {
                // 目录里有文件被占用、权限不足等等：留着就是了，不影响任务结论。
            }

            _layers.Clear();
        }

        /// <summary>
        /// 写 report.json（给"崩溃后可恢复"用，字段自己定，但必须是可读的 JSON）。
        ///
        /// 两条硬约束：
        /// ① **绝不写密码**（AGENTS.md §6 第 5 条）—— 报告只记结构和数量，调用方传进来的对象里
        ///    也不该有明文密码，本方法不做任何"帮你去掉密码"的补救；
        /// ② 写不进去（磁盘满 / 权限不足 / 序列化失败）**吞掉异常**：报告是给恢复用的辅助品，
        ///    写不出来不该让整个已经跑完的任务失败。
        /// </summary>
        public void WriteReport(object report)
        {
            if (report == null)
            {
                return;
            }

            try
            {
                if (!SafePathHelper.EnsureDirectoryExists(TaskDirectory))
                {
                    return;
                }

                string json = JsonSerializer.Serialize(report, ReportSerializerOptions);

                File.WriteAllText(
                    Path.Combine(TaskDirectory, ReportFileName),
                    json,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }
            catch
            {
                // 见方法注释：报告写不出来是"少一条恢复线索"，不是"任务失败"。
            }
        }

        private static readonly JsonSerializerOptions ReportSerializerOptions = new()
        {
            WriteIndented = true,

            // 中文状态字符串、路径都不转成 \uXXXX：报告是给人看和给恢复逻辑读的，可读性优先。
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,

            DefaultIgnoreCondition = JsonIgnoreCondition.Never
        };

        /// <summary>规范化后判断 candidate 是否等于 root、或位于 root 之下（Windows 下忽略大小写）。</summary>
        private bool IsInsideRoot(string? candidate)
        {
            string fullCandidate = NormalizeForCompare(candidate);
            string fullRoot = NormalizeForCompare(RootDirectory);

            if (fullCandidate.Length == 0 || fullRoot.Length == 0)
            {
                return false;
            }

            // 与 root 相同也算"不在其下"：那等于删掉整个根目录（别的任务的工作区可能就在旁边）。
            if (string.Equals(fullCandidate, fullRoot, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return fullCandidate.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 解析真正要发布的内容根目录（去掉无意义外壳）。
        /// 摊平条件苛刻是有意的：只要出现"多个条目"或"一个文件"，就说明当前目录本身是有含义的。
        /// </summary>
        private static string ResolveContentRoot(string outputDirectory)
        {
            string current = outputDirectory;

            for (int i = 0; i < MaxWrapperStripDepth; i++)
            {
                string[] entries;

                try
                {
                    entries = Directory.GetFileSystemEntries(current);
                }
                catch
                {
                    return current;
                }

                if (entries.Length != 1)
                {
                    return current;
                }

                string onlyEntry = entries[0];

                if (!TryGetAttributes(onlyEntry, out FileAttributes attributes))
                {
                    return current;
                }

                if ((attributes & FileAttributes.Directory) == 0)
                {
                    return current;
                }

                // 外壳是符号链接 / 联接点时不当外壳：跟着它搬等于把别处的文件挪走。
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    return current;
                }

                current = onlyEntry;
            }

            return current;
        }

        /// <summary>把内容根目录下的条目搬进目标目录，返回真正移动成功的文件数。</summary>
        private static int MoveContent(
            string sourceDirectory,
            string destinationDirectory,
            List<string> renamed,
            List<string> errors)
        {
            string[] entries;

            try
            {
                entries = Directory.GetFileSystemEntries(sourceDirectory);
            }
            catch (Exception ex)
            {
                errors.Add($"无法读取产物目录（{ex.Message}）");
                return 0;
            }

            int movedCount = 0;

            foreach (string entry in entries)
            {
                if (!TryGetAttributes(entry, out FileAttributes attributes))
                {
                    errors.Add($"{Path.GetFileName(entry)}（读不到文件属性）");
                    continue;
                }

                // 不跟随符号链接 / 联接点：跟着走会把别处的文件挪走，还可能绕圈。
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    errors.Add($"{Path.GetFileName(entry)}（是符号链接/联接点，未移动）");
                    continue;
                }

                string entryName = Path.GetFileName(entry);

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    movedCount += MoveContent(entry, Path.Combine(destinationDirectory, entryName), renamed, errors);
                    continue;
                }

                if (TryMoveFile(entry, destinationDirectory, renamed, errors))
                {
                    movedCount++;
                }
            }

            // 这一层已经搬空了就顺手删掉（只用非递归重载：目录里还有东西就必然抛，删不掉非空目录）。
            TryDeleteDirectoryIfEmpty(sourceDirectory);

            return movedCount;
        }

        private static bool TryMoveFile(
            string sourceFile,
            string destinationDirectory,
            List<string> renamed,
            List<string> errors)
        {
            string fileName = Path.GetFileName(sourceFile);
            string targetPath = Path.Combine(destinationDirectory, fileName);

            try
            {
                if (!SafePathHelper.EnsureDirectoryExists(destinationDirectory))
                {
                    errors.Add($"{fileName}（无法创建目标目录）");
                    return false;
                }

                if (File.Exists(targetPath) || Directory.Exists(targetPath))
                {
                    // 目标同名：换成 名字(1).ext 并记下来，**绝不覆盖**（AGENTS.md §6 第 3 条）。
                    string renamedPath = SafePathHelper.AutoRenameFilePath(targetPath);

                    if (!string.Equals(renamedPath, targetPath, StringComparison.OrdinalIgnoreCase))
                    {
                        renamed.Add($"{fileName} → {Path.GetFileName(renamedPath)}");
                    }

                    targetPath = renamedPath;
                }

                /*
                 * 两参数 File.Move 在目标已存在时直接抛 IOException，**不会覆盖**。
                 * 这是最后一道保险：即使 AutoRename 与 Move 之间有人往目标目录里塞了同名文件，
                 * 也只会少移动一个文件并记进 Message，不会把别人的文件冲掉。
                 */
                File.Move(sourceFile, targetPath);
                return true;
            }
            catch (Exception ex)
            {
                errors.Add($"{fileName}（{ex.Message}）");
                return false;
            }
        }

        private static string BuildPublishMessage(
            string destination,
            int movedCount,
            List<string> renamed,
            List<string> errors)
        {
            var parts = new List<string>();

            parts.Add(movedCount > 0
                ? $"已发布 {movedCount} 个文件到 {destination}"
                : "没有可发布的产物文件");

            if (renamed.Count > 0)
            {
                parts.Add($"其中 {renamed.Count} 个同名文件已改名，未覆盖原有文件（{string.Join("、", renamed)}）");
            }

            if (errors.Count > 0)
            {
                parts.Add($"{errors.Count} 个文件没能移动（{string.Join("；", errors)}）");
            }

            return string.Join("；", parts);
        }

        private static WorkspacePublishResult PublishFailure(string destination, string message)
        {
            return new WorkspacePublishResult
            {
                Success = false,
                DestinationPath = destination,
                MovedFileCount = 0,
                Message = message
            };
        }

        /// <summary>尽力删除已经空掉的目录：删不掉（非空 / 被占用 / 没权限）一律当没发生。</summary>
        private static void TryDeleteDirectoryIfEmpty(string directory)
        {
            try
            {
                if (!Directory.Exists(directory))
                {
                    return;
                }

                if (Directory.GetFileSystemEntries(directory).Length > 0)
                {
                    return;
                }

                Directory.Delete(directory, recursive: false);
            }
            catch
            {
                // 空目录删不掉不影响发布结果，留给用户自己收拾。
            }
        }

        private static string NormalizeForCompare(string? path)
        {
            return SafePathHelper.GetFullPathSafe(path)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        private static bool SafeDirectoryExists(string? path)
        {
            try
            {
                return !string.IsNullOrWhiteSpace(path) && Directory.Exists(path);
            }
            catch
            {
                return false;
            }
        }

        private static bool TryGetAttributes(string path, out FileAttributes attributes)
        {
            try
            {
                attributes = File.GetAttributes(path);
                return true;
            }
            catch
            {
                attributes = default;
                return false;
            }
        }
    }
}
