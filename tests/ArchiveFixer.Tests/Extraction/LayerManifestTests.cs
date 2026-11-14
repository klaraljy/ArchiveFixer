using ArchiveFixer.Engines;
using ArchiveFixer.Extraction;
using System.Collections.Generic;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// **空清单 = 没有清单**（用户 2026-10-02 真机实锤，见 <c>UnRarLocalizedListingTests</c> 的现场说明）。
    ///
    /// <para>一条展开多层的续解链，L3 的预期来自**叶子层**的清单（<see cref="LayerManifest"/>）。
    /// 真机上那份清单被读成"0 个文件 / 0 字节"却标着 <c>Available = true</c> ——
    /// 于是 `产物 ≥ 0` 天然成立 ⇒ 报「可证完整」⇒ 源包被搬走并永久删除。
    /// 空清单在核对这件事上提供的信息是零，所以它必须落回"取不到清单"那一档（= 判不出 = 什么都不做）。</para>
    /// </summary>
    public class LayerManifestTests
    {
        [Fact]
        public void 清单成功但0个条目_按取不到清单办()
        {
            LayerManifest manifest = LayerManifest.From(new ArchiveListResult
            {
                Success = true,
                FileCount = 0,
                TotalUncompressedSize = 0,
                Entries = new List<ArchiveEntry>()
            });

            Assert.False(manifest.Available, "空清单核对不了产物，不许当成「有清单」");
            Assert.Contains("空的", manifest.Describe(), System.StringComparison.Ordinal);
        }

        [Fact]
        public void 清单成功且有条目_照旧可用()
        {
            LayerManifest manifest = LayerManifest.From(new ArchiveListResult
            {
                Success = true,
                FileCount = 3,
                TotalUncompressedSize = 1024,
                Entries = new List<ArchiveEntry>()
            });

            Assert.True(manifest.Available);
            Assert.Equal(3, manifest.FileCount);
            Assert.Equal(1024, manifest.TotalSize);
        }

        [Fact]
        public void 列目录失败_照旧是取不到清单()
        {
            LayerManifest manifest = LayerManifest.From(new ArchiveListResult
            {
                Success = false,
                Message = "测试用：列目录失败"
            });

            Assert.False(manifest.Available);
        }
    }
}
