using System;
using System.Collections.Generic;
using System.IO;

namespace ArchiveFixer.Models;

/// <summary>
/// **这一行显示的是"用户导入的那个文件"**：它最新已知的位置、体积，以及列表那几列该显示什么。
///
/// <para>为什么需要它（用户 2026-10-07：「文件名、大小、后缀、检测格式、状态，每个都有问题，
/// 现在居然还是 <c>111.rar</c>，你是不是一直锁定到原包，根本就没有看最新的东西」）：
/// 批末补判会把这一单的**起点**改写到入口包上（真机那一行就成了 48.35 MiB 的
/// <c>111\111\111.zip</c>）；那一份**不是用户的文件**，可它一改写就把
/// <see cref="ArchiveTask.FileName"/> / <see cref="ArchiveTask.SourceSizeText"/> 整行刷新成过程物。
/// 老写法在"记着的那条已经不在了"时正是回落到这些被改写的值 ⇒ 列表里那一行变成 48MB 的入口包。</para>
///
/// <para>维护点只有三处（⛔ 不新增第四个写入点）：构造时 = 最初导入路径；<see cref="ArchiveTask.CurrentPath"/>
/// **同目录**改写时跟着走（改名 / 组内换卷）；<see cref="ArchiveTask.ShowUserFileIdentity"/> 采纳提示时。
/// 跨目录改写（补判指向入口包）**一律不采纳** —— 那已经不是用户那个文件了。</para>
///
/// <para>⛔ **只给显示用**：这里算出来的路径 / 体积 / 后缀不参与任何解压、删除、统计判据；
/// 唯一的读盘动作是 <see cref="Remember"/> 里的 <c>File.Exists</c> + <c>FileInfo.Length</c>
/// （只读、只记数、一个字节都不改盘上任何东西）。</para>
///
/// <para><b>失败模型</b>：路径形状不合法 / 文件读不到 ⇒ 回落到下一档候选，最终回落到调用方给的
/// 兼容值（<see cref="ArchiveTask.FileName"/> 等），⛔ 不抛、⛔ 不编数字。</para>
///
/// <para>生命周期：随任务创建、活到任务被丢弃；状态只有三个字段，写入点见 <see cref="Remember"/> /
/// <see cref="AdoptUserFacing"/>，没有单独的清空出口（采纳之后只会跟着候选往前走）。</para>
/// </summary>
internal sealed class UserFileIdentity
{
    private string _ownFilePath = string.Empty;
    private long _ownFileSizeBytes;
    private string _userFacingFilePath = string.Empty;

    /// <summary>用户那个文件的最新已知位置（空 = 还没记住；只给显示用，⛔ 不参与任何判据）。</summary>
    internal string OwnFilePath => _ownFilePath;

    /// <summary>这一行是否已经被采纳成"用户自己那个文件"（见 <see cref="AdoptUserFacing"/>）。</summary>
    internal bool HasUserFacingFile => _userFacingFilePath.Length > 0;

    /// <summary>
    /// 记住"用户那个文件"的最新位置与体积（<see cref="_ownFilePath"/> 的**唯一**写入实现）。
    ///
    /// <para>体积只在文件真的在时更新（被搬走 / 被删之后保留最后一次看到的那个数 ——
    /// 那一格要写的是"你那个文件多大"，⛔ 不是"现在盘上还剩什么"）。任何 IO 意外都吞掉：
    /// 这是显示，⛔ 绝不允许因为读不到就影响解压。</para>
    /// </summary>
    /// <returns>位置真的变了 ⇒ <c>true</c>（调用方据此发那几列的变更通知）。</returns>
    internal bool Remember(string? path, bool allowReplace)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        if (!allowReplace && _ownFilePath.Length > 0)
        {
            return false;
        }

        bool changed = !string.Equals(_ownFilePath, path, StringComparison.OrdinalIgnoreCase);

        _ownFilePath = path!;

        try
        {
            if (File.Exists(path))
            {
                _ownFileSizeBytes = new FileInfo(path!).Length;
            }
        }
        catch
        {
            // 读不到就保留上一次记下的体积。
        }

        return changed;
    }

    /// <summary>
    /// 这一刻**真正拿来显示**的那份文件：按"最新的盘上事实"逐条问，问不到才算空。
    ///
    /// <para>⛔ 为什么要核盘（用户 2026-10-06：「列表里面显示的没有一个是对的」「后缀也不同步」）：
    /// 记下来的路径会**过期**（改名 / 被搬走 / 被删）—— 拿一个已经不存在的名字去显示，
    /// 那一行就成了"名字是旧的、别的列是新的"的自相矛盾。</para>
    ///
    /// <para>⛔ 为什么要**跟着文件走**（用户 2026-10-07：「你是不是一直锁定到原包，根本就没有看最新的
    /// 东西」）：这一行属于**用户导入的那个文件**，而它在程序手里会改名（改回标准卷名）、
    /// 会按设置搬进它自己的「其余物」—— 只认"最初导入那一刻的那条路径"就会一直显示旧名字、旧位置。
    /// 判定顺序（先用先赢，⛔ 只读、一个字节都不改盘上任何东西）：① 记着的那条（还在 ⇒ 就是它）→
    /// ② 最初导入那条 → ③ 当前处理路径（**只在同一目录里**才认：批末补判会把起点改写到别的目录的
    /// 过程物上，那已经不是用户那个文件了）→ ④ 它自己的「其余物」目录里同名的那一份。</para>
    /// </summary>
    internal string ResolveDisplayPath(string originalPath, string currentPath, string restDirectoryPath)
    {
        if (_userFacingFilePath.Length > 0 && TaskDisplayFacts.ExistsOnDisk(_userFacingFilePath))
        {
            return _userFacingFilePath;
        }

        if (TaskDisplayFacts.ExistsOnDisk(originalPath))
        {
            return originalPath;
        }

        string originalDirectory = TaskDisplayFacts.SafeDirectoryOf(originalPath);

        if (originalDirectory.Length > 0
            && TaskDisplayFacts.ExistsOnDisk(currentPath)
            && string.Equals(TaskDisplayFacts.SafeDirectoryOf(currentPath), originalDirectory, StringComparison.OrdinalIgnoreCase))
        {
            return currentPath;
        }

        return FindOwnFileInRestDirectory(originalPath, restDirectoryPath);
    }

    /// <summary>④ 源包按设置搬进**它自己的**「其余物」之后，用户那一份就在那儿（搬运不改名）。</summary>
    private string FindOwnFileInRestDirectory(string originalPath, string restDirectoryPath)
    {
        if (string.IsNullOrWhiteSpace(restDirectoryPath))
        {
            return string.Empty;
        }

        /*
         * ⛔ 名字要试**两个**：最初导入那个名字（`OriginalPath`）与**最新已知的那个名字**
         * （`_ownFilePath` —— 改名之后就是它）。只试前者会漏掉"先改名、后被搬进其余物"那一档，
         * 于是那一行还停在旧位置（真机 2026-10-07：`111.z03` 已经在 `…\其余物\111.z03`，
         * 那一行却还写着 `111(4)\111.z03` ⇒ 用户说"显示没同步"）。
         */
        foreach (string name in new[]
                 {
                     Path.GetFileName(_ownFilePath),
                     Path.GetFileName(originalPath)
                 })
        {
            if (name.Length == 0)
            {
                continue;
            }

            try
            {
                string candidate = Path.Combine(restDirectoryPath, name);

                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch
            {
                // 路径形状不合法 ⇒ 试下一个名字。
            }
        }

        return string.Empty;
    }

    /// <summary>列表「文件名」那一格显示什么（默认 = 调用方给的兼容值，即 <see cref="ArchiveTask.FileName"/>）。</summary>
    internal string DisplayFileName(string originalPath, string currentPath, string restDirectoryPath, string fallbackFileName) =>
        ResolveDisplayPath(originalPath, currentPath, restDirectoryPath) is { Length: > 0 } path
            ? Path.GetFileName(path)
            : (_ownFilePath.Length > 0 ? Path.GetFileName(_ownFilePath) : fallbackFileName);

    /// <summary>列表「大小」那一格显示什么（默认 = 调用方给的兼容值，即 <see cref="ArchiveTask.SourceSizeText"/>）。</summary>
    internal string DisplaySizeText(string originalPath, string currentPath, string restDirectoryPath, string fallbackSizeText) =>
        ResolveDisplayPath(originalPath, currentPath, restDirectoryPath) is { Length: > 0 } path
            ? TaskDisplayFacts.DescribeSize(path)
            : (_ownFileSizeBytes > 0 ? TaskDisplayFacts.FormatSize(_ownFileSizeBytes) : fallbackSizeText);

    /// <summary>「文件名」那一格悬停提示 / 「完整路径」那一列（默认 = 调用方给的当前处理路径）。</summary>
    internal string DisplayPathToolTip(string originalPath, string currentPath, string restDirectoryPath) =>
        ResolveDisplayPath(originalPath, currentPath, restDirectoryPath) is { Length: > 0 } path
            ? path
            : (_ownFilePath.Length > 0 ? _ownFilePath : currentPath);

    /// <summary>
    /// 「当前后缀」那一列显示什么（⛔ 必须与 <see cref="DisplayFileName"/> 同步：
    /// 显示 <c>111.z02</c> 却把后缀写成 <c>.zip</c> 就是自相矛盾 —— 用户 2026-10-06 当场报的"后缀也不同步"）。
    /// </summary>
    internal string DisplayExtension(
        string originalPath,
        string currentPath,
        string restDirectoryPath,
        string fallbackFileName,
        string fallbackExtension)
    {
        string name = DisplayFileName(originalPath, currentPath, restDirectoryPath, fallbackFileName);

        if (name.Length == 0)
        {
            return fallbackExtension;
        }

        return Path.GetExtension(name) is { Length: > 0 } extension ? extension : "无";
    }

    /// <summary>
    /// **这一行仍旧显示"用户自己那个文件"**（真机 CCCC 2026-10-06，用户原话：
    /// 「48MB 在列表里面显示到了 111(4)，也就是列表里面第一个……这个你到目前还没改回来」
    /// 「也就是你没有真正的移动只是显示问题的情况，赶紧修」）。
    ///
    /// <para><b>现场</b>：批末补判把这一单的**起点**改写到"别的包解出来的入口包"上
    /// （<c>…\111(4)\111.z03</c> → <c>…\111\111\111.zip</c>，48.35 MB）—— <see cref="ArchiveTask.CurrentPath"/>
    /// 的 setter 顺手把名称 / 目录 / 体积整行刷新成那一份过程物 ⇒ 用户导入的那个文件在列表里"变成了 48MB 的东西"。</para>
    ///
    /// <para>⛔ **只改显示**：<see cref="ArchiveTask.CurrentPath"/>（真正拿去解压的起点）、<c>Outcome</c>、
    /// 借片账、落点……一个字节都不动；⛔ 起点与用户自己的文件**在同一层**（同一组里自己人的另一卷，
    /// <c>set.7z.002</c> → <c>set.7z.001</c>）时显示照旧；⛔ 认不出用户那个文件（哪条路径都不在盘上）⇒
    /// 什么都不做（显示照旧，⛔ 不猜）。</para>
    /// </summary>
    /// <param name="startPointPath">改写之后的起点（那一份过程物）；与它同名的那条路径不会被选中。</param>
    /// <param name="hintPath">
    /// 调用方替我们找到的"用户自己那一份现在在哪"（改名之后 <c>OriginalPath</c> 会过期，
    /// 而账上那几卷可能散在别的目录 ⇒ 只有调用方拿得到"同目录 + 同族"那个判据）。
    /// 它会被**优先**采用；⛔ 仍然要求它在最初导入的那个目录里。
    /// </param>
    /// <returns>
    /// <see cref="UserFileAdoption.Adopted"/> = 采纳成功（调用方发那五列通知）；
    /// <see cref="UserFileAdoption.OwnFileChanged"/> = 最新已知位置也变了（调用方另发身份通知）。
    /// </returns>
    internal UserFileAdoption AdoptUserFacing(
        string originalPath,
        IReadOnlyList<string> volumePaths,
        string currentPath,
        string? startPointPath,
        string? hintPath)
    {
        if (string.IsNullOrWhiteSpace(originalPath))
        {
            return default;
        }

        string originalDirectory = Path.GetDirectoryName(originalPath) ?? string.Empty;
        string startDirectory = string.IsNullOrWhiteSpace(startPointPath)
            ? string.Empty
            : Path.GetDirectoryName(startPointPath) ?? string.Empty;

        if (string.Equals(originalDirectory, startDirectory, StringComparison.OrdinalIgnoreCase))
        {
            // 起点就在自己旁边（自己人另一卷）⇒ 显示照旧（用户要看的是"程序真去解的那一份"）。
            return default;
        }

        foreach (string path in EnumerateOwnPaths(originalPath, volumePaths, currentPath).Prepend(hintPath ?? string.Empty))
        {
            if (string.IsNullOrWhiteSpace(path)
                || string.Equals(path, startPointPath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            /*
             * ⛔ **只认"和最初导入那一份同一个目录"的候选**：用户可以改名
             * （`111.z0删除2` → `111.z02`），但那一份东西一直在他放的那个目录里；
             * 账上那几卷可能散在**别的目录**（别的单的片）—— 拿它们顶替就成了"这一行显示别人"。
             */
            if (!string.Equals(
                    Path.GetDirectoryName(path) ?? string.Empty,
                    originalDirectory,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                if (!File.Exists(path))
                {
                    continue;
                }
            }
            catch
            {
                continue;
            }

            _userFacingFilePath = path;
            bool ownFileChanged = Remember(path, allowReplace: true);

            return new UserFileAdoption(true, ownFileChanged);
        }

        return default;
    }

    /// <summary>这一单"自己那个文件"可能在哪几条路径上（最初导入的那一份 + 账上那几卷 + 当前路径）。</summary>
    private static IEnumerable<string> EnumerateOwnPaths(string originalPath, IReadOnlyList<string> volumePaths, string currentPath)
    {
        yield return originalPath;

        foreach (string path in volumePaths)
        {
            yield return path;
        }

        yield return currentPath;
    }

    /// <summary>
    /// 采纳结果：<c>Adopted</c> = 这一行已切到用户的文件；<c>OwnFileChanged</c> = 最新已知位置也变了。
    /// </summary>
    internal readonly record struct UserFileAdoption(bool Adopted, bool OwnFileChanged);
}
