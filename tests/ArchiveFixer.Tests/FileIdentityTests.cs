using System;
using System.IO;
using ArchiveFixer.Detection;
using ArchiveFixer.Helpers;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// **"这是不是同一份文件"**（真机 CCCC 2026-10-06 20:55，用户原话：
    /// 「为什么文件真正的名字不看」）。
    ///
    /// <para><b>现场</b>：接片那一档在"这一组该在的那一层"另起了一个**零字节硬链接**
    /// （`…\111\111\111.z03`），用户那一份还在他自己那个目录里（`…\111(4)\111.z03`）
    /// —— 两条路径指着**同一份字节**。账里记的只是其中一条 ⇒ 按路径字符串比永远对不上
    /// ⇒ 那一单落到「判不出 ⇒ 什么都不做」，源片一直留着（`111(4)\111.z03` 就是这么留下的）。</para>
    ///
    /// <para><b>红检</b>：把 <see cref="FileIdentity.IsSamePhysicalFile"/> 改成"只比路径字符串"
    /// ⇒ <c>硬链接双胞胎_两条路径_算同一份文件</c> 当场变红。</para>
    /// </summary>
    public class FileIdentityTests : IDisposable
    {
        private readonly string _root;

        public FileIdentityTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "af-file-identity-" + Guid.NewGuid().ToString("N"));
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
                // 临时目录清不掉不影响结论。
            }
        }

        /// <summary>**硬链接双胞胎：两条不同的路径 = 同一份文件**（这就是真机那一格）。</summary>
        [Fact]
        public void 硬链接双胞胎_两条路径_算同一份文件()
        {
            string original = Path.Combine(_root, "111(4)", "111.z03");
            string link = Path.Combine(_root, "111", "111", "111.z03");

            Directory.CreateDirectory(Path.GetDirectoryName(original)!);
            Directory.CreateDirectory(Path.GetDirectoryName(link)!);

            File.WriteAllBytes(original, new byte[4096]);

            Assert.True(HardLinkHelper.TryCreateHardLink(link, original));
            Assert.False(string.Equals(original, link, StringComparison.OrdinalIgnoreCase));

            Assert.True(FileIdentity.IsSamePhysicalFile(original, link));
            Assert.True(FileIdentity.IsSamePhysicalFile(link, original));
        }

        /// <summary>⛔ 两份**不同**的文件不许认成同一份（判据 = 卷序列号 + 文件索引，不是名字 / 体积 / 时间）。</summary>
        [Fact]
        public void 两份不同的文件_不算同一份()
        {
            string one = Path.Combine(_root, "a.bin");
            string two = Path.Combine(_root, "b.bin");

            File.WriteAllBytes(one, new byte[4096]);
            File.WriteAllBytes(two, new byte[4096]);

            Assert.False(FileIdentity.IsSamePhysicalFile(one, two));

            // 同一个名字、同一个体积、同一个目录里的**另一个名字**也一样不许认错。
            string sameName = Path.Combine(_root, "sub", "a.bin");

            Directory.CreateDirectory(Path.GetDirectoryName(sameName)!);
            File.WriteAllBytes(sameName, new byte[4096]);

            Assert.False(FileIdentity.IsSamePhysicalFile(one, sameName));
        }

        /// <summary>⛔ 文件不在 / 路径为空 ⇒ **不认**（判不出 ⇒ 什么都不做）。</summary>
        [Fact]
        public void 读不到就不认()
        {
            string missing = Path.Combine(_root, "nope.bin");
            string present = Path.Combine(_root, "here.bin");

            File.WriteAllBytes(present, new byte[16]);

            Assert.False(FileIdentity.IsSamePhysicalFile(missing, present));
            Assert.False(FileIdentity.IsSamePhysicalFile(present, missing));
            Assert.False(FileIdentity.IsSamePhysicalFile(string.Empty, present));
            Assert.False(FileIdentity.IsSamePhysicalFile(null, null));
        }
    }
}
