using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using ArchiveFixer.Engines;
using ArchiveFixer.Engines.SevenZip;
using ArchiveFixer.Extraction;
using ArchiveFixer.Models;
using ArchiveFixer.Password;
using ArchiveFixer.Services;
using ArchiveFixer.ViewModels;
using Microsoft.Win32.SafeHandles;

namespace ArchiveFixer.Tests.Batch120
{
    /// <summary>
    /// 120 批的**操作代码**：按每份样本 recipe 里写的场景，真实地把它交给 ArchiveFixer 跑一遍。
    ///
    /// 它只负责"跑"，不负责"判"。跑完把现场落成一份运行记录，检测代码只读那份记录 + recipe。
    ///
    /// 每个类别要摆的档位（别的设置一律不动）：
    /// · 普通 50：落点 / 源包 / 最后一层过程物，来自 recipe.scenario（2×2×2 = 8 种）
    /// · 特定 20：落点来自 recipe.placement，并打开特定解压（UseSpecificExtraction）
    /// · 异常 25：落点 Original
    /// · 空间 25：落点 Original，打开空间不足模式，并按 recipe.space 注入可用字节与保留余量；
    ///           B 批次（kind=SpecificBatch）同样打开特定解压
    /// </summary>
    internal static class Batch120Operation
    {
        // 种子从环境变量来：ARCHIVEFIXER_120_SEED=918000000 就能换一批，缺省仍是 917000000。
        public static readonly string Seed =
            Environment.GetEnvironmentVariable("ARCHIVEFIXER_120_SEED") is { Length: > 0 } fromEnv ? fromEnv : "917000000";
        // 全部落在 E 盘：H 盘已确认有坏区，样本/测试区/指定位置都不再往那写。
        public static readonly string BatchRoot = Environment.GetEnvironmentVariable("ARCHIVEFIXER_120_ROOT") is { Length: > 0 } rootOverride
            ? rootOverride
            : @"E:\DeepSeekProjects\ArchiveFixer\_tmp\困难大测试";
        public static readonly string BackupBatchRoot = Path.Combine(BatchRoot, Seed + "-120批次-备份");
        public static readonly string TestBatchRoot = Path.Combine(BatchRoot, "测试区", Seed);
        public static readonly string SpecifiedBatchRoot = Path.Combine(BatchRoot, "测试区指定位置", Seed + "-指定位置");
        public static readonly string RecordRoot = $@"E:\AF\120批操作\{Seed}";

        private const string PasswordBook = "2中！0@文#2删￥6除%\nDemo#Pass1\nDemo#Pass2\n";

        public static async Task<Batch120RunRecord> RunOneAsync(string sampleId)
        {
            string backupSample = Path.Combine(BackupBatchRoot, sampleId);
            string backupPackage = Path.Combine(backupSample, "原包");
            string recipePath = Path.Combine(backupSample, "recipe.json");
            if (!Directory.Exists(backupPackage))
            {
                throw new InvalidOperationException($"缺少样本原包：{backupPackage}");
            }

            if (!File.Exists(recipePath))
            {
                throw new InvalidOperationException($"缺少 recipe：{recipePath}");
            }

            JsonNode recipe = JsonNode.Parse(File.ReadAllText(recipePath, Encoding.UTF8))!;
            string category = recipe["category"]?.GetValue<string>() ?? string.Empty;
            string kind = recipe["kind"]?.GetValue<string>() ?? string.Empty;
            string placement = ReadPlacement(recipe);
            string sourceHandling = ReadSourceHandling(recipe);
            string contentHandling = ReadContentHandling(recipe);
            bool useSpecific = category == "Specific" || kind == "SpecificBatch";
            bool spaceTight = category == "SpaceTight";
            long spaceFreeBytes = 0;
            long spaceReserveBytes = 0;
            if (spaceTight && recipe["space"] != null)
            {
                spaceFreeBytes = recipe["space"]!["freeBytes"]?.GetValue<long>() ?? 0;
                spaceReserveBytes = recipe["space"]!["reserveBytes"]?.GetValue<long>() ?? 0;
            }

            string workspace = Path.Combine(TestBatchRoot, sampleId);
            string specifiedRoot = Path.Combine(SpecifiedBatchRoot, sampleId);
            ResetDirectory(workspace, TestBatchRoot);
            ResetDirectory(specifiedRoot, SpecifiedBatchRoot);
            Directory.CreateDirectory(workspace);
            Directory.CreateDirectory(specifiedRoot);
            CopyDirectory(backupPackage, Path.Combine(workspace, "原包"));
            File.Copy(recipePath, Path.Combine(workspace, "recipe.json"), overwrite: true);

            string dataRoot = Path.Combine(Path.GetTempPath(), "ArchiveFixerBatch120", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dataRoot);
            File.WriteAllText(Path.Combine(dataRoot, "passwords.txt"), PasswordBook, new UTF8Encoding(false));

            var record = new Batch120RunRecord
            {
                SampleId = sampleId,
                Category = category,
                Placement = placement,
                SourceHandling = sourceHandling,
                RestHandling = contentHandling,
                UseSpecificExtraction = useSpecific,
                SpaceTightMode = spaceTight,
                InjectedFreeBytes = spaceFreeBytes,
                Workspace = workspace,
                SpecifiedRoot = specifiedRoot,
                StartedAt = DateTimeOffset.Now
            };

            try
            {
                ToolLocator.Default.CustomSevenZipExePath = LocateSevenZip();
                ToolLocator.Default.Invalidate();

                var pathService = new PathService { DataRootDirectory = dataRoot };
                var settingsService = new SettingsService(pathService);
                AppSettings settings = AppSettings.CreateDefault();
                settings.ExtractToOriginalDirectory = placement == "Original";
                settings.CustomOutputDirectory = placement == "Original" ? string.Empty : specifiedRoot;
                settings.SourceHandling = sourceHandling;
                settings.RestHandlingAfterVerify = contentHandling;
                settings.UseSpecificExtraction = useSpecific;
                settings.SkipOneClickConfirm = true;
                settingsService.Save(settings);

                var engine = new SevenZipEngine();
                var password = new PasswordService { DataRootDirectory = dataRoot, UseTextStore = true };
                var vm = new MainViewModel(
                    new FileScanService(), new ArchiveDetectService(), new RenameService(),
                    engine, password, new LogService(pathService), settingsService, pathService,
                    new TaskSummaryService(), new ClipboardService(), new DialogService(),
                    useTextStore: true, encryptTextStore: false);
                var extraction = new ExtractionCoordinator(vm, engine, password, pathService, new DialogService());
                var scan = new ScanCoordinator(vm, new FileScanService(), new ArchiveDetectService(), new DialogService());
                var rename = new RenameCoordinator(vm, scan, new RenameService(), new DialogService());
                var oneClick = new OneClickCoordinator(vm, scan, rename, extraction, new DialogService());
                oneClick.OptionsPromptOverride = _ => OneClickOptionsPrompt.NotShown();

                if (spaceTight)
                {
                    extraction.SpaceProbeOverride = _ => spaceFreeBytes;
                    extraction.SpaceReserveOverride = spaceReserveBytes;
                    vm.SpaceTightMode = true;
                    vm.SpaceTightKeepSource = true;
                }

                record.Before = Snapshot(workspace, specifiedRoot);

                var stopwatch = Stopwatch.StartNew();
                await vm.AddPathsAsync(new[] { Path.Combine(workspace, "原包") }, ImportMode.Replace);
                await oneClick.RunAsync().WaitAsync(TimeSpan.FromMinutes(5));
                stopwatch.Stop();
                record.ElapsedMs = stopwatch.ElapsedMilliseconds;

                record.Tasks = vm.Tasks.Select(t => new Batch120TaskRecord
                {
                    Name = t.DisplayFileName,
                    Path = t.DisplayPath,
                    Outcome = t.Outcome.ToString(),
                    Reason = t.Reason.ToString(),
                    Status = t.Status,
                    StatusDisplay = t.StatusDisplayText,
                    FinalContentPath = t.FinalContentPath,
                    FinalOutputPath = t.FinalOutputPath,
                    Error = t.ErrorMessage,
                    ProducedFiles = t.ProducedFiles.ToArray()
                }).ToArray();
            }
            catch (Exception ex)
            {
                record.Error = ex.GetType().Name + ": " + ex.Message;
            }
            finally
            {
                record.FinishedAt = DateTimeOffset.Now;
                record.After = Snapshot(workspace, specifiedRoot);
                try
                {
                    Directory.Delete(dataRoot, recursive: true);
                }
                catch
                {
                    // 临时数据目录清不掉不影响结论。
                }
            }

            Directory.CreateDirectory(RecordRoot);
            string recordPath = Path.Combine(RecordRoot, sampleId + ".json");
            File.WriteAllText(recordPath, JsonSerializer.Serialize(record, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
            return record;
        }

        private static string ReadPlacement(JsonNode recipe)
        {
            JsonNode? scenario = recipe["scenario"];
            if (scenario != null && scenario["placement"] != null)
            {
                return scenario["placement"]!.GetValue<string>();
            }

            return recipe["placement"]?.GetValue<string>() ?? "Original";
        }

        private static string ReadSourceHandling(JsonNode recipe)
        {
            JsonNode? scenario = recipe["scenario"];
            string value = scenario?["sourceHandling"]?.GetValue<string>() ?? "KeepInPlace";
            return value == "DeleteInPlace" ? "DeleteInPlace" : "KeepInPlace";
        }

        private static string ReadContentHandling(JsonNode recipe)
        {
            JsonNode? scenario = recipe["scenario"];
            string value = scenario?["contentHandling"]?.GetValue<string>() ?? "Keep";
            return value == "Delete" ? "Delete" : "Keep";
        }

        private static List<Batch120FileRecord> Snapshot(string workspace, string specifiedRoot)
        {
            var files = new List<Batch120FileRecord>();
            Collect(workspace, "工作区", files);
            Collect(specifiedRoot, "指定位置", files);
            files.Sort((a, b) => string.CompareOrdinal(a.Root + "|" + a.Path, b.Root + "|" + b.Path));
            return files;
        }

        private static void Collect(string root, string label, List<Batch120FileRecord> sink)
        {
            if (!Directory.Exists(root))
            {
                return;
            }

            foreach (string file in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
            {
                sink.Add(new Batch120FileRecord
                {
                    Root = label,
                    Path = Path.GetRelativePath(root, file),
                    Bytes = new FileInfo(file).Length
                });
            }
        }

        private static void ResetDirectory(string path, string allowedParent)
        {
            if (!Directory.Exists(path))
            {
                return;
            }

            string full = Path.GetFullPath(path);
            string parent = Path.GetFullPath(allowedParent) + Path.DirectorySeparatorChar;
            if (!full.StartsWith(parent, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"拒绝删除不属于测试根目录的路径：{full}");
            }

            Directory.Delete(full, recursive: true);
        }

        /// <summary>
        /// 复制样本到工作区，并**保留硬链接**。
        /// 有些样本靠"同一物理文件两个名字"来测去重，用 File.Copy 逐份写会把链接身份拆成两个独立文件。
        /// </summary>
        private static void CopyDirectory(string source, string destination)
        {
            Directory.CreateDirectory(destination);
            foreach (string dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
            {
                Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, dir)));
            }

            var firstCopyByFileId = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            {
                string target = Path.Combine(destination, Path.GetRelativePath(source, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);

                string? fileId = TryGetFileId(file);
                if (fileId != null && firstCopyByFileId.TryGetValue(fileId, out string? firstCopy))
                {
                    if (HardLink.TryCreate(target, firstCopy))
                    {
                        continue;
                    }
                }

                File.Copy(file, target, overwrite: true);
                if (fileId != null)
                {
                    firstCopyByFileId[fileId] = target;
                }
            }
        }

        private static string? TryGetFileId(string path)
        {
            using SafeFileHandle handle = HardLink.OpenRead(path);
            if (handle.IsInvalid)
            {
                return null;
            }

            if (!HardLink.TryGetInformation(handle, out uint volume, out uint indexHigh, out uint indexLow))
            {
                return null;
            }

            return $"{volume}:{indexHigh}:{indexLow}";
        }

        private static string LocateSevenZip()
        {
            DirectoryInfo? dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "ArchiveFixer.slnx")))
                {
                    string candidate = Path.Combine(dir.FullName, "src", "ArchiveFixer", "tools", "7zip", "7z.exe");
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }

                dir = dir.Parent;
            }

            throw new InvalidOperationException("找不到内置 7z.exe。");
        }

        private static class HardLink
        {
            private const uint FileReadAttributes = 0x80;
            private const uint ShareReadWriteDelete = 0x1 | 0x2 | 0x4;
            private const uint OpenExisting = 3;

            public static SafeFileHandle OpenRead(string path) =>
                CreateFile(path, FileReadAttributes, ShareReadWriteDelete, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);

            public static bool TryGetInformation(SafeFileHandle handle, out uint volume, out uint indexHigh, out uint indexLow)
            {
                volume = 0;
                indexHigh = 0;
                indexLow = 0;
                if (!GetFileInformationByHandle(handle, out ByHandleFileInformation info))
                {
                    return false;
                }

                volume = info.VolumeSerialNumber;
                indexHigh = info.FileIndexHigh;
                indexLow = info.FileIndexLow;
                return true;
            }

            public static bool TryCreate(string linkPath, string existingPath)
            {
                try
                {
                    return CreateHardLink(linkPath, existingPath, IntPtr.Zero);
                }
                catch
                {
                    return false;
                }
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct ByHandleFileInformation
            {
                public uint FileAttributes;
                public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
                public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
                public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
                public uint VolumeSerialNumber;
                public uint FileSizeHigh;
                public uint FileSizeLow;
                public uint NumberOfLinks;
                public uint FileIndexHigh;
                public uint FileIndexLow;
            }

            [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
            private static extern SafeFileHandle CreateFile(
                string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
                uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern bool GetFileInformationByHandle(
                SafeFileHandle file, out ByHandleFileInformation information);

            [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
            private static extern bool CreateHardLink(
                string fileName, string existingFileName, IntPtr securityAttributes);
        }
    }

    internal sealed class Batch120RunRecord
    {
        public string SampleId { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public string Placement { get; set; } = string.Empty;
        public string SourceHandling { get; set; } = string.Empty;
        public string RestHandling { get; set; } = string.Empty;
        public bool UseSpecificExtraction { get; set; }
        public bool SpaceTightMode { get; set; }
        public long InjectedFreeBytes { get; set; }
        public string Workspace { get; set; } = string.Empty;
        public string SpecifiedRoot { get; set; } = string.Empty;
        public DateTimeOffset StartedAt { get; set; }
        public DateTimeOffset FinishedAt { get; set; }
        public long ElapsedMs { get; set; }
        public List<Batch120FileRecord> Before { get; set; } = new();
        public List<Batch120FileRecord> After { get; set; } = new();
        public Batch120TaskRecord[] Tasks { get; set; } = Array.Empty<Batch120TaskRecord>();
        public string? Error { get; set; }
    }

    internal sealed class Batch120FileRecord
    {
        public string Root { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public long Bytes { get; set; }
    }

    internal sealed class Batch120TaskRecord
    {
        public string Name { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public string Outcome { get; set; } = string.Empty;
        public string Reason { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public string StatusDisplay { get; set; } = string.Empty;
        public string FinalContentPath { get; set; } = string.Empty;
        public string FinalOutputPath { get; set; } = string.Empty;
        public string? Error { get; set; }
        public string[] ProducedFiles { get; set; } = Array.Empty<string>();
    }
}
