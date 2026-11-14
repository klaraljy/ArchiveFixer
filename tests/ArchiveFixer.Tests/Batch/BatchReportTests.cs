using System;
using System.Collections.Generic;
using ArchiveFixer.Extraction;
using ArchiveFixer.Models;
using Xunit;

namespace ArchiveFixer.Tests
{
    public class BatchReportTests
    {
        [Fact]
        public void TallyPartsSumToTotalAndSeverityComesFromSameTally()
        {
            var tasks = new List<ArchiveTask>
            {
                new() { Outcome = TaskOutcome.Succeeded, OutputVerification = OutputVerificationOutcome.Passed },
                new() { Outcome = TaskOutcome.Failed },
                new() { Outcome = TaskOutcome.Skipped },
                new() { Outcome = TaskOutcome.Pending }
            };

            BatchReport report = BatchReport.Create(tasks);
            BatchOutcomeTally tally = report.Tally;

            Assert.Equal(tally.Total, tally.Succeeded + tally.Failed + tally.Skipped
                + tally.FollowerSkipped + tally.PartiallyCompleted + tally.Cancelled + tally.Untouched);
            Assert.Equal(BatchSummarySeverity.Partial, report.Severity);
        }

        [Fact]
        public void VerificationFailureKeepsSeverityRedEvenWithContent()
        {
            var tasks = new List<ArchiveTask>
            {
                new() { Outcome = TaskOutcome.Succeeded, OutputVerification = OutputVerificationOutcome.Passed },
                new() { Outcome = TaskOutcome.Succeeded, OutputVerification = OutputVerificationOutcome.Failed }
            };

            BatchReport report = BatchReport.Create(tasks);

            Assert.True(report.Tally.HasVerificationFailure);
            Assert.Equal(BatchSummarySeverity.Failed, report.Severity);
        }
    }
}
