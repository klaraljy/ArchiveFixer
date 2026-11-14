namespace ArchiveFixer.Detection;

/// <summary>
/// 组名族（与 <see cref="VolumeGroupDetector"/> 内部那一套一一对应；此处只用来判"哪个位子的规范名不带卷号"）。
///
/// <para><b>职责</b>：命名族是"卷序怎么算 / 缺卷名怎么补 / 哪个位子的规范名不带卷号"的唯一枚举；
/// 族规则本身只有 <see cref="VolumeGroupNaming"/> 一处实现。</para>
///
/// <para><b>失败模型</b>：没有"判不出"这一档 —— 判不出族时一律落 <see cref="Numeric"/>
/// （见 <see cref="VolumeGroupNaming.ResolveFamily"/>），由下游证据（体积/位置/试开）回答，不在这里猜。</para>
///
/// <para>生命周期：纯值类型，无状态；只在一次判定的内存里传递。</para>
/// </summary>
internal enum VolumeNameFamily
{
    /// <summary>末尾纯数字段（<c>volume.7z.001</c> / <c>archive.001</c>）：数字就是卷序。</summary>
    Numeric = 0,

    /// <summary>zip 分卷（<c>x.zip</c> + <c>x.z01</c> …）：本体是第 1 卷，而且它的名字**不带卷号**。</summary>
    ZipSpanned = 1,

    /// <summary>rar 老式分卷（<c>x.rar</c> + <c>x.r00</c> …）：同上。</summary>
    RarOld = 2,

    /// <summary>rar 新式分卷（<c>x.partN.rar</c>）：N 就是卷序。</summary>
    PartNumbered = 3
}
