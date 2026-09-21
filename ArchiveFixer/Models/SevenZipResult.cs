using System;

namespace ArchiveFixer.Models
{
    public class SevenZipResult
    {
        public bool Success { get; set; }

        public int ExitCode { get; set; }

        public string StandardOutput { get; set; } = string.Empty;

        public string StandardError { get; set; } = string.Empty;

        public string Status { get; set; } = string.Empty;

        public string Message { get; set; } = string.Empty;

        public string DetectedErrorType { get; set; } = "None";

        public string UsedPasswordMasked { get; set; } = string.Empty;

        public TimeSpan Elapsed { get; set; } = TimeSpan.Zero;

        public string CombinedOutput
        {
            get
            {
                if (string.IsNullOrWhiteSpace(StandardOutput))
                {
                    return StandardError ?? string.Empty;
                }

                if (string.IsNullOrWhiteSpace(StandardError))
                {
                    return StandardOutput ?? string.Empty;
                }

                return StandardOutput + Environment.NewLine + StandardError;
            }
        }

        public bool IsWrongPassword => DetectedErrorType == "WrongPassword";

        public bool IsNeedPassword => DetectedErrorType == "NeedPassword";

        public bool IsCorrupted => DetectedErrorType == "CorruptedArchive";

        public bool IsSevenZipMissing => DetectedErrorType == "SevenZipMissing";

        public bool IsCancelled => DetectedErrorType == "Cancelled";

        public bool IsTimedOut => DetectedErrorType == "TimedOut";

        public static SevenZipResult CreateSuccess(
            int exitCode,
            string output,
            string error,
            TimeSpan elapsed,
            string usedPasswordMasked = "")
        {
            return new SevenZipResult
            {
                Success = true,
                ExitCode = exitCode,
                StandardOutput = output ?? string.Empty,
                StandardError = error ?? string.Empty,
                Status = StatusText.Success,
                Message = "操作成功",
                DetectedErrorType = "None",
                UsedPasswordMasked = usedPasswordMasked ?? string.Empty,
                Elapsed = elapsed
            };
        }

        public static SevenZipResult CreateFailure(
            int exitCode,
            string output,
            string error,
            string status,
            string message,
            string detectedErrorType,
            TimeSpan elapsed,
            string usedPasswordMasked = "")
        {
            return new SevenZipResult
            {
                Success = false,
                ExitCode = exitCode,
                StandardOutput = output ?? string.Empty,
                StandardError = error ?? string.Empty,
                Status = string.IsNullOrWhiteSpace(status) ? StatusText.ProgressFailed : status,
                Message = message ?? string.Empty,
                DetectedErrorType = string.IsNullOrWhiteSpace(detectedErrorType)
                    ? "UnknownError"
                    : detectedErrorType,
                UsedPasswordMasked = usedPasswordMasked ?? string.Empty,
                Elapsed = elapsed
            };
        }

        public static SevenZipResult CreateSevenZipMissing(string path)
        {
            return new SevenZipResult
            {
                Success = false,
                ExitCode = -1,
                StandardOutput = string.Empty,
                StandardError = string.Empty,
                Status = StatusText.SevenZipMissing,
                Message = $"未找到 7-Zip 程序：{path}",
                DetectedErrorType = "SevenZipMissing",
                UsedPasswordMasked = string.Empty,
                Elapsed = TimeSpan.Zero
            };
        }

        public static SevenZipResult CreateCancelled(TimeSpan elapsed)
        {
            return new SevenZipResult
            {
                Success = false,
                ExitCode = 255,
                StandardOutput = string.Empty,
                StandardError = string.Empty,
                Status = StatusText.Cancelled,
                Message = "用户取消当前任务",
                DetectedErrorType = "Cancelled",
                UsedPasswordMasked = string.Empty,
                Elapsed = elapsed
            };
        }

        public static SevenZipResult CreateTimedOut(TimeSpan elapsed)
        {
            return new SevenZipResult
            {
                Success = false,
                ExitCode = -3,
                StandardOutput = string.Empty,
                StandardError = string.Empty,
                Status = StatusText.UnknownError,
                Message = "7-Zip 执行超时",
                DetectedErrorType = "TimedOut",
                UsedPasswordMasked = string.Empty,
                Elapsed = elapsed
            };
        }

        public override string ToString()
        {
            return $"{Status}，ExitCode={ExitCode}，ErrorType={DetectedErrorType}，Elapsed={Elapsed:hh\\:mm\\:ss}";
        }
    }
}
