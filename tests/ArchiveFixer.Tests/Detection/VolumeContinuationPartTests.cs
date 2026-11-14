using ArchiveFixer.ViewModels;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 「后续卷」判断（`OneClickCoordinator.IsVolumeContinuationPart`）也要认**被伪装过的名字**。
    ///
    /// <para>2026-09-28 审计逮到：全项目最后一处还在用"前缀档"（只认干净的 `.001`/`.z01`）的地方就是它，
    /// 于是第 2 层扫描把 `amb909.7删z.00除2` 这种后续卷判成"不是后续卷"，与探测器、改名闸门口径不一致
    /// （同一个判据第三次翻车都是"两处不同步"）。这里钉住：容差档认得出来的，它也必须认；
    /// 而**起点**（`.001`）照旧不算后续卷。</para>
    /// </summary>
    public class VolumeContinuationPartTests
    {
        [Theory]
        // 老口径：干净的后续卷
        [InlineData(@"C:\t\x.7z.002", true)]
        [InlineData(@"C:\t\x.7z.001", false)]
        [InlineData(@"C:\t\x.z01", true)]
        [InlineData(@"C:\t\x.r00", true)]
        // 新口径：名字被伪装过的后续卷（这次审计要修的正是这些）
        [InlineData(@"C:\t\amb909.7删z.00除2", true)]
        [InlineData(@"C:\t\amb909.7sz.00c1", false)]   // 它其实**是**起点（001），只是被伪装了
        [InlineData(@"C:\t\x.7z.002sc", true)]
        // 与分卷无关的名字：不许误判
        [InlineData(@"C:\t\movie.mp4", false)]
        [InlineData(@"C:\t\readme.txt", false)]
        public void 后续卷判断_容差档认得出来的也算(string path, bool expected)
        {
            Assert.Equal(expected, OneClickCoordinator.IsVolumeContinuationPart(path));
        }
    }
}
