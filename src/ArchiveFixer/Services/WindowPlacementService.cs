using System;
using System.IO;
using System.Text.Json;
using System.Windows;

namespace ArchiveFixer.Services
{
    /// <summary>
    /// 主窗口大小/位置记忆。只保存 WPF 的 DIP 坐标；损坏或屏幕外时回到默认居中/自适应。
    /// </summary>
    internal sealed class WindowPlacementService
    {
        internal const string FileName = "window-placement.json";

        private readonly string _path;

        internal WindowPlacementService(string dataRootDirectory)
        {
            _path = Path.Combine(dataRootDirectory, FileName);
        }

        internal Placement? Load()
        {
            try
            {
                if (!File.Exists(_path))
                {
                    return null;
                }

                Placement? placement = JsonSerializer.Deserialize<Placement>(File.ReadAllText(_path));

                if (placement == null
                    || placement.Width < 320
                    || placement.Height < 240
                    || double.IsNaN(placement.Left)
                    || double.IsNaN(placement.Top))
                {
                    return null;
                }

                return placement;
            }
            catch
            {
                return null;
            }
        }

        internal void Save(Window window)
        {
            try
            {
                Rect bounds = window.WindowState == WindowState.Maximized
                    ? window.RestoreBounds
                    : new Rect(window.Left, window.Top, window.Width, window.Height);

                if (bounds.Width < 320 || bounds.Height < 240)
                {
                    return;
                }

                var placement = new Placement
                {
                    Left = bounds.Left,
                    Top = bounds.Top,
                    Width = bounds.Width,
                    Height = bounds.Height,
                    Maximized = window.WindowState == WindowState.Maximized
                };

                string? directory = Path.GetDirectoryName(_path);

                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                string temp = _path + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(placement));
                File.Move(temp, _path, overwrite: true);
            }
            catch
            {
                // 窗口位置保存失败不影响退出。
            }
        }

        internal static bool IsVisibleOnScreen(Placement placement)
        {
            Rect virtualScreen = new(
                SystemParameters.VirtualScreenLeft,
                SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenWidth,
                SystemParameters.VirtualScreenHeight);

            Rect bounds = new(placement.Left, placement.Top, placement.Width, placement.Height);

            return virtualScreen.IntersectsWith(bounds)
                   && bounds.Right > virtualScreen.Left + 80
                   && bounds.Bottom > virtualScreen.Top + 80;
        }

        internal sealed class Placement
        {
            public double Left { get; set; }

            public double Top { get; set; }

            public double Width { get; set; }

            public double Height { get; set; }

            public bool Maximized { get; set; }
        }
    }
}
