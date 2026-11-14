namespace ArchiveFixer.Detection;

/// <summary>
/// **一个 coder 的属性读取**（7z 头里 coder 链的一节）：flags → ID → 可选入/出流个数 → 可选属性。
///
/// <para><b>加密属性就落在这里</b>：ID 命中 AES（判据在 <see cref="SevenZipAesCoder"/>）就把
/// <c>hasAes</c> 置真 —— 这里只负责"读到并标记"，不负责"这算哪一档结论"。
/// 一个文件夹可以有多条 coder，命中任意一条即足够。</para>
///
/// <para>⛔ 读不动 / 越界即返回 false（调用方按"读不出 coder 链"处理，绝不能当成"没加密"）。</para>
/// </summary>
internal static class SevenZipCoderReader
{
    /// <summary>coder flags：低 4 位 = ID 的字节数。</summary>
    private const byte CoderIdSizeMask = 0x0F;

    /// <summary>coder flags：复杂 coder（后面还有"入流个数 + 出流个数"两个数字）。</summary>
    private const byte CoderComplex = 0x10;

    /// <summary>coder flags：带属性（后面还有"属性长度 + 属性字节"）。</summary>
    private const byte CoderHasProperties = 0x20;

    /// <summary>读一个 coder：flags → ID → 可选入/出流个数 → 可选属性。</summary>
    internal static bool TryReadCoder(SevenZipHeaderCursor cursor, ref bool hasAes, out string failure)
    {
        failure = string.Empty;

        if (!cursor.TryReadByte(out byte flags))
        {
            failure = "coder 的 flags 读不出来";

            return false;
        }

        int idSize = flags & CoderIdSizeMask;

        if (idSize == 0)
        {
            failure = "coder 的 ID 长度是 0（布局不符）";

            return false;
        }

        if (!cursor.TryReadBytes(idSize, out byte[] methodId))
        {
            failure = "coder 的 ID 读不出来";

            return false;
        }

        if (SevenZipAesCoder.IsAesMethodId(methodId))
        {
            hasAes = true;
        }

        if ((flags & CoderComplex) != 0
            && (!cursor.TryReadNumber(out _) || !cursor.TryReadNumber(out _)))
        {
            failure = "复杂 coder 的入 / 出流个数读不出来";

            return false;
        }

        if ((flags & CoderHasProperties) != 0)
        {
            if (!cursor.TryReadNumber(out ulong propertiesSize)
                || propertiesSize > int.MaxValue
                || !cursor.TrySkip((long)propertiesSize))
            {
                failure = "coder 的属性读不出来 / 越界";

                return false;
            }
        }

        return true;
    }
}
