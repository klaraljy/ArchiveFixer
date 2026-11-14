using ArchiveFixer.Models;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ArchiveFixer.Views;

/// <summary>
/// 「改名…」小窗的真实实现。窗口创建/控件布局在 Views；ViewModel 只拿结果。
/// </summary>
internal static class RenameWindowFactory
{
    public static RenameDialogResult? Show(string defaultExtension)
    {
        var window = new Window
        {
            Title = "改名",
            Width = 560,
            MinWidth = 460,
            MaxWidth = 780,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.CanResize,
            Owner = Application.Current?.MainWindow
        };

        window.SetResourceReference(FrameworkElement.StyleProperty, "AppWindowStyle");

        /*
         * ⛔ 模态子窗一律不许最小化（用户 2026-10-05 拍板 A2；口径与 8 个 XAML 子窗同一份策略）。
         * 这个框是**代码里内联建的**，不在 XAML 名单里，漏了它就等于留着"最小化以后任务栏没有按钮、
         * 主窗口又被模态禁用 ⇒ 窗口找不回来"那个陷阱（它正是 `CanResize` + 不显示任务栏按钮那种形状）。
         */
        ArchiveFixer.Views.WindowMinimizePolicy.Apply(window);

        var root = new System.Windows.Controls.Grid
        {
            Margin = new Thickness(18, 16, 18, 14)
        };

        for (int i = 0; i < 6; i++)
        {
            root.RowDefinitions.Add(new System.Windows.Controls.RowDefinition
            {
                Height = System.Windows.GridLength.Auto
            });
        }

        var message = new System.Windows.Controls.TextBlock
        {
            Text = "对勾选的任务改名。确认后会先弹改名预览（绝不覆盖、绝不先删后移）；分卷文件只有「替换文件名里的文字」会动。",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12)
        };
        System.Windows.Controls.Grid.SetRow(message, 0);
        root.Children.Add(message);

        var modeBox = new System.Windows.Controls.ComboBox
        {
            MinHeight = 30,
            VerticalContentAlignment = VerticalAlignment.Center
        };

        modeBox.Items.Add(new System.Windows.Controls.ComboBoxItem
        {
            Content = "替换文件名里的文字（名字被塞了字就用它）",
            Tag = "ReplaceFileNameText",
            ToolTip = "把名字里出现的那一段换成另一段；「替换为」留空 = 直接删掉。整个文件名都算（含后缀段），区分大小写。"
        });

        modeBox.Items.Add(new System.Windows.Controls.ComboBoxItem
        {
            Content = "替换最后一个后缀",
            Tag = "ReplaceLastExtension",
            ToolTip = "只换最后那一段：test.jpg -> test.zip"
        });

        modeBox.Items.Add(new System.Windows.Controls.ComboBoxItem
        {
            Content = "添加后缀",
            Tag = "AddExtension",
            ToolTip = "在原名后面再挂一个后缀：x.mp4 -> x.mp4.zip"
        });

        modeBox.Items.Add(new System.Windows.Controls.ComboBoxItem
        {
            Content = "删除最后一个后缀",
            Tag = "DeleteLastExtension",
            ToolTip = "把最后那一段去掉：test.rar.jpg -> test.rar"
        });

        modeBox.SelectedIndex = 0;
        System.Windows.Controls.Grid.SetRow(modeBox, 1);
        root.Children.Add(modeBox);

        // ── 后缀那一行（后缀三种模式用） ──
        var extensionRow = new System.Windows.Controls.Grid
        {
            Margin = new Thickness(0, 8, 0, 0)
        };
        extensionRow.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition
        {
            Width = new System.Windows.GridLength(96)
        });
        extensionRow.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition
        {
            Width = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star)
        });

        var extensionLabel = new System.Windows.Controls.TextBlock
        {
            Text = "后缀",
            VerticalAlignment = VerticalAlignment.Center
        };
        System.Windows.Controls.Grid.SetColumn(extensionLabel, 0);
        extensionRow.Children.Add(extensionLabel);

        var extensionBox = new System.Windows.Controls.TextBox
        {
            Text = defaultExtension,
            MinHeight = 30,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        System.Windows.Controls.Grid.SetColumn(extensionBox, 1);
        extensionRow.Children.Add(extensionBox);

        System.Windows.Controls.Grid.SetRow(extensionRow, 2);
        root.Children.Add(extensionRow);

        // ── 查找 → 替换为（文字模式用） ──
        var textRow = new System.Windows.Controls.Grid
        {
            Margin = new Thickness(0, 8, 0, 0)
        };
        textRow.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition
        {
            Width = new System.Windows.GridLength(64)
        });
        textRow.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition
        {
            Width = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star)
        });
        textRow.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition
        {
            Width = new System.Windows.GridLength(12)
        });
        textRow.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition
        {
            Width = new System.Windows.GridLength(64)
        });
        textRow.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition
        {
            Width = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star)
        });

        var findLabel = new System.Windows.Controls.TextBlock
        {
            Text = "查找",
            VerticalAlignment = VerticalAlignment.Center
        };
        System.Windows.Controls.Grid.SetColumn(findLabel, 0);
        textRow.Children.Add(findLabel);

        var findBox = new System.Windows.Controls.TextBox
        {
            MinHeight = 30,
            VerticalContentAlignment = VerticalAlignment.Center,
            ToolTip = "要删掉 / 换掉的那一段（区分大小写）"
        };
        System.Windows.Controls.Grid.SetColumn(findBox, 1);
        textRow.Children.Add(findBox);

        var replaceLabel = new System.Windows.Controls.TextBlock
        {
            Text = "替换为",
            VerticalAlignment = VerticalAlignment.Center
        };
        System.Windows.Controls.Grid.SetColumn(replaceLabel, 3);
        textRow.Children.Add(replaceLabel);

        var replaceBox = new System.Windows.Controls.TextBox
        {
            MinHeight = 30,
            VerticalContentAlignment = VerticalAlignment.Center,
            ToolTip = "留空 = 直接删掉那一段（最常用）"
        };
        System.Windows.Controls.Grid.SetColumn(replaceBox, 4);
        textRow.Children.Add(replaceBox);

        System.Windows.Controls.Grid.SetRow(textRow, 3);
        root.Children.Add(textRow);

        var hint = new System.Windows.Controls.TextBlock
        {
            Text = "提示：「后缀」那一栏只在后缀三种模式下生效；文字模式在「整个文件名」上替换（含后缀段）。",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.75,
            Margin = new Thickness(0, 10, 0, 0)
        };
        hint.SetResourceReference(FrameworkElement.StyleProperty, "HintTextStyle");
        System.Windows.Controls.Grid.SetRow(hint, 4);
        root.Children.Add(hint);

        string SelectedMode()
        {
            return modeBox.SelectedItem is System.Windows.Controls.ComboBoxItem item &&
                   item.Tag is string tag
                ? tag
                : "ReplaceFileNameText";
        }

        void ApplyMode()
        {
            string mode = SelectedMode();
            bool isTextMode = string.Equals(mode, "ReplaceFileNameText", StringComparison.OrdinalIgnoreCase);
            bool needsValue = !string.Equals(mode, "DeleteLastExtension", StringComparison.OrdinalIgnoreCase);

            textRow.Visibility = isTextMode ? Visibility.Visible : Visibility.Collapsed;
            extensionRow.Visibility = isTextMode ? Visibility.Collapsed : Visibility.Visible;
            extensionBox.IsEnabled = needsValue;
            extensionBox.Opacity = needsValue ? 1.0 : 0.5;
        }

        modeBox.SelectionChanged += (_, _) => ApplyMode();
        ApplyMode();

        var buttons = new System.Windows.Controls.StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0)
        };

        var cancelButton = new System.Windows.Controls.Button
        {
            Content = StatusText.OpCancel,
            MinWidth = 88,
            Margin = new Thickness(0, 0, 8, 0),
            IsCancel = true
        };
        cancelButton.SetResourceReference(FrameworkElement.StyleProperty, "SecondaryButtonStyle");

        var okButton = new System.Windows.Controls.Button
        {
            Content = "确定",
            MinWidth = 96,
            IsDefault = true
        };
        okButton.SetResourceReference(FrameworkElement.StyleProperty, "PrimaryButtonStyle");

        okButton.Click += (_, _) =>
        {
            window.DialogResult = true;
            window.Close();
        };

        cancelButton.Click += (_, _) =>
        {
            window.DialogResult = false;
            window.Close();
        };

        buttons.Children.Add(cancelButton);
        buttons.Children.Add(okButton);

        System.Windows.Controls.Grid.SetRow(buttons, 5);
        root.Children.Add(buttons);

        window.Content = root;

        window.Loaded += (_, _) =>
        {
            if (string.Equals(SelectedMode(), "ReplaceFileNameText", StringComparison.OrdinalIgnoreCase))
            {
                findBox.Focus();
            }
            else
            {
                extensionBox.Focus();
            }
        };

        bool? result = window.ShowDialog();

        if (result != true)
        {
            return null;
        }

        return new RenameDialogResult(
            SelectedMode(),
            extensionBox.Text ?? string.Empty,
            findBox.Text ?? string.Empty,
            replaceBox.Text ?? string.Empty);
    }
}
