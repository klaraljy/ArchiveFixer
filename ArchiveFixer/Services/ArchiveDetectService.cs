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
    /// 3. 返回 DetectResult。
    /// 4. 判断后缀状态。
    /// 5. 将识别结果应用到 ArchiveTask。
    /// 
    /// 注意：
    /// 这里只读取文件头，不读取整个大文件。
    /// </summary>
    public class ArchiveDetectService
    {
        private const int HeaderReadLength = 1024;

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

                return DetectByHeader(header);
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

            task.Operation = "扫描";
            task.Status = "扫描中";
            task.ProgressText = "处理中";
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

            if (result.IsArchive)
            {
                task.Status = "已识别";
                task.Operation = "等待";
                task.ProgressText = "完成";
                task.ErrorMessage = string.Empty;
            }
            else
            {
                task.Status = "格式未知";
                task.Operation = "等待";
                task.ProgressText = "完成";
                task.ErrorMessage = result.Message;
            }

            task.LastUpdatedTime = DateTime.Now;

            return result;
        }

        /// <summary>
        /// 获取后缀状态。
        /// 
        /// 规则：
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
            if (result == null || !result.IsArchive || !result.IsKnownFormat)
            {
                return "格式未知";
            }

            string suggestedExtension = ExtensionHelper.NormalizeExtension(result.SuggestedExtension);

            if (string.IsNullOrWhiteSpace(suggestedExtension))
            {
                return "格式未知";
            }

            if (string.IsNullOrWhiteSpace(currentExtension) ||
                currentExtension == "无")
            {
                return "后缀缺失";
            }

            currentExtension = ExtensionHelper.NormalizeExtension(currentExtension);

            if (string.Equals(
                    currentExtension,
                    suggestedExtension,
                    StringComparison.OrdinalIgnoreCase))
            {
                return "后缀正常";
            }

            if (IsMultiExtensionSuspicious(fileName, suggestedExtension))
            {
                return "多重后缀疑似伪装";
            }

            return "后缀不匹配";
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
                Message = message ?? "格式未知",
                HeaderHex = string.Empty,
                Confidence = 0
            };
        }
    }
}
