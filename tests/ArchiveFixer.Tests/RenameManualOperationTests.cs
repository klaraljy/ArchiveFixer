using ArchiveFixer.Engines;
using ArchiveFixer.Engines.SevenZip;
using ArchiveFixer.Extraction;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using ArchiveFixer.ViewModels;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 这一组要构造真的 <see cref="MainViewModel"/>（它的构造会写两个进程级静态：7z 路径与递归工作区根），
    /// 所以声明成"不与其他集合并行"——与 <c>InnerLayerContinuationTests</c> 同一套理由。
    /// </summary>
    [Collection("ArchiveFixerGlobalState")]
    public class RenameManualOperationTests : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            "af-rename-manual-" + Guid.NewGuid().ToString("N"));

        public RenameManualOperationTests()
        {
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_root))
                {
                    Directory.Delete(_root, true);
                }
            }
            catch
            {
                // 清理失败不影响结论（临时目录）。
            }
        }

        // ================================================================
        // ① 分卷文件：五种改名操作都不许动它的名字（2026-09-26 审计）
        // ================================================================

        /// <summary>
        /// 缺陷现场：那道"别把分卷链改坏"的闸门以前只长在「智能修正」里（<c>FileNameHelper</c> 的判据），
        /// 而「添加 / 替换 / 删除最后一个后缀」照改不误 ——
        /// 勾着 <c>set.7z.001</c> 点一下「替换后缀」，整组就变成 7z 再也找不到的散卷。
        ///
        /// <para>「删除多个后缀」2026-09-26 整块退役（界面那颗按钮 + 命令一起删），所以这里只剩四种。</para>
        /// </summary>
        [Theory]
        [InlineData("FixByDetectedFormat")]
        [InlineData("AddExtension")]
        [InlineData("ReplaceLastExtension")]
        [InlineData("DeleteLastExtension")]
        public async Task 分卷文件_改名操作都不许动它的名字(string operationType)
        {
            string first = Path.Combine(_root, "set.7z.001");
            string second = Path.Combine(_root, "set.7z.002");

            File.WriteAllBytes(first, new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C, 1, 2, 3 });
            File.WriteAllBytes(second, new byte[] { 4, 5, 6 });

            ArchiveTask task = CreateTask(first, "7Z", ".7z");
            var service = new RenameService();
            var options = new RenameOptions
            {
                OperationType = operationType,
                TargetExtension = ".zip",
                DeleteExtensionCount = 2,
                ConflictAction = "AutoRename"
            };

            List<RenamePreviewItem> preview = service.BuildPreview(new[] { task }, options);

            Assert.Single(preview);
            Assert.Equal(StatusText.RenameWillSkip, preview[0].Status);
            Assert.Contains("分卷", preview[0].ErrorMessage, StringComparison.Ordinal);
            Assert.Equal("set.7z.001", preview[0].NewFileName);

            await service.ExecuteRenameAsync(preview, new[] { task });

            Assert.True(File.Exists(first), "第一卷必须还在原名字上");
            Assert.True(File.Exists(second), "后续卷一个字节都不许动");
        }

        // ================================================================
        // ①b 「名字被改坏的第一卷」：扫描完就该给建议 + 智能修正也能修它
        //     （2026-09-26 审计的入口收敛：那颗按钮以前要先跑一次失败的解压才亮）
        // ================================================================

        [Fact]
        public async Task 只有第一卷的名字坏了_同目录里还有别的分卷组_照样给得出建议()
        {
            // 真机形状（C27 那一档）：**只有第 1 卷**的名字被塞了字，后续卷名字正常。
            // 同一个目录里再躺一组名字完全正常的分卷 + 几个普通包 —— 结论必须与干净目录逐字相同。
            string broken = Path.Combine(_root, "set.7z(删掉.001");

            File.WriteAllBytes(broken, new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C, 1, 2, 3 });
            File.WriteAllBytes(Path.Combine(_root, "set.7z.002"), new byte[] { 4, 5, 6 });
            File.WriteAllBytes(Path.Combine(_root, "set.7z.003"), new byte[] { 7, 8, 9 });
            File.WriteAllBytes(Path.Combine(_root, "另一组.7z.001"), new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C, 9 });
            File.WriteAllBytes(Path.Combine(_root, "另一组.7z.002"), new byte[] { 1 });
            File.WriteAllText(Path.Combine(_root, "普通.zip"), "x");

            var task = new ArchiveTask(broken, 1) { IsSelected = true };

            await new ArchiveDetectService().ApplyDetectResultAsync(task);

            Assert.Equal("set.7z.001", task.VolumeRenameSuggestion);
        }

        [Fact]
        public async Task 每一卷的名字都被塞了字_不给建议_因为标准名推不出来()
        {
            // 2026-09-26 真机上确认过的一档：打包者把**每一卷**都改了名（<c>坏名.7z(删掉.001/.002</c>），
            // 此时"这一组本该叫什么"是**推不出来**的（谁知道那两个字塞在哪一段）——
            // 所以按钮保持灰色、建议为空，这**不是**缺陷；而 7-Zip 自己按同一套名字找同组卷，
            // 不解压失败就不需要改名。
            string broken = Path.Combine(_root, "全坏.7z(删掉.001");

            File.WriteAllBytes(broken, new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C, 1, 2, 3 });
            File.WriteAllBytes(Path.Combine(_root, "全坏.7z(删掉.002"), new byte[] { 4, 5, 6 });
            File.WriteAllBytes(Path.Combine(_root, "全坏.7z(删掉.003"), new byte[] { 7, 8, 9 });
            File.WriteAllBytes(Path.Combine(_root, "正常组.7z.001"), new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C, 9 });
            File.WriteAllBytes(Path.Combine(_root, "正常组.7z.002"), new byte[] { 1 });

            var task = new ArchiveTask(broken, 1) { IsSelected = true };

            await new ArchiveDetectService().ApplyDetectResultAsync(task);

            Assert.Equal(string.Empty, task.VolumeRenameSuggestion);
        }


        [Fact]
        public async Task 只有后续卷在列表里时_说的是分卷缺首卷_不是格式未知()
        {
            // 用户 2026-09-26 拍板的那一档：`set.7z.002` 自己一个任务（首卷没被加进来 / 或不在目录里）。
            // 它的文件头里没有归档魔数，识别必然 Unknown —— 但**状态**必须说清"这是后续卷、缺首卷"，
            // 否则用户以为文件坏了（真机代跑时看到的就是「未识别为支持的压缩格式」）。
            string later = Path.Combine(_root, "单独.7z.002");
            File.WriteAllBytes(later, new byte[] { 4, 5, 6, 7 });

            var task = new ArchiveTask(later, 1) { IsSelected = true };

            await new ArchiveDetectService().ApplyDetectResultAsync(task);

            Assert.Equal(StatusText.VolumeMissing, task.Status);
            Assert.Contains("后续卷", task.ErrorMessage, StringComparison.Ordinal);
            Assert.Contains("单独.7z.001", task.ErrorMessage, StringComparison.Ordinal);
            Assert.Equal(StatusText.ExtensionVolume, task.ExtensionStatus);
        }

        [Fact]
        public async Task 首卷就在同目录里时_那句话会告诉用户这一行不用管()
        {
            string later = Path.Combine(_root, "同目录.7z.002");
            File.WriteAllBytes(later, new byte[] { 4, 5, 6, 7 });
            File.WriteAllBytes(Path.Combine(_root, "同目录.7z.001"), new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C, 1 });

            var task = new ArchiveTask(later, 1) { IsSelected = true };

            await new ArchiveDetectService().ApplyDetectResultAsync(task);

            Assert.Equal(StatusText.VolumeMissing, task.Status);
            Assert.Contains(StatusText.LaterVolumeOnlyFirstVolumeHere, task.ErrorMessage, StringComparison.Ordinal);
        }

        [Fact]
        public async Task 首卷补齐后并组_后续卷那一行标成已并入且不再单独处理()
        {
            // 用户 2026-09-26 拍板的另半句："**或**在首卷补齐后并组"。
            // 现场：一开始只加了 `set.7z.002`（那一行的状态是「分卷缺失」），随后把 `set.7z.001` 也加进来 —
            // 这一行必须改成"已并入首卷那一行"（不再算一单、也不再去尝试单独解它）。
            string first = Path.Combine(_root, "成组.7z.001");
            string second = Path.Combine(_root, "成组.7z.002");

            File.WriteAllBytes(first, new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C, 1, 2, 3 });
            File.WriteAllBytes(second, new byte[] { 4, 5, 6, 7 });

            Harness harness = CreateHarness();
            ArchiveTask laterRow = harness.AddTask("成组.7z.002", new byte[] { 4, 5, 6, 7 }, isSelected: true);

            await new ArchiveDetectService().ApplyDetectResultAsync(laterRow);
            Assert.Equal(StatusText.VolumeMissing, laterRow.Status);

            // 首卷还没进列表 → 一个字都不许改。
            Assert.False(harness.Scan.MergeLaterVolumeIntoFirstVolumeRow(laterRow));
            Assert.Equal(StatusText.VolumeMissing, laterRow.Status);

            // 首卷进列表 → 并组。
            ArchiveTask firstRow = harness.AddTask("成组.7z.001", new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C, 1, 2, 3 }, isSelected: true);

            Assert.True(harness.Scan.MergeLaterVolumeIntoFirstVolumeRow(laterRow));
            Assert.Equal(StatusText.Skipped, laterRow.Status);
            Assert.Contains("成组.7z.001", laterRow.ErrorMessage, StringComparison.Ordinal);
            Assert.Equal(StatusText.Recognized, firstRow.Status); // 首卷那一行不受影响
        }
        [Fact]
        public async Task 普通的无格式文件_照旧报格式未知_不能被这条新规则误伤()
        {
            string junk = Path.Combine(_root, "随便一个.bin");
            File.WriteAllBytes(junk, new byte[] { 1, 2, 3, 4, 5 });

            var task = new ArchiveTask(junk, 1) { IsSelected = true };

            await new ArchiveDetectService().ApplyDetectResultAsync(task);

            Assert.Equal(StatusText.UnknownFormat, task.Status);
        }
        [Fact]
        public async Task 扫描期就给出改名建议_不用先跑一次失败的解压()
        {
            string first = Path.Combine(_root, "卷名被改坏.7z(删掉.001");
            string second = Path.Combine(_root, "卷名被改坏.7z.002");

            File.WriteAllBytes(first, new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C, 1, 2, 3 });
            File.WriteAllBytes(second, new byte[] { 4, 5, 6 });

            var task = new ArchiveTask(first, 1) { IsSelected = true };
            var detect = new ArchiveDetectService();

            await detect.ApplyDetectResultAsync(task);

            Assert.Equal("卷名被改坏.7z.001", task.VolumeRenameSuggestion);
        }

        [Fact]
        public async Task 名字本来就标准时_扫描期不给建议()
        {
            string first = Path.Combine(_root, "标准名.7z.001");
            string second = Path.Combine(_root, "标准名.7z.002");

            File.WriteAllBytes(first, new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C, 1, 2, 3 });
            File.WriteAllBytes(second, new byte[] { 4, 5, 6 });

            var task = new ArchiveTask(first, 1) { IsSelected = true };

            await new ArchiveDetectService().ApplyDetectResultAsync(task);

            // 标准名的第一卷：没有可修的 → 建议必须是空（否则那颗按钮会一直亮着、点了白改一次名字）。
            Assert.Equal(string.Empty, task.VolumeRenameSuggestion);
        }

        [Fact]
        public void 智能修正也能把改坏的卷名修回标准名()
        {
            string first = Path.Combine(_root, "被改坏.7z(删掉.001");
            string second = Path.Combine(_root, "被改坏.7z.002");

            File.WriteAllBytes(first, new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C, 1, 2, 3 });
            File.WriteAllBytes(second, new byte[] { 4, 5, 6 });

            ArchiveTask task = CreateTask(first, "7Z", ".001");
            var service = new RenameService();
            var options = new RenameOptions
            {
                OperationType = "FixByDetectedFormat",
                TargetExtension = ".7z",
                ConflictAction = "AutoRename"
            };

            List<RenamePreviewItem> preview = service.BuildPreview(new[] { task }, options);

            Assert.Single(preview);
            Assert.Equal("被改坏.7z.001", preview[0].NewFileName);
            Assert.NotEqual(StatusText.RenameWillSkip, preview[0].Status);
        }

        // ================================================================
        // ①c 「替换文件名里的文字」（2026-09-26 用户批准加的高频功能）
        // ================================================================

        [Fact]
        public async Task 名字里被塞了字_用文字替换把它去掉()
        {
            // 真机形状（第 33 条那批）：打包者把后缀塞成了 `222.ra删除r`。
            // 加/替换/删后缀都能歪打正着，但"名字中间被塞字"（第二种形状）只有这一条路能修。
            string packed = Path.Combine(_root, "222.ra删除r");
            File.WriteAllText(packed, "x");

            ArchiveTask task = CreateTask(packed, "RAR5", ".rar");
            var service = new RenameService();
            var options = new RenameOptions
            {
                OperationType = "ReplaceFileNameText",
                FindText = "删除",
                ReplaceText = string.Empty,
                ConflictAction = "AutoRename"
            };

            List<RenamePreviewItem> preview = service.BuildPreview(new[] { task }, options);

            Assert.Single(preview);
            Assert.Equal("222.rar", preview[0].NewFileName);

            await service.ExecuteRenameAsync(preview, new[] { task });

            Assert.True(File.Exists(Path.Combine(_root, "222.rar")));
            Assert.False(File.Exists(packed));
        }

        [Fact]
        public void 文字替换_名字里没有那一段时原样跳过()
        {
            string file = Path.Combine(_root, "干净的包.zip");
            File.WriteAllText(file, "x");

            ArchiveTask task = CreateTask(file, "ZIP", ".zip");
            var options = new RenameOptions
            {
                OperationType = "ReplaceFileNameText",
                FindText = "删掉",
                ReplaceText = string.Empty,
                ConflictAction = "AutoRename"
            };

            List<RenamePreviewItem> preview = new RenameService().BuildPreview(new[] { task }, options);

            Assert.Single(preview);
            Assert.Equal(StatusText.RenameWillSkip, preview[0].Status);
            Assert.Equal("干净的包.zip", preview[0].NewFileName);
        }

        [Fact]
        public async Task 文字替换_对分卷也放行_因为每一卷都被塞了同样的字()
        {
            // 打包者往**每一卷**的名字里都塞了字（set.7z(删掉.001/.002/…）：把它们一起去掉正是修法，
            // 所以这一档豁免"分卷不许改名"那道闸门（⭐ 只对 ReplaceFileNameText 豁免）。
            string first = Path.Combine(_root, "set.7z(删掉.001");
            string second = Path.Combine(_root, "set.7z(删掉.002");

            File.WriteAllBytes(first, new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C, 1, 2, 3 });
            File.WriteAllBytes(second, new byte[] { 4, 5, 6 });

            var tasks = new[] { CreateTask(first, "7Z", ".001"), CreateTask(second, "7Z", ".002") };
            var service = new RenameService();
            var options = new RenameOptions
            {
                OperationType = "ReplaceFileNameText",
                FindText = "(删掉",
                ReplaceText = string.Empty,
                ConflictAction = "AutoRename"
            };

            List<RenamePreviewItem> preview = service.BuildPreview(tasks, options);

            Assert.Equal(2, preview.Count);
            Assert.All(preview, item => Assert.NotEqual(StatusText.RenameWillSkip, item.Status));

            await service.ExecuteRenameAsync(preview, tasks);

            Assert.True(File.Exists(Path.Combine(_root, "set.7z.001")));
            Assert.True(File.Exists(Path.Combine(_root, "set.7z.002")));
        }
        // ================================================================
        // ② 本批次内两行撞名：预览期就错开（2026-09-26 审计）
        // ================================================================

        /// <summary>
        /// 缺陷现场：预览只查**磁盘上**的冲突，不查**本批内**两行撞名 ——
        /// <c>A.jpg</c> 与 <c>A.png</c> 一起改成 <c>.rar</c> 时两行落点都是 <c>A.rar</c>，
        /// 预览说"可以改 2 个"，执行到落位那一步 File.Move 撞名 → 整批回滚 + 一句看不懂的报错。
        /// </summary>
        [Fact]
        public async Task 两行改成同一个名字_预览期就自动错开_执行后两个文件都在()
        {
            string jpg = Path.Combine(_root, "A.jpg");
            string png = Path.Combine(_root, "A.png");

            File.WriteAllText(jpg, "jpg");
            File.WriteAllText(png, "png");

            var tasks = new[] { CreateTask(jpg, "Unknown", string.Empty), CreateTask(png, "Unknown", string.Empty) };
            var service = new RenameService();
            var options = new RenameOptions
            {
                OperationType = "ReplaceLastExtension",
                TargetExtension = ".rar",
                ConflictAction = "AutoRename"
            };

            List<RenamePreviewItem> preview = service.BuildPreview(tasks, options);

            Assert.Equal(2, preview.Count);

            List<string> names = preview
                .Select(item => item.NewFileName)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            Assert.Equal(new[] { "A(1).rar", "A.rar" }, names);
            Assert.Contains(preview, item => item.Status == StatusText.WillAutoRename);
            Assert.All(preview, item => Assert.NotEqual(StatusText.RenameCannot, item.Status));

            await service.ExecuteRenameAsync(preview, tasks);

            Assert.True(File.Exists(Path.Combine(_root, "A.rar")), "第一行按原计划落位");
            Assert.True(File.Exists(Path.Combine(_root, "A(1).rar")), "第二行必须自动错开，而不是整批失败");
            Assert.False(File.Exists(jpg));
            Assert.False(File.Exists(png));
        }

        // ================================================================
        // ③ 编辑新文件名 → 落点与统计当场跟着变（2026-09-26 审计）
        // ================================================================

        [Fact]
        public void 改了新文件名_落点当场跟着变_非法名字当场标出来()
        {
            string source = Path.Combine(_root, "old.mp4");

            File.WriteAllText(source, "x");

            var item = new RenamePreviewItem(source, "7Z", "按真实格式修正", Path.Combine(_root, "old.7z"));
            var vm = new RenamePreviewViewModel(new[] { item });

            Assert.True(vm.HasExecutableItems);

            // 编辑那一格（以前要等"点确认改名"才同步落点 —— 中间这段时间界面显示的是旧结论）。
            item.NewFileName = "new.zip";

            Assert.Equal(Path.Combine(_root, "new.zip"), item.NewPath);
            Assert.True(vm.HasExecutableItems);

            item.NewFileName = "bad/name.zip";

            Assert.Equal(StatusText.RenameCannot, item.Status);
            Assert.Contains("非法字符", item.ErrorMessage, StringComparison.Ordinal);
            Assert.False(item.IsSelected);
            Assert.False(vm.HasExecutableItems);
        }

        [Fact]
        public void 预览里显示的名字_长名字也要看得见后缀()
        {
            string longName = new string('x', 90) + ".7z.001";
            var item = new RenamePreviewItem(Path.Combine(_root, longName), "7Z", "替换最后后缀", Path.Combine(_root, "短.7z"));

            Assert.EndsWith(".7z.001", item.OriginalFileNameDisplay, StringComparison.Ordinal);
            Assert.Contains(FileNameMiddleEllipsis.EllipsisChar, item.OriginalFileNameDisplay);
            Assert.Equal("短.7z", item.NewFileNameDisplay);
        }

        // ================================================================
        // ④ 「冲突」那一列：提示必须指对列名（2026-09-26 审计）
        // ================================================================

        [Fact]
        public void 冲突提示指向真正带下拉框的那一列()
        {
            string source = Path.Combine(_root, "dup.rar");

            File.WriteAllText(source, "source");
            File.WriteAllText(Path.Combine(_root, "dup.zip"), "existing");

            ArchiveTask task = CreateTask(source, "ZIP", ".zip");
            var service = new RenameService();
            var options = new RenameOptions
            {
                OperationType = "FixByDetectedFormat",
                TargetExtension = ".zip",
                ConflictAction = "Ask"
            };

            List<RenamePreviewItem> preview = service.BuildPreview(new[] { task }, options);

            Assert.Single(preview);
            Assert.True(preview[0].NeedsConflictChoice);
            Assert.Contains("「冲突」", preview[0].ErrorMessage, StringComparison.Ordinal);
            Assert.DoesNotContain("「冲突处理」", preview[0].ErrorMessage, StringComparison.Ordinal);
        }

        // ================================================================
        // ⑤ 改名跑完只重扫被改过的任务（2026-09-26 审计，真缺陷）
        // ================================================================

        /// <summary>
        /// 缺陷现场：五颗改名按钮跑完都调整表 <c>ScanTasksAsync()</c>，
        /// 而 <c>ArchiveDetectService.ApplyDetectResultAsync</c> 一进来就把
        /// <c>Status = 扫描中</c>、<c>ErrorMessage = ""</c>，收尾统一写成「已识别」——
        /// 于是改一个文件的后缀，整张表里已经解压成功的行变回「已识别」、
        /// 失败行的**失败原因被清空**（汇总与失败清单读的正是 <c>ErrorMessage</c>）。
        /// </summary>
        [Fact]
        public async Task 自动改名跑完_别的任务的成功与失败结论都还在()
        {
            Harness harness = CreateHarness();
            ArchiveTask renameTarget = harness.AddTask("伪装.mp4", CreateSevenZipBytes(), isSelected: true);
            ArchiveTask finished = harness.AddTask("已经解压过的.rar", CreateSevenZipBytes(), isSelected: false);
            ArchiveTask failed = harness.AddTask("解压失败的.rar", CreateSevenZipBytes(), isSelected: false);

            finished.Status = StatusText.ExtractSuccess;
            finished.ErrorMessage = string.Empty;

            failed.Status = StatusText.ExtractFailed;
            failed.ErrorMessage = "密码错误：候选密码都不对";

            bool renamed = await harness.Rename.AutoFixExtensionsAsync();

            Assert.True(renamed, "后缀被改坏的那个包应当被自动改名");
            Assert.True(File.Exists(Path.Combine(harness.SourceDirectory, "伪装.7z")));

            Assert.Equal(StatusText.ExtractSuccess, finished.Status);
            Assert.Equal(StatusText.ExtractFailed, failed.Status);
            Assert.Equal("密码错误：候选密码都不对", failed.ErrorMessage);
        }

        /// <summary>
        /// 源码守卫：改名这条路上**不许**再出现整表重扫（那一条会冲掉别人的结果）。
        /// 与上面那条行为测试配对 —— 一条钉"行为没错"，一条钉"别再写回去"。
        ///
        /// <para>注释行先剔掉再判：解释这个坑的注释里当然会提到那个方法名。</para>
        /// </summary>
        [Fact]
        public void 改名协调器_不许再整表重扫()
        {
            string path = Path.Combine(
                XamlBindingScan.RepositoryRoot, "src", "ArchiveFixer", "ViewModels", "RenameCoordinator.cs");

            string code = string.Join(
                Environment.NewLine,
                File.ReadAllLines(path, Encoding.UTF8)
                    .Where(line =>
                    {
                        string trimmed = line.TrimStart();
                        return !trimmed.StartsWith("//", StringComparison.Ordinal)
                               && !trimmed.StartsWith("///", StringComparison.Ordinal)
                               && !trimmed.StartsWith("*", StringComparison.Ordinal);
                    }));

            Assert.DoesNotContain("ScanTasksAsync()", code, StringComparison.Ordinal);
            Assert.Contains("RescanTaskAsync", code, StringComparison.Ordinal);
        }

        // ================================================================
        // ⑥ 右键单文件改名：用完把勾选还回去（2026-09-26 审计）
        // ================================================================

        [Fact]
        public async Task 右键单文件改名_用完把勾选还回去()
        {
            Harness harness = CreateHarness();
            ArchiveTask first = harness.AddTask("a.mp4", CreateSevenZipBytes(), isSelected: true);
            ArchiveTask second = harness.AddTask("b.mp4", CreateSevenZipBytes(), isSelected: true);
            ArchiveTask third = harness.AddTask("c.mp4", CreateSevenZipBytes(), isSelected: false);

            bool scopeWasSingle = false;

            harness.Vm.SmartRenameRunnerOverride = () =>
            {
                scopeWasSingle = second.IsSelected && !first.IsSelected && !third.IsSelected;
                return Task.CompletedTask;
            };

            await harness.Vm.RunSmartRenameForSingleTaskAsync(second);

            Assert.True(scopeWasSingle, "执行的那一刻只该有这一行在作用域里");
            Assert.True(first.IsSelected, "用完必须把勾选还回去");
            Assert.True(second.IsSelected);
            Assert.False(third.IsSelected);
        }

        // ================================================================
        // ⑦ 长文件名的中间省略（纯函数）
        // ================================================================

        [Fact]
        public void 长名字_中间省略_单后缀一定看得见()
        {
            string name = new string('a', 80) + ".7z";
            string elided = FileNameMiddleEllipsis.Elide(name, 30);

            Assert.True(elided.Length < name.Length);
            Assert.StartsWith("aaaa", elided, StringComparison.Ordinal);
            Assert.Contains(FileNameMiddleEllipsis.EllipsisChar, elided);
            Assert.EndsWith(".7z", elided, StringComparison.Ordinal);
        }

        [Fact]
        public void 长名字_双后缀也留着_卷号与双后缀都看得见()
        {
            string volume = new string('b', 80) + ".7z.001";
            string doubleExtension = new string('c', 80) + ".rar.jpg";

            Assert.EndsWith(".7z.001", FileNameMiddleEllipsis.Elide(volume, 30), StringComparison.Ordinal);
            Assert.EndsWith(".rar.jpg", FileNameMiddleEllipsis.Elide(doubleExtension, 30), StringComparison.Ordinal);
        }

        [Fact]
        public void 中文名字按双宽算_省略后不会溢出那一列()
        {
            string name = new string('阿', 40) + ".zip";
            string elided = FileNameMiddleEllipsis.Elide(name, 30);

            Assert.True(
                FileNameMiddleEllipsis.Measure(elided) <= 30,
                $"省略后宽度 {FileNameMiddleEllipsis.Measure(elided)} 超过 30");
            Assert.EndsWith(".zip", elided, StringComparison.Ordinal);
        }

        [Fact]
        public void 短名字_空名字_没有后缀的怪名字都不抛()
        {
            Assert.Equal("a.zip", FileNameMiddleEllipsis.Elide("a.zip", 30));
            Assert.Equal(string.Empty, FileNameMiddleEllipsis.Elide(null));
            Assert.Equal(string.Empty, FileNameMiddleEllipsis.Elide("   "));

            // 整名就是"后缀"的形状（.gitignore / .env.local）：不当后缀保留，按"没有后缀"处理 ——
            // 至少能看出这是哪一个文件，而不是给出一个只剩 ".gggg" 的假名字。
            Assert.StartsWith(".g", FileNameMiddleEllipsis.Elide("." + new string('g', 60), 30), StringComparison.Ordinal);

            string noExtension = new string('d', 200);
            string elided = FileNameMiddleEllipsis.Elide(noExtension, 30);
            Assert.Equal(30, FileNameMiddleEllipsis.Measure(elided));
            Assert.EndsWith(new string('d', 12), elided, StringComparison.Ordinal);
        }

        // ================================================================ 装配

        private static ArchiveTask CreateTask(string path, string detectedFormat, string suggestedExtension)
        {
            return new ArchiveTask(path, 1)
            {
                IsArchive = true,
                DetectedFormat = detectedFormat,
                SuggestedExtension = suggestedExtension,
                ExtensionStatus = StatusText.ExtensionMismatch,
                Status = StatusText.Recognized,
                IsSelected = true
            };
        }

        /// <summary>真 7z 的文件头（识别只看魔数，测试里不需要真造一个归档）。</summary>
        private static byte[] CreateSevenZipBytes()
        {
            return new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C, 0x00, 0x04, 1, 2, 3, 4, 5, 6, 7, 8 };
        }

        private sealed class Harness
        {
            public Harness(MainViewModel vm, RenameCoordinator rename, ScanCoordinator scan, string sourceDirectory)
            {
                Vm = vm;
                Rename = rename;
                Scan = scan;
                SourceDirectory = sourceDirectory;
            }

            public ScanCoordinator Scan { get; }

            public MainViewModel Vm { get; }

            public RenameCoordinator Rename { get; }

            public string SourceDirectory { get; }

            public ArchiveTask AddTask(string fileName, byte[] content, bool isSelected)
            {
                string path = Path.Combine(SourceDirectory, fileName);

                File.WriteAllBytes(path, content);

                var task = new ArchiveTask(path, Vm.Tasks.Count + 1)
                {
                    IsArchive = true,
                    DetectedFormat = "7Z",
                    SuggestedExtension = ".7z",
                    ExtensionStatus = StatusText.ExtensionMismatch,
                    Status = StatusText.Recognized,
                    IsSelected = isSelected
                };

                Vm.Tasks.Add(task);

                return task;
            }
        }

        private Harness CreateHarness()
        {
            string runRoot = Path.Combine(_root, "run-" + Guid.NewGuid().ToString("N")[..6]);
            string dataRoot = Path.Combine(runRoot, "data");
            string outputRoot = Path.Combine(runRoot, "out");
            string sourceDirectory = Path.Combine(runRoot, "src");

            Directory.CreateDirectory(dataRoot);
            Directory.CreateDirectory(outputRoot);
            Directory.CreateDirectory(sourceDirectory);

            var pathService = new PathService { DataRootDirectory = dataRoot };
            var settingsService = new SettingsService(pathService);

            AppSettings settings = AppSettings.CreateDefault();
            settings.CustomOutputDirectory = outputRoot;
            settings.ExtractToOriginalDirectory = false;
            settings.AutoScanAfterDrop = false;
            settings.KeepArchiveNameFolder = true;
            settings.RecursionMode = "SingleLayer";
            settings.SourceHandling = nameof(SourceHandlingMode.KeepInPlace);
            settings.DefaultExtension = ".7z";
            settings.ConflictAction = ConflictActions.AutoRename;
            settings.UnknownFormatAction = "MarkUnknown";

            settingsService.Save(settings);

            var passwordService = new PasswordService { DataRootDirectory = dataRoot };
            var logService = new LogService(pathService);

            // MainViewModel 的构造会顺手写两个进程级静态：先存后还原。
            string? previousWorkspaceRoot = RecursiveExtractor.ConfiguredWorkspaceRoot;
            string previousSevenZipPath = ToolLocator.Default.CustomSevenZipExePath;

            var vm = new MainViewModel(
                new FileScanService(),
                new ArchiveDetectService(),
                new RenameService(),
                new SevenZipEngine(),
                passwordService,
                logService,
                settingsService,
                pathService,
                new TaskSummaryService(),
                new ClipboardService(),
                new DialogService());

            RecursiveExtractor.ConfiguredWorkspaceRoot = previousWorkspaceRoot;
            ToolLocator.Default.CustomSevenZipExePath = previousSevenZipPath;

            var scan = new ScanCoordinator(vm, new FileScanService(), new ArchiveDetectService(), new DialogService());
            var rename = new RenameCoordinator(vm, scan, new RenameService(), new DialogService());

            return new Harness(vm, rename, scan, sourceDirectory);
        }
    }
}
