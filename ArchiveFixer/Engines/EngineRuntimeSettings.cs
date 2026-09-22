using ArchiveFixer.Models;
using System;
using System.Collections.Generic;

namespace ArchiveFixer.Engines
{
    /// <summary>
    /// 引擎**运行时**要用的设置（优先级、保留损坏文件）以及"把设置推给引擎层"的**唯一入口**。
    ///
    /// <para>
    /// 为什么需要这么一个东西（而不是让 <see cref="EngineSelector"/> 直接读 <c>SettingsService</c>）：
    /// 分层铁律要求 <c>Engines/</c> 不依赖 WPF，而设置的读写一直挂在
    /// <c>Services/SettingsService</c> 与 ViewModel 上。历史做法是"谁有设置谁往
    /// <c>ToolLocator.Default</c> 里推"（<c>MainViewModel.ApplyEngineSettings</c> 推 7z 路径）。
    /// 本类把这条路收敛成**一次调用**：<see cref="Apply"/> 同时把
    /// ①两条外部工具路径推给 <see cref="ToolLocator.Default"/>、
    /// ②引擎优先级、③保留损坏文件 落到引擎层。
    /// </para>
    ///
    /// <para>
    /// ⚠ 默认值必须是**正确可用**的（不接线也能跑对）：
    /// 优先级默认 <c>WinRar → SevenZip</c>，保留损坏文件默认关。
    /// 没人调用 <see cref="Apply"/> 时，引擎层的行为与"用户没改过设置"完全一致 ——
    /// 这样即使某条路径忘了接线，也不会出现"引擎选择变成随机的"这种难查的故障。
    /// </para>
    /// </summary>
    public static class EngineRuntimeSettings
    {
        private static readonly object Gate = new();

        private static IReadOnlyList<string> _enginePriority = EngineIds.DefaultPriority;
        private static bool _keepBrokenFiles;

        /// <summary>引擎优先级（规范写法、已补全所有已知引擎）。读取时拿到的是稳定快照，不会被调用方改坏。</summary>
        public static IReadOnlyList<string> EnginePriority
        {
            get
            {
                lock (Gate)
                {
                    return _enginePriority;
                }
            }
        }

        /// <summary>
        /// 保留受损文件（WinRAR 的 <c>-kb</c>，对应设置项 <see cref="AppSettings.KeepBrokenFiles"/>，**默认关**）。
        ///
        /// <para>
        /// ⚠ 边界（AGENTS.md §6 不变量 6）：开着它**只是不解压器把校验失败的半成品删掉**，
        /// 任务状态仍然必须是"失败 / 部分完成" —— 校验和不符就是校验和不符，
        /// 绝不因为"文件留下了"就显示成功。状态由退出码与错误分类决定，与本开关无关。
        /// </para>
        /// </summary>
        public static bool KeepBrokenFiles
        {
            get
            {
                lock (Gate)
                {
                    return _keepBrokenFiles;
                }
            }
        }

        /// <summary>
        /// 把设置推给引擎层：外部工具路径（7z / UnRAR）+ 引擎优先级 + 保留损坏文件。
        /// <paramref name="settings"/> 为 null 时按默认值处理（等价于"用户没配置过"）。
        /// </summary>
        public static void Apply(AppSettings? settings)
        {
            ToolLocator.Default.CustomSevenZipExePath = settings?.CustomSevenZipExePath ?? string.Empty;
            ToolLocator.Default.CustomUnRarExePath = settings?.CustomUnRarExePath ?? string.Empty;

            SetPriority(settings?.EnginePriority);
            SetKeepBrokenFiles(settings?.KeepBrokenFiles ?? false);
        }

        /// <summary>设置引擎优先级（空/非法项会被 <see cref="EngineIds.Normalize"/> 收拾干净）。</summary>
        public static void SetPriority(IEnumerable<string>? priority)
        {
            List<string> normalized = EngineIds.Normalize(priority);

            lock (Gate)
            {
                _enginePriority = normalized;
            }
        }

        /// <summary>设置"保留受损文件"。</summary>
        public static void SetKeepBrokenFiles(bool value)
        {
            lock (Gate)
            {
                _keepBrokenFiles = value;
            }
        }

        /// <summary>
        /// 恢复默认（测试用：每个用例开始前调一次，免得上一个用例的设置漏到下一个）。
        /// 只重置本类持有的两项，**不动** <see cref="ToolLocator.Default"/> 的路径
        /// （测试里那两条路径由用例自己按需设置与还原）。
        /// </summary>
        public static void ResetToDefaults()
        {
            lock (Gate)
            {
                _enginePriority = EngineIds.DefaultPriority;
                _keepBrokenFiles = false;
            }
        }
    }
}
