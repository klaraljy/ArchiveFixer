using System;
using System.Collections.Generic;
using System.IO;
using ArchiveFixer.Models;
using ArchiveFixer.Packing;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// **打包（用户 2026-09-26 第 46 条按他口述重做）** 的纯逻辑那一层：
    /// 分卷规则（按卷数）、单文件 → 同名文件夹、命名、落点、以及"打包自己的原包/其余物操作"。
    ///
    /// <para>他确认的七条里，这一组钉住的是"不碰磁盘也能力判"的那几条：</para>
    /// <list type="bullet">
    /// <item><description>分卷：内容 &lt;1 GiB → 2 卷、≥1 GiB → 3 卷；每卷上限 = ⌈内容 ÷ 卷数⌉ 向上取整到 MiB；</description></item>
    /// <item><description>单文件：在它旁边生成同名文件夹，那个文件夹算**其余物**；</description></item>
    /// <item><description>命名：装分卷的文件夹用源名、重名 <c>源名(1)</c>；最终 rar <c>源名.rar</c>，⛔ 绝不覆盖；</description></item>
    /// <item><description>落点：本地（源旁边，默认）/ 指定位置；</description></item>
    /// <item><description>两档操作与解压侧**完全独立**，并且"原包移入其余物 + 其余物要删"必须给出红字警告。</description></item>
    /// </list>
    /// </summary>
    public class PackingOptionsTests : IDisposable
    {
        private readonly string _root;

        public PackingOptionsTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerPackOptions", Guid.NewGuid().ToString("N"));
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

        // ================================================================ 分卷规则：按卷数

        /// <summary>他给的那个例子：**240 MB → 2 卷、每卷上限 120 MiB**（旧实现按 512 MiB 上限只会切 1 卷）。</summary>
        [Fact]
        public void 分卷规则_240MB切两卷_每卷上限120MiB()
        {
            long content = 240L * 1024 * 1024;

            Assert.Equal(2, PackingVolumeRule.TargetCount(content));

            long size = PackingVolumeRule.ComputeVolumeSize(content, PackingVolumeRule.TargetCount(content));

            Assert.Equal(120L * 1024 * 1024, size);
            Assert.Equal(2, PackingVolumeRule.PlannedCount(content, size));
        }

        [Fact]
        public void 分卷规则_一到一点五GiB切三卷()
        {
            long oneGiB = PackingVolumeRule.OneGiB;

            // 边界：**正好 1 GiB** 算"≥1 GiB" → 3 卷（他给的分档线就是这个意思）。
            Assert.Equal(3, PackingVolumeRule.TargetCount(oneGiB));
            Assert.Equal(342L * 1024 * 1024, PackingVolumeRule.ComputeVolumeSize(oneGiB, 3));
            Assert.Equal(3, PackingVolumeRule.PlannedCount(oneGiB, PackingVolumeRule.ComputeVolumeSize(oneGiB, 3)));

            long oneAndHalf = 1536L * 1024 * 1024;

            Assert.Equal(3, PackingVolumeRule.TargetCount(oneAndHalf));
            Assert.Equal(512L * 1024 * 1024, PackingVolumeRule.ComputeVolumeSize(oneAndHalf, 3));

            // 差一个字节就不到 1 GiB → 回到 2 卷那一档。
            Assert.Equal(2, PackingVolumeRule.TargetCount(oneGiB - 1));
        }

        /// <summary>
        /// 内容极小时不吃 0：上限至少 1 MiB（7z 的 <c>-v</c> 以 MiB 为单位）。
        /// 内容极大时夹到 16 GiB，**并把"会多于目标卷数"如实说出来**。
        /// </summary>
        [Fact]
        public void 分卷规则_极小与极大都不撒谎()
        {
            Assert.Equal(1L * 1024 * 1024, PackingVolumeRule.ComputeVolumeSize(0, 2));
            Assert.Equal(1L * 1024 * 1024, PackingVolumeRule.ComputeVolumeSize(1024, 2));

            long huge = 100L * 1024 * 1024 * 1024;

            long size = PackingVolumeRule.ComputeVolumeSize(huge, 3);

            Assert.Equal(PackingVolumeRule.AutoMaxVolumeBytes, size);

            // 说出来的"预计卷数"必须是照着**夹过之后**的上限算的（7 卷），不是目标 3 卷。
            Assert.Equal(7, PackingVolumeRule.PlannedCount(huge, size));

            string text = PackingVolumeRule.Describe(huge, size);

            Assert.Contains("按 3 卷切", text);
            Assert.Contains("预计 7 卷", text);
        }

        [Fact]
        public void 分卷规则_每卷上限永远是整MiB()
        {
            foreach (long content in new[] { 1L, 1024L, 1234567L, 240L * 1024 * 1024, 100L * 1024 * 1024 * 1024 })
            {
                foreach (int count in new[] { 1, 2, 3 })
                {
                    long size = PackingVolumeRule.ComputeVolumeSize(content, count);

                    Assert.Equal(0, size % (1024 * 1024));
                    Assert.InRange(size, PackingVolumeRule.MinVolumeBytes, PackingVolumeRule.AutoMaxVolumeBytes);
                }
            }
        }

        // ================================================================ 源：文件夹 / 单文件

        [Fact]
        public void 源是文件夹_就用它自己_没有临时文件夹()
        {
            string folder = Path.Combine(_root, "1111");
            Directory.CreateDirectory(folder);

            PackingSourceResolution resolution = PackingSourceResolver.Resolve(folder);

            Assert.True(resolution.Success, resolution.Reason);
            Assert.Equal(PackingSourceKind.Folder, resolution.Kind);
            Assert.Equal(folder, resolution.FolderToPack);
            Assert.Equal("1111", resolution.SourceName);
            Assert.Equal(string.Empty, resolution.WrapperFolderToCreate);
        }

        /// <summary>
        /// 单文件：在它旁边生成同名文件夹（<c>111.mp4</c> → <c>111\</c>），那个文件夹**算其余物**。
        /// ⛔ 原文件本身一个字节都不动（搬进去由服务层用硬链接/复制做，这里只算路径）。
        /// </summary>
        [Fact]
        public void 源是单文件_生成同名文件夹_并且那算其余物()
        {
            string file = Path.Combine(_root, "111.mp4");
            File.WriteAllBytes(file, new byte[32]);

            PackingSourceResolution resolution = PackingSourceResolver.Resolve(file);

            Assert.True(resolution.Success, resolution.Reason);
            Assert.Equal(PackingSourceKind.File, resolution.Kind);
            Assert.Equal("111", resolution.SourceName);
            Assert.Equal(Path.Combine(_root, "111"), resolution.WrapperFolderToCreate);
            Assert.Equal(resolution.WrapperFolderToCreate, resolution.FolderToPack);

            // 那个文件夹此刻还不存在（服务层才建）—— 规划阶段不许动盘。
            Assert.False(Directory.Exists(resolution.WrapperFolderToCreate));
            Assert.True(File.Exists(file));
        }

        [Fact]
        public void 源选择_不存在的路径与盘根都拒绝()
        {
            PackingSourceResolution missing = PackingSourceResolver.Resolve(Path.Combine(_root, "没有这个"));

            Assert.False(missing.Success);
            Assert.Contains("不存在", missing.Reason);

            Assert.False(PackingSourceResolver.Resolve(string.Empty).Success);

            string? root = Path.GetPathRoot(_root);

            if (!string.IsNullOrWhiteSpace(root) && Directory.Exists(root))
            {
                PackingSourceResolution atRoot = PackingSourceResolver.Resolve(root);

                Assert.False(atRoot.Success);
                Assert.Contains("盘根", atRoot.Reason);
            }
        }

        // ================================================================ 命名

        [Fact]
        public void 命名_装分卷的文件夹重名时让位成_名字1()
        {
            string existing = Path.Combine(_root, "1111");
            Directory.CreateDirectory(existing);

            Assert.Equal(Path.Combine(_root, "1111(1)"), PackingNaming.ResolveUniqueFolderPath(_root, "1111"));

            Directory.CreateDirectory(Path.Combine(_root, "1111(1)"));

            Assert.Equal(Path.Combine(_root, "1111(2)"), PackingNaming.ResolveUniqueFolderPath(_root, "1111"));

            // 同名**文件**也算占用（不然会覆盖）。
            File.WriteAllText(Path.Combine(_root, "1111(2)"), "x");

            Assert.Equal(Path.Combine(_root, "1111(3)"), PackingNaming.ResolveUniqueFolderPath(_root, "1111"));

            // 不重名时原样。
            Assert.Equal(Path.Combine(_root, "2222"), PackingNaming.ResolveUniqueFolderPath(_root, "2222"));
        }

        [Fact]
        public void 命名_最终rar永远用源名_重名时同样让位()
        {
            Assert.Equal(Path.Combine(_root, "1111.rar"), PackingNaming.ResolveUniqueFilePath(_root, "1111", ".rar"));

            File.WriteAllText(Path.Combine(_root, "1111.rar"), "x");

            Assert.Equal(Path.Combine(_root, "1111(1).rar"), PackingNaming.ResolveUniqueFilePath(_root, "1111", ".rar"));

            // 外层 7z 与 rar 一一对应（同一条规则）。
            Assert.Equal(Path.Combine(_root, "1111.7z"), PackingNaming.ResolveUniqueFilePath(_root, "1111", ".7z"));
        }

        // ================================================================ 落点

        [Fact]
        public void 落点_默认本地就是源旁边_指定位置就用它()
        {
            string folder = Path.Combine(_root, "1111");
            Directory.CreateDirectory(folder);

            PackingSourceResolution source = PackingSourceResolver.Resolve(folder);

            Assert.Equal(_root, PackingPaths.ResolveTargetDirectory(source, new PackingRunOptions()));

            string custom = Path.Combine(_root, "别处");
            Directory.CreateDirectory(custom);

            var options = new PackingRunOptions
            {
                TargetMode = PackingTargetMode.Custom,
                CustomOutputDirectory = custom
            };

            Assert.Equal(custom, PackingPaths.ResolveTargetDirectory(source, options));

            Assert.Equal(Path.Combine(_root, "1111.rar"), PackingPaths.ResolveRarPath(_root, "1111"));

            /*
             * ⚠ 装分卷的文件夹**必须让位**：这个用例里源文件夹 `1111` 还在（原包不动那一档），
             * 所以它叫 `1111(1)` —— 用户原话："如果用户选择不操作原包，那就是 1111(1) 文件夹"。
             * 而最终 rar 仍然以源名命名（上面那条）。
             */
            Assert.Equal(Path.Combine(_root, "1111(1)"), PackingPaths.ResolveVolumesFolder(_root, "1111"));
        }

        [Fact]
        public void 落点_容器内校验()
        {
            string root = Path.Combine(_root, "out");

            Assert.True(PackingPaths.IsInside(root, Path.Combine(root, "1111", "a.7z.001")));
            Assert.False(PackingPaths.IsInside(root, _root));
            Assert.False(PackingPaths.IsInside(root, Path.Combine(_root, "out2", "x")));
            Assert.False(PackingPaths.IsInside(null, Path.Combine(root, "x")));
            Assert.False(PackingPaths.IsInside(root, null));
        }

        // ================================================================ 两档操作（与解压侧独立）

        [Fact]
        public void 默认档_原包不动_其余物彻底删除()
        {
            PackingRunOptions options = PackingRunOptions.FromSettings(AppSettings.CreateDefault());

            Assert.Equal(PackingTargetMode.Local, options.TargetMode);
            Assert.Equal(PackingSourceHandling.KeepInPlace, options.SourceHandling);
            Assert.Equal(PackingRestHandling.Delete, options.RestHandling);

            Assert.False(options.WouldDeleteSource);
            Assert.Null(options.SourceLossWarning);
        }

        /// <summary>
        /// ⚠ 危险组合必须**当场红字警告**：原包移入其余物 + 其余物要删 = 连原包一起没。
        /// </summary>
        [Fact]
        public void 原包移入其余物加其余物删除_必须给红字警告()
        {
            var moveAndDelete = new PackingRunOptions
            {
                SourceHandling = PackingSourceHandling.MoveToRest,
                RestHandling = PackingRestHandling.Delete
            };

            Assert.True(moveAndDelete.WouldDeleteSource);
            Assert.Contains("连你的原文件夹", moveAndDelete.SourceLossWarning);
            Assert.Contains("不动", moveAndDelete.SourceLossWarning);

            var moveAndRecycle = new PackingRunOptions
            {
                SourceHandling = PackingSourceHandling.MoveToRest,
                RestHandling = PackingRestHandling.RecycleBin
            };

            Assert.True(moveAndRecycle.WouldDeleteSource);
            Assert.Contains("回收站", moveAndRecycle.SourceLossWarning);

            // 其余物不动那一档不警告（原包只是被搬进其余物，还在）。
            var moveAndKeep = new PackingRunOptions
            {
                SourceHandling = PackingSourceHandling.MoveToRest,
                RestHandling = PackingRestHandling.Keep
            };

            Assert.False(moveAndKeep.WouldDeleteSource);
            Assert.Null(moveAndKeep.SourceLossWarning);
        }

        [Fact]
        public void 选择能存进设置也能读回来()
        {
            var settings = AppSettings.CreateDefault();

            var options = new PackingRunOptions
            {
                TargetMode = PackingTargetMode.Custom,
                CustomOutputDirectory = @"D:\出包",
                SourceHandling = PackingSourceHandling.MoveToRest,
                RestHandling = PackingRestHandling.RecycleBin
            };

            options.SaveTo(settings);

            Assert.Equal("Custom", settings.PackTargetMode);
            Assert.Equal(@"D:\出包", settings.PackCustomOutputDirectory);
            Assert.Equal("MoveToRest", settings.PackSourceHandling);
            Assert.Equal("RecycleBin", settings.PackRestHandling);

            PackingRunOptions reloaded = PackingRunOptions.FromSettings(settings);

            Assert.Equal(PackingTargetMode.Custom, reloaded.TargetMode);
            Assert.Equal(@"D:\出包", reloaded.CustomOutputDirectory);
            Assert.Equal(PackingSourceHandling.MoveToRest, reloaded.SourceHandling);
            Assert.Equal(PackingRestHandling.RecycleBin, reloaded.RestHandling);

            // 认不出来的值一律落回默认（旧配置 / 手改坏了都不许炸）。
            settings.PackTargetMode = "??";
            settings.PackSourceHandling = "??";
            settings.PackRestHandling = "??";

            PackingRunOptions fallback = PackingRunOptions.FromSettings(settings);

            Assert.Equal(PackingTargetMode.Local, fallback.TargetMode);
            Assert.Equal(PackingSourceHandling.KeepInPlace, fallback.SourceHandling);
            Assert.Equal(PackingRestHandling.Delete, fallback.RestHandling);
        }

        /// <summary>
        /// ⛔ 打包的两档与解压的 <c>SourceHandling</c> / <c>RestHandlingAfterVerify</c> **各存各的**：
        /// 改一边绝不许影响另一边（用户原话："两者绝对不能同步"）。
        /// </summary>
        [Fact]
        public void 打包的两档与解压侧互不影响()
        {
            var settings = AppSettings.CreateDefault();

            string extractSource = settings.SourceHandling;
            string extractRest = settings.RestHandlingAfterVerify;

            new PackingRunOptions
            {
                SourceHandling = PackingSourceHandling.MoveToRest,
                RestHandling = PackingRestHandling.Keep
            }.SaveTo(settings);

            Assert.Equal(extractSource, settings.SourceHandling);
            Assert.Equal(extractRest, settings.RestHandlingAfterVerify);

            Assert.Equal("MoveToRest", settings.PackSourceHandling);
            Assert.Equal("Keep", settings.PackRestHandling);

            // 反向：改解压侧也不许碰到打包侧。
            settings.SourceHandling = "MoveToRest";
            settings.RestHandlingAfterVerify = "RecycleBin";

            Assert.Equal("MoveToRest", settings.PackSourceHandling);
            Assert.Equal("Keep", settings.PackRestHandling);
        }

        [Fact]
        public void 进日志的那句话不含密码且说清三档()
        {
            var options = new PackingRunOptions
            {
                TargetMode = PackingTargetMode.Custom,
                CustomOutputDirectory = @"D:\出包",
                SourceHandling = PackingSourceHandling.KeepInPlace,
                RestHandling = PackingRestHandling.Delete
            };

            string line = options.DescribeForLog();

            Assert.Contains("指定位置", line);
            Assert.Contains(@"D:\出包", line);
            Assert.Contains("原包操作：不动", line);
            Assert.Contains("其余物操作：彻底删除", line);
        }
    }
}
