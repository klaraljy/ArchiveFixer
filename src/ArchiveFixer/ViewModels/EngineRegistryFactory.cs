using ArchiveFixer.Engines;
using System;
using ArchiveFixer.Models;

namespace ArchiveFixer.ViewModels
{
    /// <summary>注入引擎到注册表的唯一构造点。</summary>
    internal static class EngineRegistryFactory
    {
        internal static EngineRegistry Build(IArchiveEngine? injected)
        {
            if (injected == null)
            {
                return EngineRegistry.CreateDefault();
            }

            bool isBuiltIn = string.Equals(injected.Id, EngineIds.SevenZip, StringComparison.OrdinalIgnoreCase)
                             || string.Equals(injected.Id, EngineIds.WinRar, StringComparison.OrdinalIgnoreCase);

            if (!isBuiltIn)
            {
                var registry = new EngineRegistry();
                registry.Register(injected);
                return registry;
            }

            EngineRegistry defaultRegistry = EngineRegistry.CreateDefault();
            defaultRegistry.Register(injected);

            return defaultRegistry;
        }
    }
}
