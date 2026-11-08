using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ArchiveFixer.Detection;
using ArchiveFixer.Engines;
using ArchiveFixer.Engines.SevenZip;
using ArchiveFixer.Extraction;
using ArchiveFixer.Models;
using ArchiveFixer.Password;
using ArchiveFixer.Services;
using ArchiveFixer.ViewModels;
using Xunit;

namespace ArchiveFixer.Tests
{
    [Collection("ArchiveFixerGlobalState")]
    public sealed class FinalGroupFailureTests
    {
        [Theory]
        [InlineData("915000041", "FinalGroupIncomplete", RestHandlingModes.Keep, false)]
        [InlineData("915000042", "FinalGroupIncomplete", RestHandlingModes.Keep, false)]
        [InlineData("915000043", "FinalGroupIncomplete", RestHandlingModes.Keep, false)]
        [InlineData("915000044", "FinalGroupIncomplete", RestHandlingModes.Keep, false)]
        [InlineData("915000045", "FinalGroupIncomplete", RestHandlingModes.Keep, false)]
        [InlineData("915000046", "FinalGroupCorrupted", RestHandlingModes.Keep, false)]
        [InlineData("915000047", "FinalGroupCorrupted", RestHandlingModes.Keep, false)]
        [InlineData("915000048", "FinalGroupCorrupted", RestHandlingModes.Keep, false)]
        [InlineData("915000049", "FinalGroupCorrupted", RestHandlingModes.Keep, false)]
        [InlineData("915000050", "FinalGroupCorrupted", RestHandlingModes.Keep, false)]
        [InlineData("915000046", "FinalGroupCorrupted", RestHandlingModes.Delete, false)]
        [InlineData("915000049", "FinalGroupCorrupted", RestHandlingModes.Delete, false)]
        public async Task Probe(string sample, string expectedReason, string restHandling, bool spaceTight)
        {
            const string backupRoot = @"H:\BaiduNetdiskDownload\困难大测试\大测试备份位置\915000000-备份";
            string backup = Path.Combine(backupRoot, sample, "原包");
            if (!Directory.Exists(backup)) return;

            string root = Path.Combine(@"E:\DeepSeekProjects\_tmp\ArchiveFixer\root-probe", Guid.NewGuid().ToString("N"));
            string source = Path.Combine(root, "src");
            string data = Path.Combine(root, "data");
            string target = Path.Combine(root, "out");
            Directory.CreateDirectory(data);
            Directory.CreateDirectory(target);
            Copy(backup, source);
            File.WriteAllText(Path.Combine(data, "passwords.txt"),
                "2中！0@文#2删￥6除%\nDemo#Pass1\nDemo#Pass2\n", new UTF8Encoding(false));

            ToolLocator.Default.CustomSevenZipExePath = SevenZipFactAttribute.LocateSevenZipPath()
                ?? throw new InvalidOperationException("no 7z");
            ToolLocator.Default.Invalidate();
            var ps = new PathService { DataRootDirectory = data };
            var ss = new SettingsService(ps);
            AppSettings s = AppSettings.CreateDefault();
            s.CustomOutputDirectory = target;
            s.SourceHandling = nameof(SourceHandlingMode.KeepInPlace);
            s.RestHandlingAfterVerify = restHandling;
            s.KeepFailedWorkspace = true;
            ss.Save(s);
            var engine = new SevenZipEngine();
            var pw = new PasswordService { DataRootDirectory = data, UseTextStore = true };
            var vm = new MainViewModel(new FileScanService(), new ArchiveDetectService(), new RenameService(),
                engine, pw, new LogService(ps), ss, ps, new TaskSummaryService(),
                new ClipboardService(), new DialogService(), useTextStore: true, encryptTextStore: false);
            var coordinator = new ExtractionCoordinator(vm, engine, pw, ps, new DialogService());
            vm.SpaceTightMode = spaceTight;
            if (spaceTight) { coordinator.SpaceProbeOverride = _ => 1; coordinator.SpaceReserveOverride = 0; }
            var scan = new ScanCoordinator(vm, new FileScanService(), new ArchiveDetectService(), new DialogService());
            var rename = new RenameCoordinator(vm, scan, new RenameService(), new DialogService());
            var one = new OneClickCoordinator(vm, scan, rename, coordinator, new DialogService());
            one.OptionsPromptOverride = _ => OneClickOptionsPrompt.Confirmed(new OneClickRunOptions
            {
                PlacementMode = OutputPlacementMode.CustomRootPerArchive,
                CustomRoot = target,
                SourceHandling = SourceHandlingMode.KeepInPlace,
                RestHandling = restHandling
            });
            await vm.AddPathsAsync(new[] { source }, ImportMode.Replace);
            await one.RunAsync().WaitAsync(TimeSpan.FromMinutes(3));

            ArchiveTask[] tasks = vm.Tasks.ToArray();
            Assert.Contains(tasks, t => t.Reason.ToString() == expectedReason);
            Assert.DoesNotContain(tasks, t => t.Reason.ToString() == expectedReason && t.Outcome == TaskOutcome.Skipped);
            Assert.True(Directory.Exists(Path.Combine(source, "aaa")), "失败时源包目录必须保留");
            Assert.DoesNotContain(
                tasks,
                task => task.Reason.ToString() == expectedReason
                        && !task.IsContentGroupProcessTask
                        && task.ContentGroupFinalState == FinalContentState.Verified);

            if (expectedReason == "FinalGroupCorrupted")
            {
                ArchiveTask failed = tasks.First(t => t.Reason == TaskReason.FinalGroupCorrupted);

                if (spaceTight) { Assert.True(string.IsNullOrWhiteSpace(failed.LastProcessArtifactPath) || !File.Exists(failed.LastProcessArtifactPath), "空间不足不保留当前过程物"); }
                else if (restHandling == RestHandlingModes.Keep)
                {
                    Assert.False(string.IsNullOrWhiteSpace(failed.LastProcessArtifactPath), "Keep 必须落当前过程物路径");
                    Assert.True(File.Exists(failed.LastProcessArtifactPath), "Keep 当前过程物必须存在");
                }
                else
                {
                    Assert.True(
                        string.IsNullOrWhiteSpace(failed.LastProcessArtifactPath)
                        || !File.Exists(failed.LastProcessArtifactPath),
                        "Delete 当前过程物必须不存在");
                }
            }
        }

        private static void Copy(string s, string d)
        {
            Directory.CreateDirectory(d);
            foreach (string x in Directory.GetDirectories(s, "*", SearchOption.AllDirectories))
                Directory.CreateDirectory(Path.Combine(d, Path.GetRelativePath(s, x)));
            foreach (string x in Directory.GetFiles(s, "*", SearchOption.AllDirectories))
            {
                string p = Path.Combine(d, Path.GetRelativePath(s, x));
                Directory.CreateDirectory(Path.GetDirectoryName(p)!);
                File.Copy(x, p);
            }
        }
    }
}



