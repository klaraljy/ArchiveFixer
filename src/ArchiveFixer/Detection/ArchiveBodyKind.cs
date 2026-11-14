namespace ArchiveFixer.Detection;

/// <summary>
/// **内容自证：这一份是"完整的单卷归档"还是"某分卷组的一片"**（用户 2026-10-13 口径：
/// 「『名字被伪装到读不出卷号 ⇒ 组凑不齐』这个是**底层识别**的问题，赶快升级修改专属算法（单一和分卷）」）。
///
/// <para>四档，⛔ 判不出就是判不出（"按名字"那一档仍由消费方决定，⛔ 本枚举不许替它猜）：</para>
/// <list type="number">
/// <item><description><b>SingleComplete</b>：内容自证"这就是完整的一份单卷归档"——
/// 7z 由**起始头明文自述的整包字节数 ≤ 自己**回答（<c>32 + NextHeaderOffset + NextHeaderSize</c>）。</description></item>
/// <item><description><b>VolumeFirstPiece</b>：内容自证"我是**某分卷组的第一片**"——
/// 7z 同一句话反过来：自述整包字节数 **&gt;** 自己（并且由此能算出**共几片**：按卷大小向上取整）。</description></item>
/// <item><description><b>VolumePiece</b>：内容自证"我是分卷组的一片、而且不是第一片"——
/// RAR5 主头卷号 &gt; 1 / 跨盘 ZIP 的 EOCD 盘号 &gt; 1 那一类（<see cref="VolumeNumberFromContent"/> 的回答）。</description></item>
/// <item><description><b>Unknown</b>：判不出（7z 的**中间片**、跨盘 ZIP 的中间片、认不出的字节都落在这一档）
/// —— 这一档**照旧按名字**办，⛔ 不许当成"不是分卷"。</description></item>
/// </list>
/// </summary>
public enum ArchiveBodyKind
{
    /// <summary>判不出（⛔ 不猜）。</summary>
    Unknown = 0,

    /// <summary>内容自证：完整的单卷归档。</summary>
    SingleComplete = 1,

    /// <summary>内容自证：某分卷组的第一片（带整包字节数与片数）。</summary>
    VolumeFirstPiece = 2,

    /// <summary>内容自证：分卷组的一片、不是第一片（带卷号）。</summary>
    VolumePiece = 3
}
