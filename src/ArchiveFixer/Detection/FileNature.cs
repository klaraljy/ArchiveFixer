using System;
using System.Globalization;
using System.IO;
using ArchiveFixer.Helpers;

namespace ArchiveFixer.Detection
{
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

    /// <summary>
    /// **底层：一份文件"到底是什么"** —— 一张卡，四个来源的事实各就各位（用户 2026-10-11 口径：
    /// 「就是统筹到同一组上面去…我们就是先看看**底层能够识别到文件是什么东西**」）。
    ///
    /// <para><b>为什么要有这张卡</b>：分卷后缀那套专属算法（<see cref="ExtensionHelper"/> /
    /// <see cref="FileNameHelper"/> / <see cref="VolumeNameRepair"/> / <see cref="VolumeGroupDetector"/>）已经做了很多文章，
    /// 内容证据（<see cref="VolumeNumberFromContent"/>）也有一套；可**上面每一处判据都在自己重新拼**这四类事实 ——
    /// 于是同一个文件在不同地方能被认成不同的东西（第 125 轮那次误接、第 127 轮轮判定被名字带偏，都是这么来的）。
    /// ⇒ **"这一份是什么"只许有一个出口**，就是这里：上面只管取事实，⛔ 不许自己再判一遍。</para>
    ///
    /// <para><b>四类事实（按证据强度）</b>：</para>
    /// <list type="number">
    /// <item><description><b>内容 · 格式</b>：魔数（<see cref="VolumeContentInference.SniffFormat"/>）—— 不信后缀。</description></item>
    /// <item><description><b>内容 · 分卷身份</b>：<see cref="VolumeNumberFromContent"/> 读归档自己写的头
    /// （RAR5 主头 <c>MHFL_VOLUME 0x0001</c> / <c>MHFL_VOLNUMBER 0x0002</c>、RAR4 <c>MHD_FIRSTVOLUME</c>、
    /// 跨盘 ZIP 的 EOCD 盘号）⇒ 三档结论 + 卷号 + 首卷标志。**7z 那一族格式里没有卷号 ⇒ 只能是"判不出"**
    /// （判据在 <see cref="VolumeContentInference"/> + 试开那边）。</description></item>
    /// <item><description><b>名字 · 分卷后缀那套算法</b>：族 + 卷标记 + 规范卷名 + 本族第 1 卷名（入口包名）
    /// —— 脏名（`111.par删t1.ra除r`）也能算出来；它**只是线索**，⛔ 不是身份。</description></item>
    /// <item><description><b>盘上 · 物理身份</b>：卷序列号 + 文件索引（<see cref="FileIdentity"/>）——
    /// 同一卷里改名 / 移动之后**不变**（拷贝算新文件），所以"是不是同一份"用它，⛔ 不用路径。</description></item>
    /// </list>
    ///
    /// <para>⚠ 本类**不新造任何判据**：四个来源全部转调既有唯一出口；它只负责"把四份事实摆在一张卡上"。</para>
    /// </summary>
    public sealed class FileNatureCard
    {
        /// <summary>这一份的完整路径。</summary>
        public string Path { get; init; } = string.Empty;

        /// <summary>字节数（读不到 = 0）。</summary>
        public long Size { get; init; }

        // ===== ① 内容 · 格式（魔数）=====

        /// <summary>魔数认出来的格式（⛔ 与后缀无关）。</summary>
        public VolumeContentFormat Format { get; init; }

        // ===== ② 内容 · 分卷身份（归档自己写的头）=====

        /// <summary>内容证据的三档结论。</summary>
        public VolumeNumberFromContent.VolumeMembershipVerdict Membership { get; init; }

        /// <summary>内容里读出来的卷号（1 起）；判不出 = null。</summary>
        public int? VolumeNumber { get; init; }

        /// <summary>内容自述"我就是第 1 卷"（RAR 1.5–4.x 的主头标记；RAR5 第 1 卷表现为"没有卷号字段"）。</summary>
        public bool FirstVolumeFlag { get; init; }

        /// <summary>读不出卷号时是哪一档（给日志与文案用；成功时 = <see cref="VolumeNumberFail.None"/>）。</summary>
        public VolumeNumberFail ContentFail { get; init; }

        // ===== ②b 内容 · 专属算法（单一 vs 分卷；用户 2026-10-13 口径）=====

        /// <summary>内容自证"完整的单卷 / 某组的第一片 / 某组的一片"（见 <see cref="ArchiveBodyKind"/>）。</summary>
        public ArchiveBodyKind BodyKind { get; init; }

        /// <summary>
        /// **7z 起始头明文自述的整包字节数**（<c>32 + NextHeaderOffset + NextHeaderSize</c>）；判不出 = 0。
        ///
        /// <para>它是"整包应当有多少字节"这条硬证据（<see cref="SevenZipStartHeader"/>）在卡上的落点 ——
        /// 7z 的中间片内容里没有身份信息，唯一能自证的数就是这个。</para>
        /// </summary>
        public long DeclaredTotalBytes { get; init; }

        /// <summary>
        /// **按内容算出来的片数**（只有 <see cref="ArchiveBodyKind.VolumeFirstPiece"/> 那一档算得出：
        /// 整包自述字节数 ÷ 这一片自己的字节数，向上取整 —— 7z 分卷除末片外等大）；
        /// 判不出 = 0（⛔ 不编）。
        /// </summary>
        public int DeclaredVolumeCount { get; init; }

        /// <summary>
        /// **内容里那个"卷号字段"的原始值**（⛔ 不是 1 起的卷号）：只有 RAR 一族给得出
        /// （RAR5 主头的 <c>MHFL_VOLUME_NUMBER</c>、RAR4 归档结尾块的 <c>EARC_VOLNUMBER</c>）；判不出 = null。
        ///
        /// <para>为什么要留原值：RAR4 老编号族那一档**基数待定**（见
        /// <see cref="VolumeNumberFromContent"/> 里 `ReadRar4Legacy` 的注释：「第 1 卷 0、第 2 卷 1」是
        /// 真样本的字节，但格式没写死基数）⇒ 单看一份读不出"它是第几卷"，
        /// 而**整组之间的相对大小**是可靠的：以第一片那个值为锚点，差值就是卷号差。
        /// 这条证据的用途 = 非 7z 族"按内容补齐槽位"那一档（用户 2026-10-13 口径：名字读不出卷号
        /// 是底层识别的事）—— 第一片由 <see cref="BodyKind"/> 认，其余几片靠这个原值定序。</para>
        /// </summary>
        public int? RawVolumeField { get; init; }

        /// <summary>
        /// 这一份的**尾部有 zip 的 EOCD**（设计文档 §6.6 A / 工单 #10）：通用分片族里它 = **末片**的证据。
        /// </summary>
        public bool HasZipEndRecord { get; init; }

        /// <summary>
        /// 这一份的**开头是 zip 本地文件头** `PK\x03\x04`：通用分片族里它 = **第 1 片**的证据。
        /// </summary>
        public bool HasZipLocalHeader { get; init; }

        // ===== ③ 名字 · 分卷后缀那套算法（线索，不是身份）=====

        /// <summary>按名字算出来的卷号（脏名也算得出）；不是分卷样子的名字 = null。</summary>
        public int? VolumeIndexByName { get; init; }

        /// <summary>按名字推出来的**本族第 1 卷名**（跨盘 ZIP 族 = 末片 `X.zip`，也就是入口包名）；判不出 = null。</summary>
        public string? FirstVolumeNameByName { get; init; }

        /// <summary>按名字算出来的**规范卷名**（`111.part删2.ra除r` ⇒ `111.part2.rar`）；判不出 = 空串。</summary>
        public string CanonicalVolumeName { get; init; } = string.Empty;

        /// <summary>归档基名（唯一出口 <see cref="FileNameHelper.GetArchiveBaseName"/>）；判不出 = 空串。</summary>
        public string ArchiveBaseName { get; init; } = string.Empty;

        // ===== ④ 盘上 · 物理身份 =====

        /// <summary>拿得到物理身份吗（文件在盘上、能打开句柄）。</summary>
        public bool HasIdentity { get; init; }

        /// <summary>所在卷的序列号（与 <see cref="FileIndex"/> 合起来 = 这一份文件的身份）。</summary>
        public ulong VolumeSerial { get; init; }

        /// <summary>文件索引（同一卷内唯一；**改名 / 移动不变**，拷贝会变）。</summary>
        public ulong FileIndex { get; init; }

        /// <summary>
        /// **这一份要不要参与"按分卷组办"的动作**（接片 / 收卷 / 轮判定的分卷那一支）。
        ///
        /// <para>判据：内容证据**可证"不是分卷组的一员"** ⇒ **不参与**（它就是一份普通的单卷归档 / 别的文件）；
        /// 其余两种（是分卷 / 判不出）⇒ **参与**（判不出照旧按名字 —— ⛔ 不许把"判不出"当"不是"）。</para>
        /// </summary>
        public bool ParticipatesInVolumeHandling =>
            Membership != VolumeNumberFromContent.VolumeMembershipVerdict.ProvablyNotVolumeMember;

        /// <summary>一行判据输入（⛔ 只写程序当场算出来的值）。</summary>
        public string Describe()
        {
            string number = VolumeNumber.HasValue
                ? VolumeNumber.Value.ToString(CultureInfo.InvariantCulture)
                : "判不出";
            string byName = VolumeIndexByName.HasValue
                ? VolumeIndexByName.Value.ToString(CultureInfo.InvariantCulture)
                : "判不出";
            string identity = HasIdentity
                ? string.Format(CultureInfo.InvariantCulture, "{0:X8}:{1:X}", VolumeSerial, FileIndex)
                : "判不出";

            return $"「{System.IO.Path.GetFileName(Path)}」｜内容：格式={Format} 分卷身份={Membership}"
                + $" 卷号={number} 首卷标志={FirstVolumeFlag} 原始卷号字段={(RawVolumeField.HasValue ? RawVolumeField.Value.ToString(CultureInfo.InvariantCulture) : "判不出")} 结论={ContentFail}"
                + $" 专属算法：单卷/分卷={BodyKind}"
                + (DeclaredTotalBytes > 0 ? $" 自述整包={DeclaredTotalBytes} 字节" : string.Empty)
                + (DeclaredVolumeCount > 0 ? $" 按内容算的片数={DeclaredVolumeCount}" : string.Empty)
                + $"｜名字：卷号={byName} 本族第1卷={FirstVolumeNameByName ?? "判不出"} 规范名={CanonicalVolumeName}"
                + $" 基名={ArchiveBaseName}"
                + $"｜盘上：身份={identity}"
                + $"｜⇒ 参与分卷那一套={ParticipatesInVolumeHandling}";
        }
    }

    /// <summary>
    /// **底层识别的唯一出口**：<c>FileNature.Inspect(路径)</c> ⇒ <see cref="FileNatureCard"/>。
    ///
    /// <para>⛔ 上面任何一处判据要问"这一份是什么"，都走这里；⛔ 不许再自己拼名字、自己读魔数、自己判分卷。
    /// 它判不出（文件不在 / 读不动）时**照样返回一张卡**（各项落成"判不出"），由调用方按"判不出 ⇒ 什么都不做"办。</para>
    /// </summary>
    public static class FileNature
    {
        /// <summary>
        /// **自述片数的合理性上限**（2026-10-14 实测定的闸）。
        ///
        /// <para>为什么必须有：垃圾头也会"自述"一个荒谬的整包大小 —— 实测那份 `broken.7z`
        /// （7z 魔数 + 随机字节、4096 字节）读出 `自述整包=1252871390030289531 字节`、
        /// `按内容算的片数=2118287382` ⇒ 卡上会写 `VolumeFirstPiece` ⇒ 下游把一份**坏包**
        /// 报成「分卷缺失」（方向完全相反）。</para>
        ///
        /// <para>取值：用户口径是「分卷数目很少大于 5」；这里放到 **1024** —— 是"别把合理的组挡掉"的宽档，
        /// 只用来挡"一眼就知道是噪声"的量级（21 亿片、1.25 EB）。⛔ 它不是"分卷数上限"这条业务判据
        /// （那条判据不在这里、也不存在）。</para>
        /// </summary>
        private const long MaxSaneDeclaredVolumeCount = 1024;

        public static FileNatureCard Inspect(string? path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return new FileNatureCard { Path = path ?? string.Empty };
            }

            string name = FileNameHelper.GetFileName(path);
            VolumeNumberReading reading = VolumeNumberFromContent.Read(path);

            bool hasIdentity = FileIdentity.TryGetIdentity(path, out ulong volumeSerial, out ulong fileIndex);

            VolumeGroupResolver.TryGetCanonicalVolumeName(name, out string canonical);

            /*
             * ⚠ 大小**直接问文件**（`reading.Size` 只在部分读取路径上被填 —— 实测 EEE 那几份 200 MB 的包
             * 在卡上印成 `0 字节`；大小是盘上事实，不该借道"内容读出来的那一份"）。
             */
            long size = 0;

            try
            {
                size = new FileInfo(path).Length;
            }
            catch
            {
                // 读不到大小 ⇒ 留 0（"判不出"那一档，⛔ 不猜）。
            }

            /*
             * ===== ②b 专属算法：**单一 还是 分卷**（用户 2026-10-13 口径）=====
             *
             * 7z 那一族格式里没有卷号（见 <see cref="VolumeNumberFromContent"/> 的能力矩阵），
             * 于是"这一份到底是什么"只能由**起始头那句明文自述**回答：
             * 自述整包字节数 **≤** 自己 ⇒ 它就是**一份完整的单卷归档**；
             * 自述整包字节数 **>** 自己 ⇒ 它是**这一组的第一片**，而且**片数**也读得出来
             * （整包 ÷ 这一片，向上取整 —— 7z 分卷除末片外等大）。
             * ⛔ 中间片（没有魔数、读不出起始头）照旧"判不出" —— ⛔ 不许当"不是分卷"。
             */
            VolumeContentFormat format = VolumeContentInference.SniffFormat(path);
            ArchiveBodyKind bodyKind = ArchiveBodyKind.Unknown;
            long declaredTotalBytes = 0;
            int declaredVolumeCount = 0;

            if (format == VolumeContentFormat.SevenZip
                && SevenZipStartHeader.TryReadExpectedTotalBytes(path, out long expectedTotal)
                && expectedTotal > 0)
            {
                declaredTotalBytes = expectedTotal;

                if (size > 0)
                {
                    if (expectedTotal <= size)
                    {
                        bodyKind = ArchiveBodyKind.SingleComplete;
                    }
                    else
                    {
                        /*
                         * ⚠ 2026-10-14（`LogVolumePolicyTests.批末汇总_一眼看到成功失败与逐条失败` 当场打红，
                         * 运行期实测）：**垃圾头自述出来的荒谬值不许当证据**。那份 `broken.7z`
                         * （7z 魔数 + 随机字节）读出片数 21 亿、整包 1.25 EB ⇒ 卡上写 `VolumeFirstPiece`
                         * ⇒ 一份坏包被下游报成「分卷缺失」。⇒ 片数过一道合理性闸（见
                         * <see cref="MaxSaneDeclaredVolumeCount"/>）才算事实；过不了 ⇒ **判不出**（保持 Unknown），
                         * 照旧走"引擎说了算 / 损坏"那条老口径。⛔ 不猜、⛔ 不放宽任何既有判据。
                         */
                        long count = (expectedTotal + size - 1) / size;

                        if (count <= MaxSaneDeclaredVolumeCount)
                        {
                            bodyKind = ArchiveBodyKind.VolumeFirstPiece;
                            declaredVolumeCount = (int)count;
                        }
                    }
                }
            }
            else if (reading.IsVolumeMember && reading.Number is int contentNumber && contentNumber > 1)
            {
                // RAR5 / 跨盘 ZIP 那些"内容里就写着卷号"的族：> 1 ⇒ 它是这一组的一片（不是第一片）。
                bodyKind = ArchiveBodyKind.VolumePiece;
            }
            else if (reading.IsVolumeMember && reading.Number == 1)
            {
                /*
                 * ⚠ 2026-10-13（第十四轮读数之后的专属算法补全）：**RAR5 的第 1 卷** ——
                 * 官方布局里"第 1 卷没有卷号字段"，所以 <see cref="VolumeNumberFromContent"/> 补 1
                 * （`ReadRar5` 的 `number = 1` 初值）。这一条以前没人写 ⇒ 第 1 卷停在 `Unknown`，
                 * 而它恰恰是"整组入口那一份"（非 7z 族按内容补片时唯一的锚点）。
                 */
                bodyKind = ArchiveBodyKind.VolumeFirstPiece;
            }
            else if (reading.IsVolumeMember && reading.FirstVolumeFlag)
            {
                /*
                 * **RAR4**：主头 flags 里的"第 1 卷"标记（`MHD_FIRSTVOLUME 0x0100`，真字节证据见
                 * `VolumeNumberContentTests.RAR4_…` 那一组）⇒ 同样自证"我是这一组的第一片"。
                 * ⛔ 老编号族（`.rar` + `.r00`）也照样给这个事实（它是**内容**结论）；
                 * "要不要按名字动手"由下面那些名字尺子回答，⛔ 两件事不许混在一起。
                 */
                bodyKind = ArchiveBodyKind.VolumeFirstPiece;
            }

            /*
             * ===== 专属算法（续）：**其它族**能自证到什么就写什么 =====
             *
             * <list type="bullet">
             * <item><description><b>总片数</b>：跨盘 ZIP 的末片里 EOCD **自己写着总盘数**
             * （<see cref="VolumeNumberReading.Total"/>）⇒ 与 7z 那条"按字节算"共用**同一个字段**
             * （哪个族答得出就用哪个；⛔ 都答不出 = 0 = 判不出，⛔ 不编）。</description></item>
             * <item><description><b>完整的单卷 ZIP</b>：内容证据**可证"不是分卷组的一员"**
             * （EOCD 的盘号只有一盘）⇒ 它就是一份完整的单卷归档 ⇒ <see cref="ArchiveBodyKind.SingleComplete"/>；
             * 这一条专治"把一份普通的单卷 zip 当成某分卷组的一片"。</description></item>
             * </list>
             */
            if (reading.Total is int contentTotal && contentTotal >= 1)
            {
                declaredVolumeCount = contentTotal;
            }

            /*
             * ⚠ 2026-10-13（守门用例实测逮到）：这一档原来是上面那一条的 `else if` ⇒
             * **跨盘 ZIP 的末片**（`reading.Total` 答得出）与**单盘 ZIP** 这两件事被写成了互斥，
             * 于是"内容可证只有一盘"的那一份 bodyKind 停在 `Unknown`（用例逐字：
             * `Expected: SingleComplete / Actual: Unknown`）。两件事本来就不冲突：
             * 一个是"共几片"、一个是"它是不是分卷组的一员" ⇒ 拆成独立的一条。
             */
            if (bodyKind == ArchiveBodyKind.Unknown
                && format == VolumeContentFormat.Zip
                && !reading.IsVolumeMember)
            {
                bodyKind = ArchiveBodyKind.SingleComplete;
            }

            return new FileNatureCard
            {
                Path = path!,
                Size = size,
                Format = format,
                Membership = VolumeNumberFromContent.ClassifyMembership(path),
                VolumeNumber = reading.Number,
                FirstVolumeFlag = reading.FirstVolumeFlag,
                ContentFail = reading.Fail,
                BodyKind = bodyKind,
                DeclaredTotalBytes = declaredTotalBytes,
                DeclaredVolumeCount = declaredVolumeCount,
                RawVolumeField = reading.RawField,
                HasZipEndRecord = reading.HasZipEndRecord,
                HasZipLocalHeader = reading.HasZipLocalHeader,
                VolumeIndexByName = VolumeGroupDetector.TryGetVolumeIndex(name),
                FirstVolumeNameByName = VolumeGroupDetector.TryGetFirstVolumeName(name),
                CanonicalVolumeName = canonical ?? string.Empty,
                ArchiveBaseName = FileNameHelper.GetArchiveBaseName(name),
                HasIdentity = hasIdentity,
                VolumeSerial = volumeSerial,
                FileIndex = fileIndex
            };
        }
    }
}
