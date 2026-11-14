using ArchiveFixer.Services;

namespace ArchiveFixer.ViewModels.PasswordList;

/// <summary>
/// 「写回密码本 / 保存密码列表」这条路的日志落点。
///
/// <para>落点与主界面同一个数据根目录（<see cref="PasswordService.DataRootDirectory"/>，
/// 主界面刻意把它与 <see cref="PathService.DataRootDirectory"/> 同步成同一个值）。
/// 传进来的文案**只有数量与文件名**，绝不含密码明文（§8 隐私红线）。</para>
/// </summary>
internal sealed class PasswordWriteBackLog
{
    private readonly PasswordService _passwordService;

    internal PasswordWriteBackLog(PasswordService passwordService)
    {
        _passwordService = passwordService;
    }

    /// <summary>写一条写回日志；写不出来也不让"写回"这个动作失败（日志是附带的，文件才是结果）。</summary>
    internal void Write(string message)
    {
        try
        {
            var pathService = new PathService
            {
                DataRootDirectory = _passwordService.DataRootDirectory
            };

            var logService = new LogService(pathService);

            /*
             * Initialize 在这里的作用只有一个：把这个 LogService 的"当前日志文件"算出来。
             * 传 enableFileLog: false 是故意的 —— 不带时间戳的文件名会**把主界面这一天已经写下的日志清空**
             * （Initialize 首行是 WriteAllText），那等于为了记一条日志毁掉当天的日志。
             * 关掉它，Write 就走"追加到那个按时间戳命名的文件"，与主界面同一个落点、不互相覆盖。
             */
            logService.Initialize(enableFileLog: false);

            logService.WriteInfo(message);
        }
        catch
        {
            // 写不出日志不该让"写回"这个动作失败：日志是附带的，文件才是结果。
        }
    }
}
