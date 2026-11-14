
namespace ArchiveFixer.Extraction;

/// <summary>待清理对象的身份。身份在产生时确定，⛔ 不在删除现场靠后缀/名字猜。</summary>
public enum CleanupItemKind
{
    UserSource,
    LayerInput,
    RestArtifact,
    Workspace
}

