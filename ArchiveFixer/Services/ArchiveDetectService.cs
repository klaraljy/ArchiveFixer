using ArchiveFixer.Detection;
using ArchiveFixer.Extraction;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ArchiveFixer.Services
{
    /// <summary>
    /// 压缩包真实格式识别服务。
    /// 
    /// 职责：
    /// 1. 读取文件头。
    /// 2. 通过魔数判断真实格式。
    /// 3. 文件头不认识时，再读文件尾部找"内嵌归档"（双面文件）。
    /// 4. 尾部没命中时，顺序扫一遍找 RAR / 7z 魔数与"不在末尾的 ZIP"（第 38 / 40 条）。
    /// 5. 返回 DetectResult。
    /// 6. 判断后缀状态。
    /// 7. 将识别结果应用到 ArchiveTask。
    /// 
    /// 注意：
    /// 这里只读取文件头（34 KB）和"认识不出来时"的文件尾部（256 KB）；
    /// 只有尾部那条路也没命中时才会顺序扫一遍（上限 512 MiB，命中即停），
    /// **正常包一个字节都不多读**。
    /// </summary>
    public class ArchiveDetectService
    {
        // 需要读到 ISO 9660 卷描述符位置（偏移 0x8001），所以读取 34KB 文件头。
        private const int HeaderReadLength = 34816;

        /// <summary>
        /// 识别文件真实格式。
        /// </summary>
        public async Task<DetectResult> DetectAsync(
            string filePath,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return CreateUnknownResult("文件路径为空");
            }

            if (!File.Exists(filePath))
            {
                return CreateUnknownResult("文件不存在");
            }

            try
            {
                byte[] header = await ReadHeaderAsync(filePath, HeaderReadLength, cancellationToken);

                if (header.Length == 0)
                {
                    return CreateUnknownResult("文件为空");
                }

                DetectResult headerResult = DetectByHeader(header);

                /*
                 * 只有文件头"不认识"时才去看文件尾部。
                 *
                 * 为什么必须加这个条件：尾部检测要多读 128 KB，而它对已经认出来的格式毫无意义 ——
                 * 给每个正常包都多读一次纯属浪费（一批几百个包就是几十 MB 的无用 IO）。
                 * 更关键的是**不许**给已识别的格式也去读尾部：自解压安装器（PE 头 + 尾部归档）、
                 * 可执行文件后面接数据的形态都很常见，一律去尾部找 ZIP 会大面积误报。
                 * 判据用"文件头是否认识"而不是逐个排除格式，所以以后新增魔数也自动受这条保护。
                 *
                 * 反过来，尾部检测正是"只读文件头"这个设计的补丁：
                 * 真实案例里 ZIP 在 17 MB 之后，前 34 KB 全是 MP4 的 ftyp 头，
                 * 只看文件头永远得不出结论，这种文件就被判成"格式未知"了。
                 */
                if (headerResult.Format == "Unknown")
                {
                    /*
                     * 尾部那 256 KB 里找 ZIP 的 EOCD（最便宜的一条：只读尾部）。
                     * 这一条覆盖"视频/图片 + 尾部整包"的全部真实样本（第 33/38 条那批）。
                     */
                    DetectResult? embeddedResult = DetectEmbeddedArchive(filePath, headerResult);

                    if (embeddedResult != null)
                    {
                        return embeddedResult;
                    }

                    /*
                     * 尾部那条路没命中时，**一遍顺序扫**同时找两样东西（用户 2026-09-25 第 40 条）：
                     * ①RAR / 7z 的魔数（第 38 条追加：这两种格式的目录结构不支持从尾部反推起点，
                     *   实测前缀 20 MB 时连引擎都报"不是归档"，只能自己定位起点、抠出来再解）；
                     * ②ZIP 的 EOCD 候选（第 40 条：ZIP 前面垫了图片、**后面还垫了数据**时，
                     *   尾部 256 KB 里根本没有 EOCD，整条"从尾部反推"的路失效）。
                     *
                     * 为什么合成一遍扫：两条路在"都没命中"时要各读一遍整文件，
                     * 一批几十个包就是白读几十遍 —— 一次 IO 拿两个结论，代价只算一次。
                     */
                    TailArchiveScanResult scan = TailArchiveScanner.Scan(filePath);

                    /*
                     * 优先级：**自洽校验通过的 ZIP 优先于一个孤零零的魔数**。
                     * 理由：魔数只有 7 个字节，它可能只是 ZIP **里面装着的一个 RAR 文件**
                     * （打包者常这么套），而 ZIP 候选要过"中央目录签名 + 局部文件头"两条位置校验，
                     * 是实打实的"这里有一个 ZIP"（判据见 EmbeddedArchiveDetector，一个字没放宽）。
                     */
                    if (scan.ZipEocdOffsets.Count > 0)
                    {
                        EmbeddedArchiveInfo middle = EmbeddedArchiveDetector.DetectAt(filePath, scan.ZipEocdOffsets);

                        if (middle.Found)
                        {
                            return BuildEmbeddedResult(filePath, middle, headerResult);
                        }
                    }

                    if (scan.Archive != null)
                    {
                        return BuildTailArchiveResult(filePath, scan.Archive, headerResult);
                    }
                }

                return headerResult;
            }
            catch (UnauthorizedAccessException)
            {
                return CreateUnknownResult("没有权限读取文件");
            }
            catch (IOException ex)
            {
                return CreateUnknownResult("读取文件失败：" + ex.Message);
            }
            catch (Exception ex)
            {
                return CreateUnknownResult("识别失败：" + ex.Message);
            }
        }

        /// <summary>
        /// 文件头不认识、尾部那 256 KB 也没命中 ZIP、顺序扫也没找到 RAR / 7z 魔数时，
        /// 把扫出来的归档签名落成识别结论（用户 2026-09-25 第 38 条追加）。
        ///
        /// <para>结论与 ZIP 那条路**同一形状**：<see cref="DetectResult.Format"/> 是真实格式、
        /// <see cref="DetectResult.EmbeddedArchiveOffset"/> 是起点，管线照旧"抠出 [起点, 末尾) → 交给引擎"。</para>
        ///
        /// <para>两点如实说明：①**终点取文件末尾** —— RAR / 7z 没有 ZIP 那种能算出精确终点的结构，
        /// 而两者都能容忍归档结束标记之后的少量尾巴（抠出来的副本照旧能解）；
        /// ②<see cref="DetectResult.EmbeddedDirectReadSupported"/> 一定是 <c>false</c> ——
        /// 内置直读器只会解 ZIP，RAR / 7z 必须走"抠取 + 引擎"。</para>
        /// </summary>
        private DetectResult BuildTailArchiveResult(string filePath, TailArchiveSignature signature, DetectResult headerResult)
        {
            long end = headerResult.EmbeddedArchiveEnd;

            if (end <= signature.Offset)
            {
                try
                {
                    end = new FileInfo(filePath).Length;
                }
                catch
                {
                    end = 0;
                }
            }

            return new DetectResult
            {
                Format = signature.Format,
                SuggestedExtension = signature.SuggestedExtension,
                IsArchive = true,
                IsKnownFormat = true,
                IsProbablyEncrypted = false,
                Message = signature.Describe(),
                HeaderHex = headerResult.HeaderHex,
                Confidence = 80,
                EmbeddedArchiveOffset = signature.Offset,
                EmbeddedArchiveEnd = end,
                EmbeddedDirectReadSupported = false,
                EmbeddedDirectReadReason = "内置直读器只解 ZIP；RAR / 7z 这条走「抠取 + 引擎」"
            };
        }

        /// <summary>
        /// 文件头不认识时，再看一眼文件尾部有没有"内嵌归档"（双面文件）。
        ///
        /// 命中时返回的置信度是 80 而不是 100：结论不是"文件开头就是 ZIP"这种直接证据，
        /// 而是"尾部 EOCD 与中央目录位置自洽、算出来的起点处确实是 PK 03 04"的推断。
        /// 80 也是给后续流程的信号 —— 这种文件要按偏移抠出来才能解压。
        ///
        /// 不命中返回 null（而不是一个 IsArchive=false 的结果）：调用方要保留原文件头的 HeaderHex
        /// 与消息，不能被一个"空结果"覆盖掉。
        /// </summary>
        private static DetectResult? DetectEmbeddedArchive(string filePath, DetectResult headerResult)
        {
            EmbeddedArchiveInfo info = EmbeddedArchiveDetector.Detect(filePath);

            return info.Found ? BuildEmbeddedResult(filePath, info, headerResult) : null;
        }

        /// <summary>
        /// 把一个"命中了的"内嵌归档结论落成 <see cref="DetectResult"/>。
        ///
        /// <para>两条路共用这一份：①尾部 256 KB 里找到 EOCD（<see cref="EmbeddedArchiveDetector.Detect"/>）；
        /// ②整文件顺序扫找到 EOCD（<see cref="EmbeddedArchiveDetector.DetectAt"/>，第 40 条）。
        /// 共用是刻意的 —— 直读探查、置信度、文案口径只允许有一处。</para>
        /// </summary>
        private static DetectResult BuildEmbeddedResult(string filePath, EmbeddedArchiveInfo info, DetectResult headerResult)
        {
            /*
             * 顺手做一次**只读**的直读探查（用户 2026-09-24 需求第 7 条）。
             *
             * 为什么要在这里探：空间核算必须在**排任务之前**就知道"这一次要不要那份等大的临时副本"，
             * 否则账面上会一边说"直读省了副本"、一边把它预留出来（用户明确不许）。
             * 代价只有一次尾部读 + 一次中央目录读，而且只对**识别成内嵌归档**的文件做 ——
             * 普通包一个字节都不多读。
             *
             * 探查失败 / 抛异常一律按"不支持"处理（返回 false + 原因），绝不因此让识别失败：
             * 那时解压侧会照旧回落到抠取 + 7z，一切与今天一样。
             */
            EmbeddedZipProbeResult directRead = EmbeddedZipStreamExtractor.Probe(
                filePath,
                info.Offset,
                info.ArchiveEnd);

            /*
             * 加密的 AES 内嵌包（百度网盘那种分享包）在识别阶段**拿不到密码**，探出来是"不支持"；
             * 但它其实只差一个密码 —— 解压那一刻候选密码一到就能直读（见 ExtractionCoordinator）。
             * 所以这里按"能直读"记：空间账面上不该为一笔**根本不会发生**的抠取副本预留空间
             * （40 GB 的双面文件被那笔虚假占用卡在空间门上，是实打实的误拦）。
             * 密码不对时是硬失败（PasswordRejected），同样不会抠副本 —— 这个乐观是准的。
             */
            bool directReadApplies = directRead.Supported || directRead.RequiresPassword;

            return new DetectResult
            {
                Format = "ZIP",
                SuggestedExtension = ".zip",
                IsArchive = true,
                IsKnownFormat = true,
                IsProbablyEncrypted = false,
                Message = info.Message,
                HeaderHex = headerResult.HeaderHex,
                Confidence = 80,
                EmbeddedArchiveOffset = info.Offset,
                EmbeddedArchiveEnd = info.ArchiveEnd,
                EmbeddedDirectReadSupported = directReadApplies,
                EmbeddedDirectReadReason = directReadApplies
                    ? string.Empty
                    : (directRead.PathRejected ? directRead.Message : directRead.Reason)
            };
        }

        /// <summary>
        /// 识别并应用到任务。
        /// </summary>
        public async Task<DetectResult> ApplyDetectResultAsync(
            ArchiveTask task,
            CancellationToken cancellationToken = default)
        {
            if (task == null)
            {
                return CreateUnknownResult("任务为空");
            }

            task.Operation = StatusText.OpScan;
            task.Status = StatusText.Scanning;
            task.ProgressText = StatusText.ProgressProcessing;
            task.ErrorMessage = string.Empty;
            task.LastUpdatedTime = DateTime.Now;

            DetectResult result = await DetectAsync(task.CurrentPath, cancellationToken);

            string currentExtension = ExtensionHelper.GetLastExtensionDisplay(task.CurrentPath);

            task.FileName = Path.GetFileName(task.CurrentPath);
            task.DirectoryPath = Path.GetDirectoryName(task.CurrentPath) ?? string.Empty;
            task.CurrentExtension = currentExtension;

            task.DetectedFormat = result.Format;
            task.SuggestedExtension = result.SuggestedExtension;
            task.ExtensionStatus = GetExtensionStatus(
                task.FileName,
                currentExtension,
                result);

            task.IsArchive = result.IsArchive;
            task.IsEncrypted = result.IsProbablyEncrypted;

            // 内嵌归档偏移必须落到任务上：解压管线靠它决定"要不要先抠出来"。
            // 终点（EOCD + 22 + 注释长度）同样要落：真实文件在 EOCD 之后还有十几 KB 正常数据，
            // 抠取按终点截断，不能拿"文件末尾"当终点。
            // 直读探查结论一并落下来：**只用于空间核算**（直读时那笔抠取副本不预留）。
            task.EmbeddedArchiveOffset = result.EmbeddedArchiveOffset;
            task.EmbeddedArchiveEnd = result.EmbeddedArchiveEnd;
            task.EmbeddedDirectReadSupported = result.EmbeddedDirectReadSupported;
            task.EmbeddedDirectReadReason = result.EmbeddedDirectReadReason ?? string.Empty;

            if (result.IsArchive)
            {
                task.Status = StatusText.Recognized;
                task.Operation = StatusText.OpWaiting;
                task.ProgressText = StatusText.ProgressCompleted;
                task.ErrorMessage = string.Empty;
            }
            else
            {
                task.Status = StatusText.UnknownFormat;
                task.Operation = StatusText.OpWaiting;
                task.ProgressText = StatusText.ProgressCompleted;
                task.ErrorMessage = result.Message;
            }

            task.LastUpdatedTime = DateTime.Now;

            return result;
        }

        /// <summary>
        /// 获取后缀状态。
        /// 
        /// 规则：
        /// 如果是内嵌归档（尾部藏着 ZIP）：
        ///     内嵌归档（**这一条最先判**：这种文件不该改名）
        /// 如果 Unknown：
        ///     格式未知
        /// 如果没有后缀：
        ///     后缀缺失
        /// 如果最后后缀等于建议后缀：
        ///     后缀正常
        /// 如果存在多个后缀，并且其中一个后缀等于建议后缀，但最后后缀不等于建议后缀：
        ///     多重后缀疑似伪装
        /// 否则：
        ///     后缀不匹配
        /// </summary>
        public string GetExtensionStatus(
            string fileName,
            string currentExtension,
            DetectResult result)
        {
            /*
             * 内嵌归档排在最前面判，而且**优先于一切后缀结论**。
             *
             * 理由：这种文件"没有可修正的后缀"。它的 ZIP 内部偏移相对它自己，前置数据又远超
             * 7-Zip 的容忍上限（实测 8 MiB），所以改成 .zip 之后 7z 依然打不开；
             * 报成"后缀缺失 / 不匹配 / 多重伪装"都会把用户引向改名这条路 —— 那是一条死路。
             * 它该看到的是"内嵌归档：需要按偏移取出"，所以这一条必须在其它判断之前返回。
             */
            if (result != null && result.EmbeddedArchiveOffset > 0)
            {
                return StatusText.ExtensionEmbedded;
            }

            if (result == null || !result.IsArchive || !result.IsKnownFormat)
            {
                return StatusText.UnknownFormat;
            }

            /*
             * 分卷文件的"后缀"是 .001 / .z01 / .part1 这类卷标记，不是伪装，也不是漏写后缀。
             * 设计.md §七 要求把它单独归一类；报成"多重后缀疑似伪装"会误导用户去"修正"它，
             * 而修正分卷名会直接切断分卷链。
             */
            if (FileNameHelper.IsVolumePartFileName(fileName))
            {
                return StatusText.ExtensionVolume;
            }

            string suggestedExtension = ExtensionHelper.NormalizeExtension(result.SuggestedExtension);

            if (string.IsNullOrWhiteSpace(suggestedExtension))
            {
                return StatusText.UnknownFormat;
            }

            if (string.IsNullOrWhiteSpace(currentExtension) ||
                currentExtension == "无")
            {
                return StatusText.ExtensionMissing;
            }

            currentExtension = ExtensionHelper.NormalizeExtension(currentExtension);

            /*
             * 本身就是 ZIP 容器的合法后缀（.apk / .jar / .docx / .xlsx / .epub …）：
             * 内容是 ZIP，但后缀本来就是对的 —— 不许判成"后缀不匹配"。
             * 否则「智能修正后缀」会把 xxx.apk 改成 xxx.zip，把安装包变成打不开的压缩包。
             */
            if (IsZipFamilyFormat(result.Format) &&
                ExtensionHelper.IsZipContainerExtension(currentExtension))
            {
                return StatusText.ExtensionNormal;
            }

            if (string.Equals(
                    currentExtension,
                    suggestedExtension,
                    StringComparison.OrdinalIgnoreCase))
            {
                return StatusText.ExtensionNormal;
            }

            if (IsMultiExtensionSuspicious(fileName, suggestedExtension))
            {
                return StatusText.ExtensionMultiFake;
            }

            return StatusText.ExtensionMismatch;
        }

        /// <summary>ZIP 家族（含空 ZIP 与分卷 ZIP）—— 它们的容器都是 ZIP 结构。</summary>
        private static bool IsZipFamilyFormat(string? format)
        {
            return format is "ZIP" or "ZIP_EMPTY" or "ZIP_SPANNED";
        }

        /// <summary>
        /// 判断是否是多重后缀疑似伪装。
        /// 例如：
        /// 111.7z.jpg 检测为 7Z，则 true。
        /// 111.rar.pdf.jpg 检测为 RAR，则 true。
        /// </summary>
        public bool IsMultiExtensionSuspicious(
            string fileName,
            string suggestedExtension)
        {
            return FileNameHelper.IsMultiExtensionSuspicious(fileName, suggestedExtension);
        }

        /// <summary>
        /// 根据格式获取建议后缀。
        /// </summary>
        public string GetSuggestedExtension(string format)
        {
            return ExtensionHelper.GetSuggestedExtensionByFormat(format);
        }

        /// <summary>
        /// 根据文件头识别格式。
        /// </summary>
        private DetectResult DetectByHeader(byte[] header)
        {
            if (header == null || header.Length == 0)
            {
                return CreateUnknownResult("文件头为空");
            }

            string headerHex = SafePathHelper.ToHexString(header, 64);

            // 7Z:
            // 37 7A BC AF 27 1C
            if (StartsWith(header, 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C))
            {
                return new DetectResult
                {
                    Format = "7Z",
                    SuggestedExtension = ".7z",
                    IsArchive = true,
                    IsKnownFormat = true,
                    IsProbablyEncrypted = false,
                    Message = "识别为 7Z 压缩包",
                    HeaderHex = headerHex,
                    Confidence = 100
                };
            }

            // RAR4:
            // 52 61 72 21 1A 07 00
            if (StartsWith(header, 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00))
            {
                return new DetectResult
                {
                    Format = "RAR4",
                    SuggestedExtension = ".rar",
                    IsArchive = true,
                    IsKnownFormat = true,
                    IsProbablyEncrypted = false,
                    Message = "识别为 RAR4 压缩包",
                    HeaderHex = headerHex,
                    Confidence = 100
                };
            }

            // RAR5:
            // 52 61 72 21 1A 07 01 00
            if (StartsWith(header, 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x01, 0x00))
            {
                return new DetectResult
                {
                    Format = "RAR5",
                    SuggestedExtension = ".rar",
                    IsArchive = true,
                    IsKnownFormat = true,
                    IsProbablyEncrypted = false,
                    Message = "识别为 RAR5 压缩包",
                    HeaderHex = headerHex,
                    Confidence = 100
                };
            }

            // ZIP:
            // PK 03 04 普通 ZIP
            // PK 05 06 空 ZIP
            // PK 07 08 分卷或数据描述符相关
            if (StartsWith(header, 0x50, 0x4B, 0x03, 0x04))
            {
                return new DetectResult
                {
                    Format = "ZIP",
                    SuggestedExtension = ".zip",
                    IsArchive = true,
                    IsKnownFormat = true,
                    IsProbablyEncrypted = IsZipProbablyEncrypted(header),
                    Message = "识别为 ZIP 压缩包",
                    HeaderHex = headerHex,
                    Confidence = 100
                };
            }

            if (StartsWith(header, 0x50, 0x4B, 0x05, 0x06))
            {
                return new DetectResult
                {
                    Format = "ZIP_EMPTY",
                    SuggestedExtension = ".zip",
                    IsArchive = true,
                    IsKnownFormat = true,
                    IsProbablyEncrypted = false,
                    Message = "识别为空 ZIP 压缩包",
                    HeaderHex = headerHex,
                    Confidence = 100
                };
            }

            if (StartsWith(header, 0x50, 0x4B, 0x07, 0x08))
            {
                return new DetectResult
                {
                    Format = "ZIP_SPANNED",
                    SuggestedExtension = ".zip",
                    IsArchive = true,
                    IsKnownFormat = true,
                    IsProbablyEncrypted = IsZipProbablyEncrypted(header),
                    Message = "识别为 ZIP 分卷或特殊 ZIP",
                    HeaderHex = headerHex,
                    Confidence = 90
                };
            }

            // GZIP:
            // 1F 8B
            if (StartsWith(header, 0x1F, 0x8B))
            {
                return new DetectResult
                {
                    Format = "GZIP",
                    SuggestedExtension = ".gz",
                    IsArchive = true,
                    IsKnownFormat = true,
                    IsProbablyEncrypted = false,
                    Message = "识别为 GZIP 压缩包",
                    HeaderHex = headerHex,
                    Confidence = 100
                };
            }

            // BZIP2:
            // 42 5A 68 = BZh
            if (StartsWith(header, 0x42, 0x5A, 0x68))
            {
                return new DetectResult
                {
                    Format = "BZIP2",
                    SuggestedExtension = ".bz2",
                    IsArchive = true,
                    IsKnownFormat = true,
                    IsProbablyEncrypted = false,
                    Message = "识别为 BZIP2 压缩包",
                    HeaderHex = headerHex,
                    Confidence = 100
                };
            }

            // XZ:
            // FD 37 7A 58 5A 00
            if (StartsWith(header, 0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00))
            {
                return new DetectResult
                {
                    Format = "XZ",
                    SuggestedExtension = ".xz",
                    IsArchive = true,
                    IsKnownFormat = true,
                    IsProbablyEncrypted = false,
                    Message = "识别为 XZ 压缩包",
                    HeaderHex = headerHex,
                    Confidence = 100
                };
            }

            // TAR:
            // ustar 出现在偏移 257。
            if (IsTar(header))
            {
                return new DetectResult
                {
                    Format = "TAR",
                    SuggestedExtension = ".tar",
                    IsArchive = true,
                    IsKnownFormat = true,
                    IsProbablyEncrypted = false,
                    Message = "识别为 TAR 归档文件",
                    HeaderHex = headerHex,
                    Confidence = 90
                };
            }

            // CAB:
            // 4D 53 43 46 = MSCF
            if (StartsWith(header, 0x4D, 0x53, 0x43, 0x46))
            {
                return new DetectResult
                {
                    Format = "CAB",
                    SuggestedExtension = ".cab",
                    IsArchive = true,
                    IsKnownFormat = true,
                    IsProbablyEncrypted = false,
                    Message = "识别为 CAB 压缩包",
                    HeaderHex = headerHex,
                    Confidence = 100
                };
            }

            // ARJ:
            // 60 EA
            if (StartsWith(header, 0x60, 0xEA))
            {
                return new DetectResult
                {
                    Format = "ARJ",
                    SuggestedExtension = ".arj",
                    IsArchive = true,
                    IsKnownFormat = true,
                    IsProbablyEncrypted = false,
                    Message = "识别为 ARJ 压缩包",
                    HeaderHex = headerHex,
                    Confidence = 100
                };
            }

            // LZH:
            // 2D 6C 68 = -lh
            if (StartsWith(header, 0x2D, 0x6C, 0x68))
            {
                return new DetectResult
                {
                    Format = "LZH",
                    SuggestedExtension = ".lzh",
                    IsArchive = true,
                    IsKnownFormat = true,
                    IsProbablyEncrypted = false,
                    Message = "识别为 LZH 压缩包",
                    HeaderHex = headerHex,
                    Confidence = 100
                };
            }

            // Z（Unix compress）:
            // 1F 9D
            if (StartsWith(header, 0x1F, 0x9D))
            {
                return new DetectResult
                {
                    Format = "Z",
                    SuggestedExtension = ".z",
                    IsArchive = true,
                    IsKnownFormat = true,
                    IsProbablyEncrypted = false,
                    Message = "识别为 Z 压缩包",
                    HeaderHex = headerHex,
                    Confidence = 100
                };
            }

            // Zstandard:
            // 28 B5 2F FD
            if (StartsWith(header, 0x28, 0xB5, 0x2F, 0xFD))
            {
                return new DetectResult
                {
                    Format = "ZSTD",
                    SuggestedExtension = ".zst",
                    IsArchive = true,
                    IsKnownFormat = true,
                    IsProbablyEncrypted = false,
                    Message = "识别为 Zstandard 压缩包",
                    HeaderHex = headerHex,
                    Confidence = 100
                };
            }

            // LZ4:
            // 04 22 4D 18
            if (StartsWith(header, 0x04, 0x22, 0x4D, 0x18))
            {
                return new DetectResult
                {
                    Format = "LZ4",
                    SuggestedExtension = ".lz4",
                    IsArchive = true,
                    IsKnownFormat = true,
                    IsProbablyEncrypted = false,
                    Message = "识别为 LZ4 压缩包",
                    HeaderHex = headerHex,
                    Confidence = 100
                };
            }

            // RPM:
            // ED AB EE DB
            if (StartsWith(header, 0xED, 0xAB, 0xEE, 0xDB))
            {
                return new DetectResult
                {
                    Format = "RPM",
                    SuggestedExtension = ".rpm",
                    IsArchive = true,
                    IsKnownFormat = true,
                    IsProbablyEncrypted = false,
                    Message = "识别为 RPM 软件包",
                    HeaderHex = headerHex,
                    Confidence = 100
                };
            }

            // ISO 9660:
            // 卷描述符 "CD001" 位于偏移 0x8001（扇区 16 起始处）。
            if (IsIso9660(header))
            {
                return new DetectResult
                {
                    Format = "ISO",
                    SuggestedExtension = ".iso",
                    IsArchive = true,
                    IsKnownFormat = true,
                    IsProbablyEncrypted = false,
                    Message = "识别为 ISO 光盘镜像",
                    HeaderHex = headerHex,
                    Confidence = 90
                };
            }

            return new DetectResult
            {
                Format = "Unknown",
                SuggestedExtension = string.Empty,
                IsArchive = false,
                IsKnownFormat = false,
                IsProbablyEncrypted = false,
                Message = "未识别为支持的压缩格式",
                HeaderHex = headerHex,
                Confidence = 0
            };
        }

        /// <summary>
        /// 安全读取文件头。
        /// </summary>
        private static async Task<byte[]> ReadHeaderAsync(
            string filePath,
            int length,
            CancellationToken cancellationToken)
        {
            byte[] buffer = new byte[length];

            await using var stream = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                bufferSize: 4096,
                useAsync: true);

            int read = await stream.ReadAsync(buffer.AsMemory(0, length), cancellationToken);

            if (read <= 0)
            {
                return Array.Empty<byte>();
            }

            if (read == length)
            {
                return buffer;
            }

            return buffer.Take(read).ToArray();
        }

        /// <summary>
        /// 判断文件头是否以指定字节开头。
        /// </summary>
        private static bool StartsWith(byte[] data, params byte[] signature)
        {
            if (data == null || signature == null)
            {
                return false;
            }

            if (data.Length < signature.Length)
            {
                return false;
            }

            for (int i = 0; i < signature.Length; i++)
            {
                if (data[i] != signature[i])
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// 判断 TAR。
        /// TAR 的 ustar 标记通常位于偏移 257。
        /// </summary>
        private static bool IsTar(byte[] header)
        {
            if (header == null || header.Length < 262)
            {
                return false;
            }

            return header[257] == (byte)'u' &&
                   header[258] == (byte)'s' &&
                   header[259] == (byte)'t' &&
                   header[260] == (byte)'a' &&
                   header[261] == (byte)'r';
        }

        /// <summary>
        /// 判断是否为 ISO 9660 镜像。
        /// 主卷描述符的 "CD001" 标记位于文件偏移 0x8001。
        /// </summary>
        private static bool IsIso9660(byte[] header)
        {
            if (header == null || header.Length < 0x8001 + 5)
            {
                return false;
            }

            return header[0x8001] == (byte)'C' &&
                   header[0x8002] == (byte)'D' &&
                   header[0x8003] == (byte)'0' &&
                   header[0x8004] == (byte)'0' &&
                   header[0x8005] == (byte)'1';
        }

        /// <summary>
        /// 粗略判断 ZIP 是否可能加密。
        /// 
        /// ZIP Local File Header:
        /// offset 6-7 为 general purpose bit flag。
        /// bit 0 = encrypted。
        /// 
        /// 注意：
        /// 这里只是基于文件头的初步判断，最终仍应以 7-Zip 测试结果为准。
        /// </summary>
        private static bool IsZipProbablyEncrypted(byte[] header)
        {
            if (header == null || header.Length < 8)
            {
                return false;
            }

            if (!StartsWith(header, 0x50, 0x4B, 0x03, 0x04) &&
                !StartsWith(header, 0x50, 0x4B, 0x07, 0x08))
            {
                return false;
            }

            ushort generalPurposeBitFlag = BitConverter.ToUInt16(header, 6);

            return (generalPurposeBitFlag & 0x0001) != 0;
        }

        /// <summary>
        /// 创建 Unknown 结果。
        /// </summary>
        private static DetectResult CreateUnknownResult(string message)
        {
            return new DetectResult
            {
                Format = "Unknown",
                SuggestedExtension = string.Empty,
                IsArchive = false,
                IsKnownFormat = false,
                IsProbablyEncrypted = false,
                Message = message ?? StatusText.UnknownFormat,
                HeaderHex = string.Empty,
                Confidence = 0
            };
        }
    }
}
