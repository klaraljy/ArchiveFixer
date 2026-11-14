namespace ArchiveFixer.Detection;

/// <summary>
/// **next header 的结构走读**：把 7z 头读到"能判定 coder 链"为止，输出
/// <see cref="SevenZipHeaderShape"/>（走到哪一步停下）+ 结构原因，⛔ 自己不下加密结论。
///
/// <para>⛔ 刻意**只解析到能判定 coder 就停**（<c>kUnPackInfo</c> 的文件夹链读完就收工）：
/// 读得越少，越不会被与"加没加密"无关的结构（SubStreamsInfo、FilesInfo…）绊倒。</para>
///
/// <para>解码链 / 数据链里有没有 AES 由 <see cref="SevenZipCoderReader"/> 在走 coder 时标记；
/// 本类只负责"走到哪、读不读得下去"。结论文案不在这里（见 <see cref="SevenZipEncryptionVerdict"/>）。</para>
///
/// <para>所有"读不出来"的档都返回失败原因，⛔ <b>绝不当成"没加密"</b> —— 那是下游判据的事，
/// 而且那几档一律落"不知道"。</para>
/// </summary>
internal static class SevenZipHeaderWalker
{
    // ===== 7z 头属性 ID（官方 7zFormat.txt / 7zIn.cpp 的 kXxx 常量）=====
    private const byte KEnd = 0x00;
    private const byte KHeader = 0x01;
    private const byte KArchiveProperties = 0x02;
    private const byte KAdditionalStreamsInfo = 0x03;
    private const byte KMainStreamsInfo = 0x04;
    private const byte KFilesInfo = 0x05;
    private const byte KPackInfo = 0x06;
    private const byte KUnPackInfo = 0x07;
    private const byte KSize = 0x09;
    private const byte KCRC = 0x0A;
    private const byte KFolder = 0x0B;
    private const byte KEncodedHeader = 0x17;

    /// <summary>
    /// 解析 next header 的**开头那一段**：只回答"结构走到哪一步停下"，不回答"加没加密"。
    ///
    /// <para>四种起手：空头 / 既不是 <c>kHeader</c> 也不是 <c>kEncodedHeader</c> /
    /// <c>kEncodedHeader</c>（解码链在 StreamsInfo 里）/ <c>kHeader</c>（逐个属性走到
    /// MainStreamsInfo 或 FilesInfo）。</para>
    /// </summary>
    internal static SevenZipHeaderWalk Walk(byte[] header)
    {
        var cursor = new SevenZipHeaderCursor(header);

        if (!cursor.TryReadByte(out byte first))
        {
            return new SevenZipHeaderWalk { Shape = SevenZipHeaderShape.EmptyHeader };
        }

        if (first == KEncodedHeader)
        {
            if (!TryReadStreamsInfo(cursor, out bool hasAes, out bool sawCoderInfo, out string failure))
            {
                return new SevenZipHeaderWalk
                {
                    Shape = SevenZipHeaderShape.EncodedHeaderUnreadable,
                    Failure = failure
                };
            }

            if (hasAes)
            {
                return new SevenZipHeaderWalk { Shape = SevenZipHeaderShape.EncodedHeaderEncrypted };
            }

            /*
             * ⛔ 这一档**必须**是"不知道"：头被压缩时，文件数据的 coder 链在解压之后才看得见。
             * 实测 many-p.7z（80 个文件 + -p）与 many-plain.7z（同样 80 个文件、不加密）
             * 的编码头在这里**逐字节同构**（都只有 LZMA），而前者是真加密的
             * ⇒ 说"没加密"就是误报，"不知道"才是唯一诚实的答案。
             */
            return sawCoderInfo
                ? new SevenZipHeaderWalk { Shape = SevenZipHeaderShape.EncodedHeaderWithoutAes }
                : new SevenZipHeaderWalk { Shape = SevenZipHeaderShape.EncodedHeaderWithoutCoderInfo };
        }

        if (first != KHeader)
        {
            return new SevenZipHeaderWalk { Shape = SevenZipHeaderShape.NotA7zHeader };
        }

        while (cursor.TryReadByte(out byte propertyId))
        {
            if (propertyId == KEnd)
            {
                break;
            }

            if (propertyId == KArchiveProperties)
            {
                if (!TrySkipArchiveProperties(cursor, out string propertiesFailure))
                {
                    return new SevenZipHeaderWalk
                    {
                        Shape = SevenZipHeaderShape.ArchivePropertiesUnreadable,
                        Failure = propertiesFailure
                    };
                }

                continue;
            }

            if (propertyId == KMainStreamsInfo)
            {
                if (!TryReadStreamsInfo(cursor, out bool hasAes, out bool sawCoderInfo, out string failure))
                {
                    return new SevenZipHeaderWalk
                    {
                        Shape = SevenZipHeaderShape.MainStreamsUnreadable,
                        Failure = failure
                    };
                }

                if (hasAes)
                {
                    return new SevenZipHeaderWalk { Shape = SevenZipHeaderShape.MainStreamsEncrypted };
                }

                return sawCoderInfo
                    ? new SevenZipHeaderWalk { Shape = SevenZipHeaderShape.MainStreamsPlain }
                    : new SevenZipHeaderWalk { Shape = SevenZipHeaderShape.MainStreamsWithoutCoderInfo };
            }

            if (propertyId == KFilesInfo)
            {
                // 走到 FilesInfo 都还没见过 MainStreamsInfo ⇒ 这一包里没有数据流（只有目录 / 空文件）。
                return new SevenZipHeaderWalk { Shape = SevenZipHeaderShape.ReachedFilesInfo };
            }

            if (propertyId == KAdditionalStreamsInfo)
            {
                /*
                 * ⛔ 刻意不做这一档：AdditionalStreamsInfo 的边界在格式说明的不同版本里写法不一致
                 * （它内嵌的 StreamsInfo 后面到底还有没有一个 kEnd），而读错一步就会把后面的字节
                 * 当成头属性继续解析 —— 那正是"猜"。它只出现在外置属性 / 编码数据的包上，
                 * 内置 7z.exe 对普通归档从不产出。
                 */
                return new SevenZipHeaderWalk { Shape = SevenZipHeaderShape.AdditionalStreamsInfo };
            }

            return new SevenZipHeaderWalk
            {
                Shape = SevenZipHeaderShape.UnexpectedProperty,
                PropertyId = propertyId
            };
        }

        return new SevenZipHeaderWalk { Shape = SevenZipHeaderShape.EndedWithoutMainStreams };
    }

    /// <summary>
    /// 读一段 <c>StreamsInfo</c>：<c>[kPackInfo] [kUnPackInfo] [kSubStreamsInfo] kEnd</c>。
    ///
    /// <para>⚠ 读到 <c>kUnPackInfo</c> 就**直接返回**（coder 链全在那里，判定已经做完了）；
    /// 只有一整段 StreamsInfo 走完都没有 kUnPackInfo 时才会继续遇到 kEnd，
    /// 那时 <paramref name="sawCoderInfo"/> 为 false —— 调用方按"读不出 coder 链"处理，⛔ 不当成"没加密"。</para>
    /// </summary>
    private static bool TryReadStreamsInfo(
        SevenZipHeaderCursor cursor,
        out bool hasAes,
        out bool sawCoderInfo,
        out string failure)
    {
        hasAes = false;
        sawCoderInfo = false;
        failure = string.Empty;

        while (cursor.TryReadByte(out byte propertyId))
        {
            if (propertyId == KEnd)
            {
                return true;
            }

            if (propertyId == KPackInfo)
            {
                if (!TrySkipPackInfo(cursor, out failure))
                {
                    return false;
                }

                continue;
            }

            if (propertyId == KUnPackInfo)
            {
                sawCoderInfo = true;

                return TryReadUnPackInfo(cursor, out hasAes, out failure);
            }

            failure = "StreamsInfo 里出现了预期之外的属性 0x" + propertyId.ToString("X2");

            return false;
        }

        failure = "StreamsInfo 读到缓冲区末尾（头被截断）";

        return false;
    }

    /// <summary>
    /// 跳过 <c>kPackInfo</c>：<c>packPos NumPackStreams [kSize …] [kCRC …] kEnd</c>。
    ///
    /// <para>它与"加没加密"无关，但**必须正确跳过**才能走到后面的 kUnPackInfo。</para>
    /// </summary>
    private static bool TrySkipPackInfo(SevenZipHeaderCursor cursor, out string failure)
    {
        failure = string.Empty;

        if (!cursor.TryReadNumber(out _))
        {
            failure = "PackInfo 的 packPos 读不出来";

            return false;
        }

        if (!cursor.TryReadNumber(out ulong packStreams)
            || packStreams > SevenZipEncryptionReader.MaxPackStreams)
        {
            failure = "PackInfo 的流数读不出来 / 越界";

            return false;
        }

        while (cursor.TryReadByte(out byte propertyId))
        {
            if (propertyId == KEnd)
            {
                return true;
            }

            if (propertyId == KSize)
            {
                for (ulong index = 0; index < packStreams; index++)
                {
                    if (!cursor.TryReadNumber(out _))
                    {
                        failure = "PackInfo 的 kSize 读不出来";

                        return false;
                    }
                }

                continue;
            }

            if (propertyId == KCRC)
            {
                if (!TrySkipDigests(cursor, packStreams, out failure))
                {
                    return false;
                }

                continue;
            }

            failure = "PackInfo 里出现了预期之外的属性 0x" + propertyId.ToString("X2");

            return false;
        }

        failure = "PackInfo 读到缓冲区末尾（头被截断）";

        return false;
    }

    /// <summary>
    /// 跳过一组 CRC：<c>AllAreDefined(1)</c>；为 1 时后面是 <c>count × 4</c> 字节，
    /// 为 0 时先是一张 <c>count</c> 位的位图，再按置位个数各 4 字节（7-Zip 的 <c>ReadHashDigests</c>）。
    /// </summary>
    private static bool TrySkipDigests(SevenZipHeaderCursor cursor, ulong count, out string failure)
    {
        failure = string.Empty;

        if (!cursor.TryReadByte(out byte allAreDefined))
        {
            failure = "kCRC 的 allAreDefined 读不出来";

            return false;
        }

        if (allAreDefined != 0)
        {
            if (!cursor.TrySkip((long)count * 4))
            {
                failure = "kCRC 的长度越界";
                return false;
            }

            return true;
        }

        int bitmapLength = (int)((count + 7) / 8);

        if (!cursor.TryReadBytes(bitmapLength, out byte[] defined))
        {
            failure = "kCRC 的位图越界";

            return false;
        }

        long definedCount = 0;

        for (ulong index = 0; index < count; index++)
        {
            if ((defined[(int)(index / 8)] & (1 << (int)(index % 8))) != 0)
            {
                definedCount++;
            }
        }

        if (!cursor.TrySkip(definedCount * 4))
        {
            failure = "kCRC 的定义值越界";

            return false;
        }

        return true;
    }

    /// <summary>读 <c>kUnPackInfo</c> 的 <c>kFolder(0x0B)</c> 段，把每个文件夹的 coder 链走完。</summary>
    private static bool TryReadUnPackInfo(SevenZipHeaderCursor cursor, out bool hasAes, out string failure)
    {
        hasAes = false;
        failure = string.Empty;

        if (!cursor.TryReadByte(out byte propertyId) || propertyId != KFolder)
        {
            failure = "UnPackInfo 的第一个属性不是 kFolder";

            return false;
        }

        if (!cursor.TryReadNumber(out ulong folders)
            || folders == 0
            || folders > SevenZipEncryptionReader.MaxFolders)
        {
            failure = "文件夹数读不出来 / 越界";

            return false;
        }

        if (!cursor.TryReadByte(out byte external))
        {
            failure = "文件夹数据是否外置的那一字节读不出来";

            return false;
        }

        if (external != 0)
        {
            // 外置时文件夹定义在 AdditionalStreamsInfo 里 —— 这一档我们读不出来，⛔ 不猜。
            failure = "文件夹数据是外置的（External = 1）";

            return false;
        }

        ulong totalCoders = 0;

        for (ulong folder = 0; folder < folders; folder++)
        {
            if (!cursor.TryReadNumber(out ulong coders)
                || coders == 0
                || coders > SevenZipEncryptionReader.MaxCodersPerFolder)
            {
                failure = "coder 个数读不出来 / 越界";

                return false;
            }

            totalCoders += coders;

            if (totalCoders > SevenZipEncryptionReader.MaxCodersTotal)
            {
                failure = "coder 太多，超出一次判读的预算";

                return false;
            }

            for (ulong coder = 0; coder < coders; coder++)
            {
                if (!SevenZipCoderReader.TryReadCoder(cursor, ref hasAes, out failure))
                {
                    return false;
                }
            }
        }

        /*
         * coder 链已经全部读到 ⇒ 判定做完了。⛔ 刻意不再往下读 kCodersUnPackSize / kCRC：
         * 那两个属性的长度依赖"每个文件夹有几条出流"，多读一步就多一处可能与格式说明理解不一致的地方。
         */
        return true;
    }

    /// <summary>
    /// 跳过 <c>kArchiveProperties</c>：<c>while(有属性) { 属性类型 属性长度 属性数据 } kEnd</c>
    /// （7-Zip 的 <c>ReadHeader</c>：类型读到 kEnd(0) 就结束，别的都是"长度 + 数据"）。
    /// </summary>
    private static bool TrySkipArchiveProperties(SevenZipHeaderCursor cursor, out string failure)
    {
        failure = string.Empty;

        while (cursor.TryReadNumber(out ulong propertyType))
        {
            if (propertyType == 0)
            {
                return true;
            }

            if (!cursor.TryReadNumber(out ulong propertySize)
                || propertySize > int.MaxValue
                || !cursor.TrySkip((long)propertySize))
            {
                failure = "属性长度读不出来 / 越界";

                return false;
            }
        }

        failure = "ArchiveProperties 读到缓冲区末尾（头被截断）";

        return false;
    }
}
