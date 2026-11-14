using ArchiveFixer.Engines;

namespace ArchiveFixer.Models;

/// <summary>
/// 一次 L3 裁决：这一单拿**哪一层**的清单当预期，或者**为什么取不到**。
///
/// <para>它是 <c>ExtractionCoordinator</c> 与各删除闸门之间唯一的"预期清单"接口 ——
/// 谁都不许自己再判一遍"展开了几层、该拿哪一层的清单"。</para>
/// </summary>
public sealed class ManifestExpectation
{
    /// <summary>不是任何一层（取值与 ChainManifestResolver.NoLayer 一致）；只用于 LayerDepth。</summary>
    internal const int NoLayer = -1;

    /// <summary>兜底值 = **取不到清单**（⛔ 漏赋值只会变成"不核对、不删源"，不会变成"敢删"）。</summary>
    public static ManifestExpectation None { get; } = new()
    {
        Source = ManifestExpectationSource.None,
        UnavailableReason = "没有列过归档清单，没法核对最终产物"
    };

    public ManifestExpectationSource Source { get; init; } = ManifestExpectationSource.None;

    /// <summary>能直接用的清单（<see cref="ManifestExpectationSource.LeafLayer"/> 时是本层清单折出来的那一份）。</summary>
    public ArchiveListResult? Expected { get; init; }

    /// <summary>清单来自哪一层（-1 = 不是某一层的清单）。</summary>
    public int LayerDepth { get; init; } = NoLayer;

    /// <summary>那一层的说法（`第 1 层：Sociology.7z`）。</summary>
    public string LayerLabel { get; init; } = string.Empty;

    /// <summary>
    /// 清单对应的**产物根绝对路径**（叶子层才可能非空）。校验时由调用方相对最终产物根折算前缀；
    /// 外层清单（第 0 层）与现问引擎的清单为空。
    /// </summary>
    public string ExpectedRootPath { get; init; } = string.Empty;

    /// <summary>取不到时的原因（**点名哪一层 + 为什么** + 用户能做什么）；取得到时为空串。</summary>
    public string UnavailableReason { get; init; } = string.Empty;

    /// <summary>有没有可用的清单。</summary>
    public bool HasManifest => Source != ManifestExpectationSource.None && Expected is { Success: true };

    /// <summary>排障用的一行（机器可判的字段，⛔ 不是给人看的文案）。</summary>
    public string Evidence => Source == ManifestExpectationSource.None
        ? $"ManifestExpectation=None;Reason={UnavailableReason}"
        : $"ManifestExpectation={Source};Layer={LayerDepth}";
}
