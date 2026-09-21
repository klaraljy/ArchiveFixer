using System;
using System.Collections.Generic;
using System.Linq;

namespace ArchiveFixer.Engines
{
    /// <summary>
    /// 引擎注册表。
    ///
    /// 现在只注册一个引擎，但**注册表本身必须存在**：
    /// 以后加 libarchive / unar / 专项工具时，改的是注册表，不是 GUI、不是调度器、不是递归核心
    /// （设计.md 阶段三的验收标准）。
    /// </summary>
    public sealed class EngineRegistry
    {
        private readonly List<IArchiveEngine> _engines = new();

        /// <summary>建一个只含内置 7-Zip 引擎的注册表。</summary>
        public static EngineRegistry CreateDefault()
        {
            var registry = new EngineRegistry();
            registry.Register(new SevenZip.SevenZipEngine());
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

        public IArchiveEngine? Default => _engines.FirstOrDefault(e => e.IsAvailable) ?? _engines.FirstOrDefault();
    }
}
