using System;
using System.Linq;
using ArchiveFixer.Engines;
using Xunit;

namespace ArchiveFixer.Tests
{
    public class ExtractionAttemptTests
    {
        [Fact]
        public void FromResult_MapsMachineFactsAndShadowMatches()
        {
            var result = new ArchiveOperationResult
            {
                Success = true,
                ExitCode = 7,
                EngineId = "7z",
                CommandSummary = "7z x -p****** a.7z out",
                Status = "解压成功",
                Message = "ok",
                DetectedErrorType = "None",
                UsedPasswordMasked = "******",
                Elapsed = TimeSpan.FromSeconds(3),
                FailedEntryNames = new[] { "a.txt" }
            };

            ExtractionAttempt attempt = ExtractionAttempt.FromResult(
                result,
                ExtractionAttemptKind.Extract,
                archivePath: @"C:\in\a.7z",
                outputDirectory: @"C:\out",
                isolatedDirectory: @"C:\out\isolated",
                outputFileCount: 2,
                outputBytes: 10);

            Assert.Equal(ExtractionAttemptKind.Extract, attempt.Kind);
            Assert.Equal(7, attempt.ExitCode);
            Assert.Equal("7z", attempt.EngineId);
            Assert.Equal(2, attempt.OutputFileCount);
            Assert.Equal(10, attempt.OutputBytes);
            Assert.Equal(@"C:\out\isolated", attempt.IsolatedDirectory);
            Assert.True(attempt.HasSideEffects);
            Assert.Single(attempt.FailedEntryNames);
            Assert.Empty(ExtractionAttempt.DescribeDifferences(result, attempt));
        }

        [Fact]
        public void ShadowCompare_ReportsExitAndStatusDifferences()
        {
            var result = new ArchiveOperationResult
            {
                ExitCode = 2,
                Status = "失败",
                DetectedErrorType = "WrongPassword"
            };

            ExtractionAttempt attempt = ExtractionAttempt.FromResult(
                result,
                ExtractionAttemptKind.Test);

            var changed = new ArchiveOperationResult
            {
                ExitCode = 1,
                Status = "部分完成",
                DetectedErrorType = "Corrupted"
            };

            string[] differences = ExtractionAttempt
                .DescribeDifferences(changed, attempt)
                .ToArray();

            Assert.Contains(differences, line => line.StartsWith("ExitCode:", StringComparison.Ordinal));
            Assert.Contains(differences, line => line.StartsWith("Status:", StringComparison.Ordinal));
            Assert.Contains(differences, line => line.StartsWith("ErrorType:", StringComparison.Ordinal));
        }
    }
}
