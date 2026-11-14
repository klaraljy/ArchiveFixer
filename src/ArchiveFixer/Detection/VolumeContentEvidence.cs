namespace ArchiveFixer.Detection;

/// <summary>
/// **内容证据模型**：一次内容读数（<see cref="VolumeNumberReading"/>）怎么落成"这一份是不是分卷组的一员"
/// 的三档结论，以及"没认出来"那一档读数怎么造。
///
/// <para><b>为什么单独成层</b>：三档结论是**唯一出口**（主类 <c>VolumeNumberFromContent.ClassifyMembership</c>
/// 只做转发），消费方只认这三档，⛔ 不许自己去看 <see cref="VolumeNumberReading.IsVolumeMember"/> /
/// <see cref="VolumeNumberReading.Fail"/> 再判一次。判据只有两条内容事实：</para>
/// <list type="bullet">
/// <item><description>内容自述"我是分卷组的一员" ⇒ <c>VolumeMember</c>；</description></item>
/// <item><description>头读得出来、而且它写着"单卷"（<see cref="VolumeNumberFail.NotVolumeMember"/> /
/// <see cref="VolumeNumberFail.ZipSingleDisk"/>）⇒ <c>ProvablyNotVolumeMember</c>；</description></item>
/// <item><description>其余（魔数认不出 / 头截断 / 头加密 / RAR 老式编号族）⇒ <c>Unknown</c>
/// —— 这一档**照旧按名字判**，⛔ 绝不许当成"不是分卷成员"（跨盘 ZIP 的中间片就在这一档，它照样是分卷的片）。</description></item>
/// </list>
///
/// <para>生命周期：纯静态、无状态；只读调用方给的事实，不碰文件系统。</para>
/// </summary>
internal static class VolumeContentEvidence
{
    /// <summary>三档结论的唯一实现（判据见类注释）。</summary>
    public static VolumeNumberFromContent.VolumeMembershipVerdict Classify(VolumeNumberReading reading)
    {
        if (reading.IsVolumeMember)
        {
            return VolumeNumberFromContent.VolumeMembershipVerdict.VolumeMember;
        }

        return reading.Fail is VolumeNumberFail.NotVolumeMember or VolumeNumberFail.ZipSingleDisk
            ? VolumeNumberFromContent.VolumeMembershipVerdict.ProvablyNotVolumeMember
            : VolumeNumberFromContent.VolumeMembershipVerdict.Unknown;
    }

    /// <summary>
    /// 造"没认出来"那一档的读数（<c>IsVolumeMember = false</c>，卷号/总片数等一律不填）。
    ///
    /// <para><paramref name="mainHeaderRead"/> 用来区分"主头读成功且证明不是分卷成员"与
    /// "结构就不对/不是归档开头"—— <c>Fail</c> 那一位做不到这件事（两种都落
    /// <see cref="VolumeNumberFail.NotVolumeMember"/>），而"单卷 ⇒ SingleComplete"只允许认前者
    /// （2026-10-22，`24242424` 收口）。</para>
    /// </summary>
    public static VolumeNumberReading NotAVolume(
        string path,
        VolumeContentFormat format,
        VolumeNumberFail fail,
        bool mainHeaderRead = false) => new()
        {
            Path = path,
            Format = format,
            IsVolumeMember = false,
            MainHeaderRead = mainHeaderRead,
            Fail = fail
        };
}
