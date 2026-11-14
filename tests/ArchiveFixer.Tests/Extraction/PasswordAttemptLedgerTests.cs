using ArchiveFixer.Password;
using Xunit;

namespace ArchiveFixer.Tests
{
    public class PasswordAttemptLedgerTests
    {
        [Fact]
        public void SkipDoesNotConsumeAttemptAndTryStopsAtLimit()
        {
            var ledger = new PasswordAttemptLedger(maxAttempts: 2);

            ledger.Skip("空密码：已加密");
            ledger.Skip("加密头");

            Assert.Equal(0, ledger.Tried);
            Assert.Equal(2, ledger.Skipped);
            Assert.True(ledger.CanTry);

            Assert.True(ledger.Try());
            Assert.True(ledger.Try());
            Assert.False(ledger.Try());

            Assert.Equal(2, ledger.Tried);
            Assert.Equal(2, ledger.Skipped);
            Assert.False(ledger.CanTry);
        }

        [Fact]
        public void NotCountedIsSeparateFromSkippedAndTried()
        {
            var ledger = new PasswordAttemptLedger(maxAttempts: 1);

            ledger.NotCount("引擎不可用");
            ledger.Skip("空密码");

            Assert.Equal(0, ledger.Tried);
            Assert.Equal(1, ledger.Skipped);
            Assert.Equal(1, ledger.NotCounted);
            Assert.Single(ledger.NotCountedReasons);
            Assert.Single(ledger.SkippedReasons);
        }
    }
}
