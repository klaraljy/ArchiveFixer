using ArchiveFixer.Engines;
using ArchiveFixer.Extraction;
using ArchiveFixer.Models;
using Xunit;

namespace ArchiveFixer.Tests
{
    public sealed class RecursionPasswordStatusTests
    {
        [Fact]
        public void 递归层用过非空密码成功_落密码正确()
        {
            var task = new ArchiveTask(@"H:\a.7z.001", 1);
            var result = new RecursionResult
            {
                Layers = new[]
                {
                    new RecursionLayerReport
                    {
                        Success = true,
                        UsedPasswordMasked = "******"
                    }
                }
            };

            RecursionPasswordStatus.Apply(task, result);

            Assert.Equal(StatusText.PasswordCorrect, task.PasswordStatus);
        }

        [Fact]
        public void 递归层无加密证据_落不需要密码()
        {
            var task = new ArchiveTask(@"H:\a.7z", 1);
            var result = new RecursionResult
            {
                Layers = new[]
                {
                    new RecursionLayerReport
                    {
                        Success = true,
                        ListingSaidNoEncryption = true
                    }
                }
            };

            RecursionPasswordStatus.Apply(task, result);

            Assert.Equal(StatusText.PasswordNotNeeded, task.PasswordStatus);
        }

        [Fact]
        public void 递归层密码类失败_落密码错误()
        {
            var task = new ArchiveTask(@"H:\a.7z", 1);
            var result = new RecursionResult
            {
                Layers = new[]
                {
                    new RecursionLayerReport
                    {
                        Success = false,
                        DetectedErrorType = EngineErrorTypes.WrongPassword
                    }
                }
            };

            RecursionPasswordStatus.Apply(task, result);

            Assert.Equal(StatusText.WrongPassword, task.PasswordStatus);
        }

        [Fact]
        public void 跟班卷和缺卷显示不适用而不是未检测()
        {
            var follower = new ArchiveTask(@"H:\a.7z.002", 2)
            {
                IsVolumeGroupFollower = true,
                Status = StatusText.Skipped
            };

            Assert.Equal("跟班卷", follower.PasswordStatusDisplay);

            var missing = new ArchiveTask(@"H:\a.7z.003", 3)
            {
                Status = StatusText.VolumeMissing
            };

            Assert.Equal(StatusText.PasswordNotApplicable, missing.PasswordStatusDisplay);
        }
    }
}
