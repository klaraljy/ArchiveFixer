using System;
using System.Collections.Generic;
using System.Linq;

namespace ArchiveFixer.Engines
{
    /// <summary>
    /// 引擎注册表。
    ///
    /// 加第二个引擎（RARLAB UnRAR）时改的是**这里**，不是 GUI、不是调度器、不是递归核心 ——
    /// 这正是设计.md 阶段三那条验收标准（"GUI 不直接调用 7-Zip / 递归核心只依赖 IArchiveEngine"）
    /// 要在只有一个引擎的时候就成立的原因。
    /// </summary>
    public sealed class EngineRegistry
    {
        private readonly List<IArchiveEngine> _engines = new();

        /// <summary>
        /// 建一个含全部内置引擎的注册表（用户在设置里改的是**优先级**，不是"注册哪几个"）。
        ///
        /// <paramref name="tools"/> 为空时用 <see cref="ToolLocator.Default"/>；
        /// 测试可以传一个自建实例来模拟"这台机器上没装 UnRAR"。
        /// </summary>
        public static EngineRegistry CreateDefault(ToolLocator? tools = null)
        {
            ToolLocator locator = tools ?? ToolLocator.Default;

            var registry = new EngineRegistry();

            // 注册顺序不影响选择结果（选择按"能力筛 + 优先级排序"），但影响"优先级列表里没提到的引擎"的兜底次序。
            registry.Register(new WinRar.UnRarEngine(new WinRar.UnRarProcessRunner(locator)));
            registry.Register(new SevenZip.SevenZipEngine(new SevenZip.SevenZipProcessRunner(locator)));

            return registry;
        }

        public IReadOnlyList<IArchiveEngine> Engines => _engines;

        public void Register(IArchiveEngine engine)
        {
            if (engine == null)
            {
                return;
            }

            // 同一个 Id 重复注册时覆盖，避免测试或热重载时堆出一串重复引擎。
            _engines.RemoveAll(e => string.Equals(e.Id, engine.Id, StringComparison.OrdinalIgnoreCase));
            _engines.Add(engine);
        }

        public IArchiveEngine? FindById(string? id)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return null;
            }

            return _engines.FirstOrDefault(
                e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// 按优先级顺序排列的引擎（不可用的**不排除** —— 界面要把"排第一但没装"如实显示出来）。
        ///
        /// 优先级默认取自 <see cref="EngineRuntimeSettings.EnginePriority"/>（<c>WinRar → SevenZip</c>）；
        /// 列表里没提到的引擎排在最后，保持注册顺序（稳定、可复现）。
        ///
        /// <paramref name="priority"/> 用于**预览**：②「解压方式」页里用户改了顺序、还没点「保存设置」时，
        /// 界面要显示"改完之后会怎样"，而全局的运行时设置此刻**不能**被改
        /// （否则没点保存也会生效 —— "改了没保存却生效了"的经典缺陷）。
        /// </summary>
        public IReadOnlyList<IArchiveEngine> EnginesInPriorityOrder(IReadOnlyList<string>? priority = null)
        {
            IReadOnlyList<string> effective = priority ?? EngineRuntimeSettings.EnginePriority;

            return _engines
                .Select((engine, index) => new { engine, index })
                .OrderBy(x => EngineIds.PriorityIndexOf(effective, x.engine.Id))
                .ThenBy(x => x.index)
                .Select(x => x.engine)
                .ToList();
        }

        /// <summary>
        /// "通用引擎"：**能处理格式最多的那个可用引擎**。
        ///
        /// 为什么不取"优先级第一"：优先级是给**具体格式**分派用的（RAR 交给 UnRAR、zip 交给 7-Zip）。
        /// 报告里"引擎：…"那一行、以及不知道格式时的默认身份，需要的是"谁是这个程序的通用引擎" ——
        /// 拿 RAR 专用引擎去标一个 zip 任务会把溯源信息写错（不变量 14 要求能追到**实际**用的引擎）。
        /// 现有的 7-Zip 注册表只有一个引擎时，这里返回的仍然是它，行为不变。
        /// </summary>
        public IArchiveEngine? Default =>
            _engines
                .Where(e => e.IsAvailable)
                .OrderByDescending(e => e.Capabilities.Formats.Count)
                .ThenBy(e => EngineIds.PriorityIndexOf(EngineRuntimeSettings.EnginePriority, e.Id))
                .FirstOrDefault()
            ?? _engines.FirstOrDefault();
    }
}
