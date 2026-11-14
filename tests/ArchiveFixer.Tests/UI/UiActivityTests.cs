using ArchiveFixer.Services;
using Xunit;

namespace ArchiveFixer.Tests;

/// <summary>
/// UI 卡顿归因：后台清理/扫描的标记不能覆盖 UI 阶段，否则监控会把停顿记到
/// “恢复后才开始”的那个后台动作头上（2026-11-11 真机 54 秒/117 秒两条都踩过）。
/// </summary>
[Collection("ArchiveFixerGlobalState")]
public sealed class UiActivityTests
{
    [Fact]
    public void 后台标记不覆盖UI阶段_但会作为后台事实显示()
    {
        try
        {
            UiActivity.Mark("产物扫描", "第 1 轮", "收集内层产物");
            UiActivity.MarkBackground("工作区清理", "1~12.rar", "递归删除工作区");

            string snapshot = UiActivity.Snapshot();

            Assert.Contains("阶段=产物扫描", snapshot, System.StringComparison.Ordinal);
            Assert.DoesNotContain("阶段=工作区清理", snapshot, System.StringComparison.Ordinal);
            Assert.Contains("后台=工作区清理", snapshot, System.StringComparison.Ordinal);
            Assert.Contains("标记于", snapshot, System.StringComparison.Ordinal);
        }
        finally
        {
            UiActivity.Mark("空闲");
            UiActivity.MarkBackground(string.Empty);
        }
    }
}
