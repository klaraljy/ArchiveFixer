using System;
using System.IO;
using ArchiveFixer.Services;
using ArchiveFixer.Storage;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 对当前机器上真实用户数据做**只读副本**兼容性冒烟：不打印、不修改真实密码值。
    /// 没有真实数据的机器直接视为通过（测试不能依赖某台开发机）。
    /// </summary>
    [Collection("ArchiveFixerGlobalState")]
    public sealed class RealUserDataCompatibilityTests : IDisposable
    {
        private readonly string _root;

        public RealUserDataCompatibilityTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "af-real-data-copy-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { }
        }

        [Fact]
        public void 真实密码本副本能读入且元数据不丢()
        {
            string realRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ArchiveFixer");

            string realPasswords = Path.Combine(realRoot, "passwords.txt");

            if (!File.Exists(realPasswords))
            {
                FixtureGate.Skip("RealUserDataCompatibilityTests.cs", "前置条件不满足：夹具或运行环境缺失");
                 return;
            }

            foreach (string name in new[]
                     {
                         "passwords.txt",
                         "passwords.txt.bak",
                         "passwords.meta.json",
                         "passwords.meta.json.bak",
                         "appsettings.json",
                         "password-list.dat"
                     })
            {
                string source = Path.Combine(realRoot, name);
                if (File.Exists(source))
                {
                    File.Copy(source, Path.Combine(_root, name), overwrite: true);
                }
            }

            var service = new PasswordService
            {
                DataRootDirectory = _root,
                UseTextStore = true
            };
            service.TextStore.EncryptAtRest = true;

            PasswordListLoadStatus status = service.LoadRememberedList();

            Assert.Equal(PasswordListLoadStatus.Loaded, status);
            Assert.NotEmpty(service.Passwords);
        }
    }
}
