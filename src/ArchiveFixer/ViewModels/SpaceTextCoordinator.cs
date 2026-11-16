using ArchiveFixer.Models;
using ArchiveFixer.Services;

namespace ArchiveFixer.ViewModels;

/// <summary>空间紧张模式/并发建议文案刷新。</summary>
internal sealed class SpaceTextCoordinator
{
    private readonly MainViewModel _vm;

    public SpaceTextCoordinator(MainViewModel vm)
    {
        _vm = vm;
    }

    internal void RefreshSpaceModeText()
    {
        if (_vm.Settings == null)
        {
            return;
        }

        // 这一句现在只说"源包 + 其余物"两档（2026-09-25 第 32 条：危险模式与自测凭证整块退役）。
        _vm.SetSpaceModeText(_vm.ExtractionCoordinator?.Schedule.DescribeSpaceMode() ?? string.Empty);

        RefreshSpaceTightOverrideText();

        _vm.SetParallelAdviceText("点「按设备算建议」查看源/落点盘的并发建议（只提示，不会改你选的值）");
    }

    internal void RefreshSpaceTightOverrideText()
    {
        /*
         * 提示说清空间不足会删除源包，但失败/部分完成/取消不删。
         * 会删源包那一档说"会删"，安全档说"一个字节都不动" —— 一条文案盖两种行为就是撒谎。
         */
        _vm.SetSpaceTightOverrideText(!_vm.SpaceTightMode
            ? string.Empty
            : StatusText.SpaceTightOverrideNotice);

        _vm.NotifySpaceTightOverrideChanged();
    }
}
