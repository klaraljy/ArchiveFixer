using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ArchiveFixer.Engines;
using ArchiveFixer.Extraction;
using ArchiveFixer.Engines.SevenZip;
using ArchiveFixer.Models;
using ArchiveFixer.ViewModels;
using ArchiveFixer.Password;
using ArchiveFixer.Services;
using Xunit;

namespace ArchiveFixer.Tests
{
    [Collection("ArchiveFixerGlobalState")]
    public sealed class Normal915SamplesTests
    {
        [Fact]
        public async Task 四十个普通样本_全部成功且不落最终组失败()
        {
            const string backupRoot = @"H:\BaiduNetdiskDownload\困难大测试\大测试备份位置\915000000-备份";
            for (int index = 1; index <= 40; index++)
            {
                string sample = "915" + index.ToString("D6");
                string backup = Path.Combine(backupRoot, sample, "原包");
                if (!Directory.Exists(backup))
                {
                    throw new InvalidOperationException($"缺少样本 {sample}: {backup}");
                }

                await RunOneAsync(sample, backup);
            }
        }

        private static async Task RunOneAsync(string sample, string backup)
        {
            string root = Path.Combine(@"E:\DeepSeekProjects\_tmp\ArchiveFixer\normal-probe", Guid.NewGuid().ToString("N"));
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

            ArchiveTask[] tasks = vm.Tasks.ToArray();
            Assert.DoesNotContain(tasks, t => t.Reason is TaskReason.FinalGroupIncomplete or TaskReason.FinalGroupCorrupted);
            Assert.Contains(tasks, t => t.Outcome == TaskOutcome.Succeeded);
            Assert.True(Directory.Exists(Path.Combine(source, "aaa")), $"{sample} 成功时源包目录应保留");
            Assert.DoesNotContain(tasks, t => t.ContentGroupFinalState == FinalContentState.Missing && t.Reason.ToString().StartsWith("FinalGroup"));
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


