using ArchiveFixer.BindingModels;
using ArchiveFixer.Engines;
using ArchiveFixer.Models;

namespace ArchiveFixer.ViewModels;

/// <summary>任务实际使用引擎的只读解析。</summary>
internal static class EngineIdentityResolver
{
    internal static EngineIdentity? Resolve(IArchiveEngine engine, ArchiveTask? task)
    {
        if (task == null || engine is not EngineRouter router)
        {
            return null;
        }

        return router.ResolveIdentityFor(task.CurrentPath);
    }
}
