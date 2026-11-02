using System;
using ArchiveFixer.Extraction;
using ArchiveFixer.Models;
using Xunit;

namespace ArchiveFixer.Tests
{
    public class TaskReasonTests
    {
        [Fact]
        public void VolumeMissingStatusWithoutReason_DoesNotBecomePartiallyCompleted()
        {
            var task = new ArchiveTask
            {
                Status = StatusText.VolumeMissing,
                EndTime = DateTime.Now,
                Outcome = TaskOutcome.Pending
            };

            OutcomeFinalizer.FinalizeIfPending(task, producedContent: false);

            Assert.Equal(TaskOutcome.Failed, task.Outcome);
        }

        [Fact]
        public void VolumeMissingReason_BecomesPartiallyCompleted()
        {
            var task = new ArchiveTask
            {
                Status = StatusText.ExtractSuccess,
                Reason = TaskReason.VolumeMissing,
                EndTime = DateTime.Now,
                Outcome = TaskOutcome.Pending
            };

            OutcomeFinalizer.FinalizeIfPending(task, producedContent: false);

            Assert.Equal(TaskOutcome.PartiallyCompleted, task.Outcome);
        }
    }
}
