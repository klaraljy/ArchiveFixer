using ArchiveFixer.Services;
using System;
using System.Threading.Tasks;

namespace ArchiveFixer.ViewModels
{
    /// <summary>
    /// UI 线程这一步的接线（第 14 轮③：实现已搬进 <c>Services/UiThreadStep.cs</c> 独立类，
    /// 这里只留一行调用；静态投递工具直接从那个类调，不再在协调器里留副本）。
    /// </summary>
    internal sealed partial class ExtractionCoordinator
    {
        private readonly UiThreadStep _uiThread;

        private Task<bool> ShowConfirmOnUiThreadAsync(string message) => _uiThread.ShowConfirmAsync(message);

        private Task ShowWarningOnUiThreadAsync(string message) => _uiThread.ShowWarningAsync(message);
    }
}
