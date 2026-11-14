using ArchiveFixer.Models;
using System.Collections.ObjectModel;
using System.Text;

namespace ArchiveFixer.Services;

/// <summary>
/// **屏幕日志**（<see cref="LogService.Logs"/>）的唯一所有者：加一行、封顶、清空、整段取文本。
///
/// <para>为什么单独一个类：屏幕集合是给界面绑定的那一份，它既不是文件日志、也不是诊断事件，
/// 却和它们共用 <see cref="LogOperationContext.SyncRoot"/> 那一把锁（拆分前后都是）。
/// 把它抽出来以后，写文件的那条路不再顺带知道"界面最多留 3000 行"这种事。</para>
///
/// <para>⛔ 封顶数字与跨线程容错口径与拆分前一致：超 3000 行从最旧的行开始丢；
/// 跨线程直接动集合抛异常时**吞掉**（界面可以选择订阅 <see cref="LogService.LogAdded"/> 自己加）。</para>
/// </summary>
internal sealed class LogScreenBuffer
{
    /// <summary>屏幕日志上限（防止界面集合无限增长；与拆分前同一个数字）。</summary>
    private const int MaxEntries = 3000;

    /// <summary>界面直接绑定的那一份集合（引用恒定：只加/删元素，不整体替换）。</summary>
    internal ObservableCollection<OperationLogItem> Items { get; } = new();

    /// <summary>加一行并封顶。⛔ 调用方必须已经持有 <see cref="LogOperationContext.SyncRoot"/>。</summary>
    internal void Add(OperationLogItem item)
    {
        try
        {
            Items.Add(item);

            // 防止屏幕日志无限增长。
            while (Items.Count > MaxEntries)
            {
                Items.RemoveAt(0);
            }
        }
        catch
        {
            // 如果跨线程直接访问 ObservableCollection 出错，
            // ViewModel 可选择订阅 LogAdded 后在 UI 线程添加。
        }
    }

    /// <summary>清空屏幕日志（不删除文件日志）。</summary>
    internal void Clear()
    {
        try
        {
            Items.Clear();
        }
        catch
        {
            // 忽略 UI 线程问题。
        }
    }

    /// <summary>把屏幕日志整段拼成文本（每行 <c>OperationLogItem.DisplayText</c>）。</summary>
    internal string ToText()
    {
        var builder = new StringBuilder();

        foreach (OperationLogItem item in Items)
        {
            builder.AppendLine(item.DisplayText);
        }

        return builder.ToString();
    }
}
