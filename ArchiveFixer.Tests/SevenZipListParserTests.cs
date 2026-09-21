using System.Linq;
using ArchiveFixer.Engines;
using ArchiveFixer.Engines.SevenZip;
using ArchiveFixer.Models;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// <c>7z l -slt</c> 解析的回归测试。
    ///
    /// 这块的价值在于：它给出的"条目数 / 总大小"是**解压后校验**的预期值，
    /// 而校验又是**清理源包**（不可逆）的前置条件。数错一个就可能删错或漏删。
    ///
    /// 用真实的 -slt 文本片段做输入（不是想象的格式）：
    /// 下面的 7z 段是 `7z l -slt` 对含一个目录的 7z 包的真实输出结构，
    /// 目录条目**没有 Folder 键**、只有 `Attributes = D` —— 这正是踩过的坑。
    /// </summary>
    public class SevenZipListParserTests
    {
        private const string SevenZipWithDirectory = @"
7-Zip 26.01 (x64) : Copyright (c) 1999-2026 Igor Pavlov : 2026-04-27

Scanning the drive for archives:
1 file, 170 bytes (1 KiB)

Listing archive: C:\t\a.7z

--
Path = C:\t\a.7z
Type = 7z
Physical Size = 170
Headers Size = 159
Method = LZMA2:12
Solid = -
Blocks = 1

----------
Path = packA
Size = 0
Packed Size = 0
Modified = 2026-09-21 19:39:33.3240651
Attributes = D
CRC = 
Encrypted = -
Method = 
Block = 

Path = packA\a.txt
Size = 7
Packed Size = 11
Modified = 2026-09-21 19:39:33.3373021
Attributes = A
CRC = 46CE8AAC
Encrypted = -
Method = LZMA2:12
Block = 0

";

        private const string ZipWithFolderFlag = @"
Path = C:\t\b.zip
Type = Zip
Physical Size = 200

----------
Path = docs
Size = 0
Packed Size = 0
Attributes = D_
Folder = +
Encrypted = -

Path = docs\x.txt
Size = 12
Packed Size = 12
Attributes = A
Folder = -
Encrypted = +

";

        private static ArchiveListResult Parse(string text)
        {
            return SevenZipListParser.Parse(text, @"C:\t\a.7z", "sevenzip", "26.01");
        }

        [Fact]
        public void 七z格式的目录条目要被算成目录而不是文件()
        {
            ArchiveListResult result = Parse(SevenZipWithDirectory);

            Assert.True(result.Success);
            Assert.Equal(2, result.Entries.Count);
            Assert.Equal(1, result.FileCount);
            Assert.Equal(1, result.DirectoryCount);
            Assert.Equal(7, result.TotalUncompressedSize);

            Assert.True(result.Entries.Single(e => e.Path == "packA").IsDirectory);
            Assert.False(result.Entries.Single(e => e.Path == @"packA\a.txt").IsDirectory);
        }

        [Fact]
        public void zip的Folder标记目录同样要算成目录()
        {
            ArchiveListResult result = Parse(ZipWithFolderFlag);

            Assert.Equal(1, result.FileCount);
            Assert.Equal(1, result.DirectoryCount);
            Assert.Equal(12, result.TotalUncompressedSize);
        }

        [Fact]
        public void 加密标记要被识别出来()
        {
            ArchiveListResult result = Parse(ZipWithFolderFlag);

            Assert.True(result.IsEncrypted, "条目里出现 Encrypted = + 时应判定为加密");
        }

        [Fact]
        public void 空输出不抛异常且返回空结果()
        {
            ArchiveListResult result = Parse(string.Empty);

            Assert.True(result.Success);
            Assert.Empty(result.Entries);
            Assert.Equal(0, result.FileCount);
            Assert.Equal(0, result.TotalUncompressedSize);
        }

        [Fact]
        public void 分卷包的文件名要被认出来()
        {
            ArchiveListResult result = SevenZipListParser.Parse(
                SevenZipWithDirectory,
                @"C:\t\volume.7z.001",
                "sevenzip",
                "26.01");

            Assert.True(result.IsMultiVolume, "文件名本身是分卷标记时要判定为分卷");
        }
    }
}
