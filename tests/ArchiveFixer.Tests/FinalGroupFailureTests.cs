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
        [InlineData("915000041", "FinalGroupIncomplete")]
        [InlineData("915000049", "FinalGroupCorrupted")]
        public async Task Probe(string sample, string expectedReason)
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
            s.RestHandlingAfterVerify = RestHandlingModes.Keep;
            s.KeepFailedWorkspace = true;
            ss.Save(s);
            var engine = new SevenZipEngine();
            var pw = new PasswordService { DataRootDirectory = data, UseTextStore = true };
            var vm = new MainViewModel(new FileScanService(), new ArchiveDetectService(), new RenameService(),
                engine, pw, new LogService(ps), ss, ps, new TaskSummaryService(),
                new ClipboardService(), new DialogService(), useTextStore: true, encryptTextStore: false);
            var coordinator = new ExtractionCoordinator(vm, engine, pw, ps, new DialogService());
            var scan = new ScanCoordinator(vm, new FileScanService(), new ArchiveDetectService(), new DialogService());
            var rename = new RenameCoordinator(vm, scan, new RenameService(), new DialogService());
            var one = new OneClickCoordinator(vm, scan, rename, coordinator, new DialogService());
            one.OptionsPromptOverride = _ => OneClickOptionsPrompt.Confirmed(new OneClickRunOptions
            {
                PlacementMode = OutputPlacementMode.CustomRootPerArchive,
                CustomRoot = target,
                SourceHandling = SourceHandlingMode.KeepInPlace,
                RestHandling = RestHandlingModes.Keep
            });
            await vm.AddPathsAsync(new[] { source }, ImportMode.Replace);
            await one.RunAsync().WaitAsync(TimeSpan.FromMinutes(3));

            foreach (ArchiveTask t in vm.Tasks)
                Console.WriteLine($"ROOT {sample} {t.FileName} outcome={t.Outcome} reason={t.Reason} group={t.ContentGroupFinalState}");
            ArchiveTask[] tasks = vm.Tasks.ToArray();
            Assert.Contains(tasks, t => t.Reason.ToString() == expectedReason);
            Assert.DoesNotContain(tasks, t => t.Reason.ToString() == expectedReason && t.Outcome == TaskOutcome.Skipped);
            Assert.True(Directory.Exists(Path.Combine(source, "aaa")), "失败时源包目录必须保留");
            Assert.DoesNotContain(
                Directory.Exists(target)
                    ? Directory.GetFiles(target, "content-*.bin", SearchOption.AllDirectories)
                    : Array.Empty<string>(),
                path => path.Length > 0);
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

