using System;
using System.Threading.Tasks;

namespace ArchiveFixer.ViewModels;

/// <summary>
/// UI 线程能力的宿主注入点：ViewModels 只认这些委托，不直接读 Application.Current/Dispatcher。
/// App 在启动时用 WPF Dispatcher 实现；无界面宿主保持 null。
/// </summary>
internal static class UiThread
{
    public static Func<bool>? IsAvailable { get; set; }

    public static Func<bool>? CheckAccess { get; set; }

    public static Action<Action>? BeginInvoke { get; set; }

    public static Func<Func<Task>, Task>? InvokeAsync { get; set; }

    public static Func<Action<string>, IDisposable?>? StartMonitor { get; set; }

    public static Func<TimeSpan, Action, IDisposable?>? StartTimer { get; set; }
}
