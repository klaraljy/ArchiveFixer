using System;
using System.Drawing;
using System.Windows;
using System.Windows.Forms;

namespace ArchiveFixer.Services
{
    /// <summary>
    /// 主窗口的托盘图标：X 关闭只隐藏到托盘，左键恢复，右键菜单才有“退出”。
    /// </summary>
    public sealed class TrayIconService : IDisposable
    {
        private readonly Window _window;
        private readonly NotifyIcon _notifyIcon;
        private WindowState _lastWindowState;
        private bool _hidden;

        public TrayIconService(Window window)
        {
            _window = window ?? throw new ArgumentNullException(nameof(window));
            _lastWindowState = window.WindowState == WindowState.Minimized
                ? WindowState.Normal
                : window.WindowState;

            Icon icon = TryLoadIcon();
            var menu = new ContextMenuStrip();
            var openItem = new ToolStripMenuItem("打开 ArchiveFixer");
            var exitItem = new ToolStripMenuItem("退出");

            openItem.Click += (_, _) => Dispatch(Restore);
            exitItem.Click += (_, _) => Dispatch(() => ExitRequested?.Invoke());
            menu.Items.Add(openItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(exitItem);

            _notifyIcon = new NotifyIcon
            {
                Icon = icon,
                Text = "ArchiveFixer",
                Visible = true,
                ContextMenuStrip = menu
            };
            _notifyIcon.MouseClick += (_, args) =>
            {
                if (args.Button == MouseButtons.Left)
                {
                    Dispatch(Restore);
                }
            };
        }

        /// <summary>用户在托盘菜单点了“退出”。</summary>
        public event Action? ExitRequested;

        public void HideToTray()
        {
            if (_hidden)
            {
                return;
            }

            _lastWindowState = _window.WindowState == WindowState.Minimized
                ? WindowState.Normal
                : _window.WindowState;
            _hidden = true;
            _window.ShowInTaskbar = false;
            _window.Hide();
        }

        public void Restore()
        {
            if (!_hidden)
            {
                if (_window.WindowState == WindowState.Minimized)
                {
                    _window.WindowState = WindowState.Normal;
                }

                _window.Activate();
                return;
            }

            _hidden = false;
            _window.ShowInTaskbar = true;
            _window.Show();

            if (_lastWindowState == WindowState.Maximized)
            {
                _window.WindowState = WindowState.Maximized;
            }
            else
            {
                _window.WindowState = WindowState.Normal;
            }

            _window.Activate();
        }

        public void Dispose()
        {
            try
            {
                _notifyIcon.Visible = false;
                _notifyIcon.Dispose();
            }
            catch
            {
            }
        }

        private void Dispatch(Action? action)
        {
            if (action == null)
            {
                return;
            }

            System.Windows.Application? application = System.Windows.Application.Current;

            if (application == null)
            {
                action();
                return;
            }

            application.Dispatcher.BeginInvoke(action);
        }

        private static Icon TryLoadIcon()
        {
            try
            {
                string? executable = Environment.ProcessPath;

                if (!string.IsNullOrWhiteSpace(executable))
                {
                    Icon? icon = Icon.ExtractAssociatedIcon(executable);

                    if (icon != null)
                    {
                        return icon;
                    }
                }
            }
            catch
            {
            }

            return SystemIcons.Application;
        }
    }
}
