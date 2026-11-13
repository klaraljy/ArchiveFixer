namespace ArchiveFixer.Detection
{
    /// <summary>
    /// **内容物事实**（与"是不是分卷"分开记）：这一份的字节**按媒体容器自己的结构**正好是一段完整媒体、
    /// 且里面没有可自洽的尾部归档。
    ///
    /// <para>⛔ 它只是**否定证据**（"这一份是普通媒体，不是分卷的一片"），不改变
    /// <see cref="FileNatureCard.Membership"/> / <see cref="FileNatureCard.ParticipatesInVolumeHandling"/>：
    /// 那两格回答的是"内容里有没有分卷身份"，媒体结构回答的是"这段字节是不是内容物"。</para>
    /// </summary>
    public enum FileContentKind
    {
        /// <summary>判不出（⛔ 不猜；带尾巴的媒体、裸切续卷伪装成 .png 都落这一档）。</summary>
        Unknown = 0,

        /// <summary>普通媒体内容物：媒体结构正好占满整个文件，且没有尾部归档。</summary>
        NormalMedia = 1
    }
}
