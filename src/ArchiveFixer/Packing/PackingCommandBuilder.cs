using ArchiveFixer.Engines;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace ArchiveFixer.Packing;

/// <summary>
/// 打包用到的**命令行构造**（<see cref="PackingService"/> 的协作类，职责：执行 / 参数模板）。
///
/// <para>⛔ 命令行参数模板**只允许出现在打包目录内**（docs/打包功能.md §5）—— 收在这里，
/// 是为了让"用什么参数"这件事只有一个出口：执行那一步、产物校验那一步、以及钉住真 7z 的用例
/// 读的都是同一份。</para>
///
/// <para>共同的几条（与解压侧踩过的坑同一个）：<c>-bsp1</c> 把进度打到 stdout（⛔ 不能加 <c>-bd</c>，
/// 那会把进度指示器整个压掉）；<c>-sccUTF-8</c> 输出用 UTF-8，中文名才认得出来；
/// <c>-y</c> 不问任何问题（无人值守）。</para>
/// </summary>
internal static class PackingCommandBuilder
{
    /// <summary>
    /// 第一步的命令行：
    /// <c>a -t7z -mx=5 -mhe=on -bsp1 -sccUTF-8 -y -p&lt;密码&gt; -v&lt;n&gt;m "&lt;落点&gt;\&lt;源名&gt;.7z" "&lt;A&gt;\*"</c>。
    ///
    /// <para><c>-mhe=on</c>：连文件名一起加密；<c>-v&lt;n&gt;m</c>：按 MiB 分卷。
    /// ⚠ 2026-09-26 追加改口径：分卷**直接落在落点目录里**（<see cref="PackingPlan.VolumeBasePath"/>），
    /// 不再先落进一个以源名命名的中间文件夹 —— 用户原话："7z 分卷文件外面好像不用再套一件文件夹了，
    /// 有点太多余了，这样也不用去考虑文件名重复的问题了"。</para>
    /// </summary>
    internal static List<string> BuildVolumeArguments(PackingPlan plan, string password)
    {
        string volumeMegabytes = (plan.VolumeSizeBytes / (1024L * 1024L))
            .ToString(CultureInfo.InvariantCulture);

        return new List<string>
        {
            "a",
            "-t7z",
            "-mx=5",
            "-mhe=on",
            "-bsp1",
            "-sccUTF-8",
            "-y",
            "-p" + password,
            "-v" + volumeMegabytes + "m",
            plan.VolumeBasePath,
            Path.Combine(plan.SourceFolder, "*")
        };
    }

    /// <summary>
    /// 第二步（外层容器 = 7z）的命令行：
    /// <c>a -t7z -mhe=on -mx=5 -bsp1 -sccUTF-8 -y -p&lt;密码&gt; "&lt;落点&gt;\&lt;源名&gt;.7z" 分卷1 分卷2…</c>。
    ///
    /// <para>与第一步同一套安全传参 / 进度 / 取消做法，差别只有两条：不分卷（外层就是一个文件），以及
    /// <c>-mhe=on</c> 让**文件名也加密**（与 rar 那边的 <c>-hp</c> 对齐）。</para>
    ///
    /// <para>⚠ 追加改口径：参数里给的是**逐个点名的分卷文件名**（进程在**落点目录**里执行），
    /// 于是归档里就是这些分卷（顶层、没有文件夹层）。
    /// ⛔ 不能写 <c>&lt;落点&gt;\*</c>：那会把源文件夹本身也扫进去（"越打越多"）。</para>
    /// </summary>
    internal static List<string> BuildSevenZipOuterArguments(
        PackingPlan plan,
        string outerPassword,
        IReadOnlyList<string> volumePaths)
    {
        var args = new List<string>
        {
            "a",
            "-t7z",
            "-mx=5",
            "-mhe=on",
            "-bsp1",
            "-sccUTF-8",
            "-y",
            "-p" + outerPassword,
            plan.SevenZipOuterPath
        };

        foreach (string volume in volumePaths)
        {
            args.Add(Path.GetFileName(volume));
        }

        return args;
    }

    /// <summary>
    /// 核对 7z 外层容器时的列条目命令：<c>l -slt -sccUTF-8 -y -p&lt;密码&gt; "&lt;B&gt;.7z"</c>。
    ///
    /// <para>为什么用 <c>-slt</c>（技术信息）而不是默认的表格排版：默认输出是给人看的表格，
    /// 列宽随内容变、还带汇总行，把它钉进代码等于把"某一版 7-Zip 的排版"当成契约；
    /// <c>-slt</c> 是一行一个 <c>键 = 值</c> 的机器格式（见 <see cref="PackingVerifier.ParseSevenZipSltList"/>）。
    /// 代价是它一开始会多出一段描述**归档自己**的块，那个由解析函数按分隔线切掉。</para>
    /// </summary>
    internal static List<string> BuildSevenZipListArguments(PackingPlan plan, string outerPassword)
    {
        return new List<string>
        {
            "l",
            "-slt",
            "-sccUTF-8",
            "-y",
            "-p" + outerPassword,
            plan.SevenZipOuterPath
        };
    }

    /// <summary>
    /// 第二步的命令行（外层 rar）：
    /// <c>a -hp&lt;密码&gt; -ep1 [-ibck] -y "&lt;落点&gt;\&lt;源名&gt;.rar" 分卷1 分卷2…</c>。
    ///
    /// <para><c>-hp</c>：连文件名一起加密；<c>-ep1</c>：不保存路径 —— 给文件时就是**只有文件名**，
    /// 于是归档里是顶层的那几个分卷（与 7z 外层形态一一对应）。</para>
    ///
    /// <para>⚠ 追加改口径：不再传那个中间文件夹，改成**逐个点名分卷文件**（进程在落点目录里跑）。
    /// 退到 <c>WinRAR.exe</c> 时补 <c>-ibck</c>（后台 / 托盘运行）——
    /// 那个是 GUI 程序，不加它会在用户桌面弹出一个进度窗口并抢焦点。</para>
    /// </summary>
    internal static List<string> BuildRarArguments(
        ToolLocator tools,
        PackingPlan plan,
        string outerPassword,
        IReadOnlyList<PackingVolume> volumes)
    {
        var args = new List<string> { "a", "-hp" + outerPassword, "-ep1" };

        if (tools.IsUsingWinRarGuiForRar)
        {
            args.Add("-ibck");
        }

        args.Add("-y");
        args.Add(plan.RarPath);

        foreach (string volume in VolumePaths(volumes))
        {
            args.Add(Path.GetFileName(volume));
        }

        return args;
    }

    /// <summary>分卷的完整路径（外层那一步只给文件名，进程在落点目录里跑）。</summary>
    internal static List<string> VolumePaths(IReadOnlyList<PackingVolume> volumes)
    {
        var paths = new List<string>(volumes.Count);

        foreach (PackingVolume volume in volumes)
        {
            if (!string.IsNullOrWhiteSpace(volume.Path))
            {
                paths.Add(volume.Path);
            }
        }

        return paths;
    }

    /// <summary>
    /// 核对产物时的列条目命令：<c>lb -p&lt;密码&gt; &lt;rar&gt;</c>。
    /// 用 <c>lb</c>（bare list，一行一个名字）而不是 <c>l</c>：后者的表头 / 分隔线 / 列宽
    /// 属于"某一版 RAR 的排版"，钉进代码里迟早会随版本变（见 <see cref="PackingVerifier"/>）。
    /// </summary>
    internal static List<string> BuildRarListArguments(
        ToolLocator tools,
        PackingPlan plan,
        string outerPassword)
    {
        var args = new List<string> { "lb", "-p" + outerPassword };

        if (tools.IsUsingWinRarGuiForRar)
        {
            args.Add("-ibck");
        }

        args.Add(plan.RarPath);

        return args;
    }
}
