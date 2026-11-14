using ArchiveFixer.Models;
using ArchiveFixer.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ArchiveFixer.ViewModels.PasswordList;

/// <summary>
/// 「记住密码本路径」与设置读取：把路径同时写进 <c>appsettings.json</c> 与侧车文件，
/// 并在读设置失败时按默认设置继续。
///
/// <para>设置文件按"数据根目录"定位：<see cref="PasswordService.DataRootDirectory"/> 与
/// <see cref="PathService.DataRootDirectory"/> 被主界面刻意同步成同一个值
/// （MainViewModel.ApplyEngineSettings），所以这里用前者构造 <see cref="PathService"/>，
/// 两边算出来的是同一个目录。</para>
/// </summary>
internal sealed class PasswordBookRemembering
{
    private readonly PasswordListViewModel _vm;

    internal PasswordBookRemembering(PasswordListViewModel vm)
    {
        _vm = vm;
    }

    /// <summary>
    /// 记住密码本路径。
    ///
    /// <para>两个地方都要写，少一个就会出现"这次导了、下次启动不认"：</para>
    /// <list type="number">
    /// <item><description><c>appsettings.json</c> 的 <c>PasswordBookPath</c> —— 与主界面导入走的是同一个设置项；</description></item>
    /// <item><description>侧车文件 <c>password-book.path</c> —— 由 <see cref="PasswordService.ImportPasswordList"/> 自己写。</description></item>
    /// </list>
    /// </summary>
    internal void RememberPasswordBookPath(string path)
    {
        try
        {
            SettingsService settingsService = CreateSettingsService();

            // 先 Load 再改：不能拿一份全新的默认设置整体覆盖用户的 appsettings.json。
            AppSettings settings = settingsService.Load();

            /*
             * 2026-09-24 起"记住哪一本"是**有序清单**（多本密码本）：这里是**追加**，
             * 不是在清单上覆盖 —— 覆盖的后果正是用户报的那个现象"导入第二本，第一本不见了"。
             * 去重按 Windows 路径口径（大小写不敏感），重复导入同一本不会长出第二项。
             */
            settings.PasswordBookPaths = AppendBookPath(settings.PasswordBookPaths, path);

            // 老字段一起维护（回退到旧版本时那边只认它）。
            settings.PasswordBookPath = path;

            settingsService.Save(settings);

            /*
             * 侧车文件：ImportPasswordList 顺手写的那一份在这里补上。
             * 用户如果是"先写回、再首次导入"这个顺序，少了这一笔主界面就会说"没配过密码本"。
             * 只写路径，绝不碰密码本内容本身。
             */
            WritePasswordBookSidecar(path);
        }
        catch (Exception ex)
        {
            // 记不住路径不该让导入本身失败，但要说清楚 —— 否则用户下次启动发现密码本没自动加载会以为是 bug。
            _vm.Message += "（提示：路径没能记进设置，下次启动可能不会自动加载：" + ex.Message + "）";
        }
    }

    /// <summary>读设置（只读，不保存）。定位口径与 <see cref="RememberPasswordBookPath"/> 完全一致。</summary>
    internal AppSettings LoadSettings()
    {
        try
        {
            return CreateSettingsService().Load();
        }
        catch (Exception ex)
        {
            // 设置读不出来不该让写回整条路瘫掉：用一份默认设置继续（它会走"问一次选文件"那条分支）。
            _vm.Message += "（提示：设置读取失败，已按默认设置继续：" + ex.Message + "）";

            return AppSettings.CreateDefault();
        }
    }

    /// <summary>构造设置服务（写回与"记住路径"共用，保证两处定位到同一个文件）。</summary>
    private SettingsService CreateSettingsService()
    {
        var pathService = new PathService
        {
            DataRootDirectory = _vm.Service.DataRootDirectory
        };

        return new SettingsService(pathService);
    }

    /// <summary>把一本密码本追加进清单（已存在则原样返回，顺序不动）。</summary>
    private static List<string> AppendBookPath(IEnumerable<string>? existing, string path)
    {
        var result = new List<string>();

        if (existing != null)
        {
            foreach (string item in existing)
            {
                if (!string.IsNullOrWhiteSpace(item))
                {
                    result.Add(item);
                }
            }
        }

        foreach (string item in result)
        {
            if (string.Equals(item, path, StringComparison.OrdinalIgnoreCase))
            {
                return result;
            }
        }

        if (!string.IsNullOrWhiteSpace(path))
        {
            result.Add(path);
        }

        return result;
    }

    /// <summary>把密码本路径写进侧车文件（与 <see cref="PasswordService"/> 同一份口径与位置）。</summary>
    private void WritePasswordBookSidecar(string path)
    {
        string dataRoot = _vm.Service.DataRootDirectory;

        // 未装配数据根（测试/夹具里的裸 PasswordService）⇒ 一个字节都不写。
        if (dataRoot.Length == 0)
        {
            return;
        }

        Directory.CreateDirectory(dataRoot);

        File.WriteAllText(
            Path.Combine(dataRoot, "password-book.path"),
            path,
            new UTF8Encoding(false));
    }
}
