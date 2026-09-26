using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ArchiveFixer.Models;
using ArchiveFixer.Packing;
using ArchiveFixer.Storage;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// **打包成功之后的收尾**（用户 2026-09-26 第 46 条）：原包操作 + 其余物操作。
    ///
    /// <para>他确认的口径：原包默认**不动**；其余物（= 装 7z 分卷的文件夹，单文件时还有那个临时建的同名文件夹）
    /// **默认彻底删除**（"这个对用户来说一点用没有"）；两档与解压那套完全独立。</para>
    ///
    /// <para>这一组钉的是**四条红线**：①没成功一个字节都不动；②不做外层容器时其余物那一档不执行
    /// （否则会把结果删掉）；③只删落点目录里我们自己造的那几个目录；④删失败不改结论。</para>
    /// </summary>
    public class PackingCleanupTests : IDisposable
    {
        private readonly string _root;

        public PackingCleanupTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerPackCleanup", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_root))
                {
                    Directory.Delete(_root, recursive: true);
                }
            }
            catch
            {
            }
        }

        // ================================================================ 红线①：没成功什么都不动

        [Fact]
        public void 没成功_一个字节都不动()
        {
            Harness harness = Build(withWrapper: false);

            PackingCleanupOutcome outcome = PackingCleanup.Run(
                harness.Plan,
                succeeded: false,
                harness.OuterPath,
                harness.Delete);

            Assert.False(outcome.Ran);
            Assert.True(File.Exists(harness.Volume001), "没成功时其余物（分卷文件）必须原样留着");
            Assert.True(Directory.Exists(harness.SourceFolder), "没成功时源必须原样留着");
            Assert.Contains(outcome.LogLines, line => line.Contains("一个字节都不动", StringComparison.Ordinal));
        }

        // ================================================================ 默认档：原包不动 + 其余物彻底删除

        [Fact]
        public void 默认档_原包不动_其余物被彻底删除_只剩外层容器()
        {
            Harness harness = Build(withWrapper: false);

            PackingCleanupOutcome outcome = PackingCleanup.Run(
                harness.Plan,
                succeeded: true,
                harness.OuterPath,
                harness.Delete);

            Assert.True(outcome.Ran);
            Assert.False(outcome.SourceMoved);
            Assert.Equal("已彻底删除", outcome.RestSummary);
            Assert.True(outcome.FreedBytes > 0);

            Assert.False(File.Exists(harness.Volume001), "其余物（分卷文件）应当被删掉");
            Assert.True(Directory.Exists(harness.SourceFolder), "原包默认不动");
            Assert.True(File.Exists(harness.OuterPath), "外层容器（= 结果）必须在");
        }

        [Fact]
        public void 其余物不动那一档_分卷文件留着()
        {
            Harness harness = Build(withWrapper: false, options: new PackingRunOptions
            {
                RestHandling = PackingRestHandling.Keep
            });

            PackingCleanupOutcome outcome = PackingCleanup.Run(
                harness.Plan,
                succeeded: true,
                harness.OuterPath,
                harness.Delete);

            Assert.Equal("保留", outcome.RestSummary);
            Assert.Equal(0, outcome.FreedBytes);
            Assert.True(File.Exists(harness.Volume001));
            Assert.Contains(outcome.LogLines, line => line.Contains("其余物保留", StringComparison.Ordinal));
        }

        [Fact]
        public void 其余物回收站那一档_用的是回收站模式()
        {
            Harness harness = Build(withWrapper: false, options: new PackingRunOptions
            {
                RestHandling = PackingRestHandling.RecycleBin
            });

            PackingCleanupOutcome outcome = PackingCleanup.Run(
                harness.Plan,
                succeeded: true,
                harness.OuterPath,
                harness.Delete);

            Assert.Equal("已移入回收站", outcome.RestSummary);
            Assert.Equal(DeleteMode.RecycleBin, Assert.Single(harness.DeleteModes));
        }

        // ================================================================ 原包移入其余物

        [Fact]
        public void 原包移入其余物_源被搬进装分卷的文件夹()
        {
            Harness harness = Build(withWrapper: false, options: new PackingRunOptions
            {
                SourceHandling = PackingSourceHandling.MoveToRest,
                RestHandling = PackingRestHandling.Keep
            });

            PackingCleanupOutcome outcome = PackingCleanup.Run(
                harness.Plan,
                succeeded: true,
                harness.OuterPath,
                harness.Delete);

            Assert.True(outcome.SourceMoved);
            Assert.False(Directory.Exists(harness.SourceFolder), "源应当已经搬走");

            /*
             * ⚠ 追加改口径：分卷不再装在中间文件夹里，所以"移入其余物"这一步由程序**现建**一个
             * 以源名命名的文件夹（源文件夹本来就叫 `素材`，于是让位成 `素材(1)`），把源搬进去。
             */
            string holder = Path.Combine(_root, "素材(1)");
            string moved = Path.Combine(holder, "素材");

            Assert.True(Directory.Exists(moved), "源应当在其余物那个文件夹里：" + moved);
            Assert.True(File.Exists(Path.Combine(moved, "a.bin")));
        }

        /// <summary>
        /// ⚠ 危险组合（原包移入其余物 + 其余物彻底删除）：**连原包一起没**。
        /// 弹窗里那句红字警告就是为这一档准备的 —— 这里钉住"警告确实存在"与"用户选了就真照做"。
        /// </summary>
        [Fact]
        public void 原包移入其余物加其余物删除_两个一起没_而且事先有红字警告()
        {
            var options = new PackingRunOptions
            {
                SourceHandling = PackingSourceHandling.MoveToRest,
                RestHandling = PackingRestHandling.Delete
            };

            Assert.True(options.WouldDeleteSource);
            Assert.NotNull(options.SourceLossWarning);

            Harness harness = Build(withWrapper: false, options: options);

            PackingCleanupOutcome outcome = PackingCleanup.Run(
                harness.Plan,
                succeeded: true,
                harness.OuterPath,
                harness.Delete);

            Assert.True(outcome.SourceMoved);
            Assert.False(File.Exists(harness.Volume001), "其余物（分卷文件）应当被删");
            Assert.False(Directory.Exists(harness.SourceFolder), "原包也被一起删了（用户明知并确认过）");
            Assert.False(Directory.Exists(Path.Combine(_root, "素材(1)")), "装原包的那个文件夹也一起没了");
            Assert.True(File.Exists(harness.OuterPath));
        }

        // ================================================================ 红线②：不做外层容器时不动其余物

        [Fact]
        public void 不做外层容器时_其余物那一档不执行()
        {
            Harness harness = Build(withWrapper: false, outerContainer: PackOuterContainer.None);

            PackingCleanupOutcome outcome = PackingCleanup.Run(
                harness.Plan,
                succeeded: true,
                outerArtifactPath: string.Empty,
                harness.Delete);

            Assert.True(File.Exists(harness.Volume001), "分卷就是结果，绝不能删");
            Assert.Empty(harness.DeleteModes);
            Assert.Contains(outcome.LogLines, line => line.Contains("分卷就是结果", StringComparison.Ordinal));
        }

        // ================================================================ 单文件：那个临时文件夹也算其余物

        [Fact]
        public void 单文件_临时建的同名文件夹与分卷一起按档处理()
        {
            string file = Path.Combine(_root, "111.mp4");
            File.WriteAllBytes(file, new byte[64]);

            Harness harness = Build(withWrapper: true, sourcePath: file);

            // 服务层才会真的建那个文件夹，这里替它建出来（收尾时它应当被一起收拾掉）。
            Directory.CreateDirectory(harness.Plan.WrapperFolderToCreate);

            PackingCleanupOutcome outcome = PackingCleanup.Run(
                harness.Plan,
                succeeded: true,
                harness.OuterPath,
                harness.Delete);

            Assert.Equal("已彻底删除", outcome.RestSummary);
            Assert.False(File.Exists(harness.Volume001), "分卷文件应当被删");
            Assert.False(Directory.Exists(harness.Plan.WrapperFolderToCreate), "临时建的同名文件夹也应当被删");
            Assert.True(File.Exists(file), "原文件默认不动（它不在其余物里）");
        }

        // ================================================================ 红线③：越界只跳过并点名

        [Fact]
        public void 其余物不在落点目录里_跳过并如实点名()
        {
            Harness harness = Build(withWrapper: false, outputOutsideTarget: true);

            PackingCleanupOutcome outcome = PackingCleanup.Run(
                harness.Plan,
                succeeded: true,
                harness.OuterPath,
                harness.Delete);

            Assert.True(File.Exists(harness.Volume001), "越界的其余物一个字节都不许动");
            Assert.Empty(harness.DeleteModes);
            Assert.Contains("跳过", outcome.RestSummary, StringComparison.Ordinal);
        }

        // ================================================================ 红线④：删失败不改结论

        [Fact]
        public void 删失败_只记结果_不影响已经成功的外层容器()
        {
            Harness harness = Build(withWrapper: false, deleteFails: true);

            PackingCleanupOutcome outcome = PackingCleanup.Run(
                harness.Plan,
                succeeded: true,
                harness.OuterPath,
                harness.Delete);

            Assert.True(File.Exists(harness.Volume001), "没删成当然还在");
            Assert.Contains("没做成", outcome.RestSummary, StringComparison.Ordinal);
            Assert.True(File.Exists(harness.OuterPath), "结果文件不受收尾影响");
        }

        // ================================================================ 工具

        private Harness Build(
            bool withWrapper,
            string? sourcePath = null,
            PackingRunOptions? options = null,
            PackOuterContainer outerContainer = PackOuterContainer.Rar,
            bool outputOutsideTarget = false,
            bool deleteFails = false)
        {
            string source = sourcePath ?? Path.Combine(_root, "素材");

            if (sourcePath == null)
            {
                Directory.CreateDirectory(source);
                File.WriteAllBytes(Path.Combine(source, "a.bin"), new byte[4096]);
            }

            var request = new PackingRequest
            {
                SourceFolder = source,
                Password = "测试密码",
                VolumeSizeBytes = 1024 * 1024,
                OuterContainer = outerContainer,
                RunOptions = options ?? new PackingRunOptions()
            };

            PackingSourceResolution resolution = PackingSourceResolver.Resolve(source);

            string target = outputOutsideTarget ? Path.Combine(_root, "别的落点") : _root;
            Directory.CreateDirectory(target);

            /*
             * ⚠ 2026-09-26 追加改口径：分卷**直接落在落点目录里**（不再套一个中间文件夹），
             * 所以"其余物"是那些**分卷文件**。这里照新模型造现场：
             * · OutputFolder = 落点目录（除非专门测"越界"那一档）；
             * · VolumeBasePath = `<落点>\<源名>.7z`，第一卷 = 它 + `.001`。
             */
            string output = outputOutsideTarget ? Path.Combine(_root, "越界的其余物") : target;

            Directory.CreateDirectory(output);

            string volumeBase = PackingPaths.ResolveUniqueVolumeBasePath(output, resolution.SourceName);
            string volume001 = volumeBase + ".001";

            File.WriteAllBytes(volume001, new byte[4096]);

            string outerPath = Path.Combine(target, resolution.SourceName + ".rar");
            File.WriteAllBytes(outerPath, new byte[8192]);

            var plan = new PackingPlan
            {
                SourceFolder = resolution.FolderToPack,
                SourceFolderName = resolution.SourceName,
                SourceResolution = resolution,
                TargetDirectory = target,
                RunOptions = request.RunOptions,
                OutputFolder = output,
                VolumeBasePath = volumeBase,
                RarPath = outerPath,
                SevenZipOuterPath = Path.Combine(target, resolution.SourceName + ".7z"),
                OuterContainer = outerContainer,
                VolumeSizeBytes = request.VolumeSizeBytes,
                ContentBytes = 4096,
                FileCount = 1
            };

            var harness = new Harness(plan, source, outerPath, deleteFails) { Volume001 = volume001 };

            return harness;
        }

        private sealed class Harness
        {
            public Harness(PackingPlan plan, string sourceFolder, string outerPath, bool deleteFails)
            {
                Plan = plan;
                SourceFolder = sourceFolder;
                OuterPath = outerPath;
                DeleteFails = deleteFails;
            }

            public PackingPlan Plan { get; }

            public string SourceFolder { get; }

            public string OuterPath { get; }

            /// <summary>造出来的第一卷（其余物 = 这些分卷文件）。</summary>
            public string Volume001 { get; init; } = string.Empty;

            public bool DeleteFails { get; }

            public List<DeleteMode> DeleteModes { get; } = new();

            /// <summary>真的删（这样"其余物没了"是真的没了，不是记账）。</summary>
            public DeleteResult Delete(DeleteRequest request, DeleteOptions options)
            {
                DeleteModes.Add(options.Mode);

                if (DeleteFails)
                {
                    return new DeleteResult
                    {
                        Mode = options.Mode,
                        AllowedRoot = options.AllowedRoot,
                        Outcomes = new[]
                        {
                            new DeleteOutcome
                            {
                                Path = request.Path,
                                Success = false,
                                Mode = options.Mode,
                                Message = "设备被占用（测试用的假失败）"
                            }
                        }
                    };
                }

                try
                {
                    if (Directory.Exists(request.Path))
                    {
                        Directory.Delete(request.Path, recursive: true);
                    }
                    else if (File.Exists(request.Path))
                    {
                        File.Delete(request.Path);
                    }
                }
                catch
                {
                    // 与真实删除同一口径：删不掉就当失败。
                }

                return new DeleteResult
                {
                    Mode = options.Mode,
                    AllowedRoot = options.AllowedRoot,
                    Outcomes = new[]
                    {
                        new DeleteOutcome
                        {
                            Path = request.Path,
                            Success = true,
                            Mode = options.Mode,
                            Message = "已删除"
                        }
                    }
                };
            }
        }
    }
}
