using System;
using System.IO;
using ArchiveFixer.Services;
using Xunit;

namespace ArchiveFixer.Tests
{
    [Collection("ArchiveFixerGlobalState")]
    public sealed class WindowPlacementServiceTests : IDisposable
    {
        private readonly string _root;

        public WindowPlacementServiceTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "af-placement-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { }
        }

        [Fact]
        public void 损坏或小到不可用的窗口位置不恢复()
        {
            File.WriteAllText(
                Path.Combine(_root, WindowPlacementService.FileName),
                "{\"Left\":10,\"Top\":10,\"Width\":200,\"Height\":100,\"Maximized\":false}");

            var service = new WindowPlacementService(_root);
            Assert.Null(service.Load());
        }

        [Fact]
        public void 屏幕外位置不恢复()
        {
            var placement = new WindowPlacementService.Placement
            {
                Left = -50000,
                Top = -50000,
                Width = 1000,
                Height = 700
            };

            Assert.False(WindowPlacementService.IsVisibleOnScreen(placement));
        }

        [Fact]
        public void 屏幕内位置保留()
        {
            var placement = new WindowPlacementService.Placement
            {
                Left = 20,
                Top = 20,
                Width = 1000,
                Height = 700
            };

            Assert.True(WindowPlacementService.IsVisibleOnScreen(placement));
        }
    }
}
