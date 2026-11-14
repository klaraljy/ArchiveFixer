using ArchiveFixer.Engines;
using ArchiveFixer.Storage;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ArchiveFixer.Packing;

/// <summary>
/// 打包（用户 2026-09-22 需求第 10 条）：文件夹 A 的内容 → 7z 加密分卷 → 放进文件夹 B →
/// B 整个压成一个带密码的**外层容器** → 结果 = 压缩包 B。
///
/// <para><b>外层容器三选一</b>（用户 2026-09-23 决定，取代早先的 <c>bool SkipOuterRar</c>）：
/// <c>rar</c>（默认）/ <c>7z</c> / <c>不做外层</c>。为什么必须有三条：
/// <c>Rar.exe</c> / <c>WinRAR.exe</c> 是**共享软件**，RARLAB 的 EULA 禁止随任何软件包分发
/// （AGENTS.md §3.1），所以"本机没装 WinRAR"是常态 —— 那时除了"去装一个"和"什么都不做"，
/// 还必须有第三条不需要额外安装的路（7-Zip 是 LGPL、本来就随程序分发）。</para>
///
/// <para><b>两步 + 一次校验</b>（命令行的参数模板**只允许出现在本目录内**，
/// docs/打包功能.md §5）：</para>
/// <code>
/// ① 7z.exe  a -t7z -mx=5 -mhe=on -bsp1 -sccUTF-8 -y -p&lt;密码&gt; -v512m "&lt;B&gt;\&lt;A名&gt;.7z" "&lt;A&gt;\*"
/// ②a Rar.exe a -hp&lt;密码&gt; -r -ep1 "&lt;B&gt;.rar" "&lt;B&gt;"          ← 外层容器 = rar
/// ②b 7z.exe  a -t7z -mhe=on -mx=5 -bsp1 -sccUTF-8 -y -p&lt;密码&gt; "&lt;B&gt;.7z" "&lt;B&gt;"   ← 外层容器 = 7z
/// ③a Rar.exe lb -p&lt;密码&gt; "&lt;B&gt;.rar"                            ← 按容器校验
/// ③b 7z.exe  l -slt -p&lt;密码&gt; "&lt;B&gt;.7z"
/// </code>
///
/// <para><b>几条不肯让步的规矩</b>：</para>
/// <list type="bullet">
/// <item><description>7z **不能**建 rar（RAR 算法是专有的）。选了 rar 而本机没有 <c>Rar.exe</c> 时
/// **明确报错**并给出**三条**出路，⛔ 绝不静默换成别的容器、也绝不把 7z 的产物改名成 <c>.rar</c> 糊过去；</description></item>
/// <item><description><c>Rar.exe</c> 是共享软件：**只检测、只调用，绝不打包、绝不复制**（AGENTS.md §3.1）；</description></item>
/// <item><description>取消只杀**自己启动的那个 PID 及其子进程**（在 <see cref="PackProcessRunner"/> 里）；</description></item>
/// <item><description>失败 / 取消**不留半个外层容器**；分卷保留（重试前要用户自己清空 B —— 见下）；</description></item>
/// <item><description>完成之后**按容器**核对产物，对不上**不显示成功**（不变量 6）。</description></item>
/// </list>
///
/// <para><b>为什么 B 必须不存在或为空</b>：B 会被整个装进外层容器，混进旧文件就会让"结果 = 压缩包 B"
/// 这句话不成立；而"把上一次失败留下的半成品静默删掉再重来"又是另一种不打招呼的破坏。
/// 所以规则是**明说 + 让用户自己决定**：B 非空就拒绝并告诉他清空或换一个。</para>
///
/// <para>⚠ 本类**只留公开 API（薄转发）**：流程与判据按职责拆在几个协作类里，改动时先看对应那一类 ——
/// <see cref="PackingPreparation"/>（计划 / 落点准备）、<see cref="PackingCommandBuilder"/>（命令行模板）、
/// <see cref="PackingEngineSteps"/>（起进程）、<see cref="PackingProductCheck"/>（分卷与产物校验）、
/// <see cref="PackingIncompleteOuterCleanup"/>（没做完的外层容器）、<see cref="PackingRunLog"/>（日志与进度）、
/// <see cref="PackingPipeline"/>（把它们串成一次打包）。⛔ 判据只有一处实现，这里不复制第二份。</para>
/// </summary>
public sealed class PackingService
{
    private readonly ToolLocator _tools;
    private readonly PackingPipeline _pipeline;

    public PackingService()
        : this(ToolLocator.Default, null, null)
    {
    }

    /// <param name="tools">外部工具路径的唯一来源；null = <see cref="ToolLocator.Default"/>。</param>
    /// <param name="runner">起进程的那一层；null = 真的起进程（测试注入假的，用来验判据）。</param>
    /// <param name="availableSpaceProbe">
    /// 取目标盘可用空间；null = <see cref="SpaceChecker.GetAvailableFreeSpace"/>。
    /// 注入点是为了让"空间不够就不开始"这条判据能被确定性地测（真机上没法把盘填满）。
    /// </param>
    public PackingService(
        ToolLocator? tools,
        IPackProcessRunner? runner = null,
        Func<string, long?>? availableSpaceProbe = null)
    {
        _tools = tools ?? ToolLocator.Default;
        _pipeline = new PackingPipeline(
            _tools,
            runner ?? new PackProcessRunner(_tools),
            availableSpaceProbe ?? SpaceChecker.GetAvailableFreeSpace);
    }

    public ToolLocator Tools => _tools;

    /// <summary>
    /// 跑一次打包。**不抛异常**：失败与取消都落成 <see cref="PackingResult"/>。
    /// </summary>
    /// <param name="request">请求（含两个密码；只存内存）。</param>
    /// <param name="log">每一条步骤日志的出口（可空）；内容与 <see cref="PackingResult.LogLines"/> 一致。</param>
    /// <param name="progress">进度接收端（可空）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public Task<PackingResult> PackAsync(
        PackingRequest request,
        Action<string>? log = null,
        IProgress<PackingProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return _pipeline.RunAsync(request, log, progress, cancellationToken);
    }

    /// <summary>
    /// 第一步的命令行（**只允许在本目录内拼**）。
    /// <para>这条转发只为一件事：让用例能用**同一份**参数驱动真 7-Zip（跑通即证明参数没错）。
    /// 唯一实现在 <see cref="PackingCommandBuilder.BuildVolumeArguments"/>，这里不复制模板。</para>
    /// </summary>
    internal static List<string> BuildVolumeArguments(PackingPlan plan, string password)
    {
        return PackingCommandBuilder.BuildVolumeArguments(plan, password);
    }
}
