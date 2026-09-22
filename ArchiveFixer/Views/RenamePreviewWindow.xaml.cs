using ArchiveFixer.Models;
using ArchiveFixer.ViewModels;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;

namespace ArchiveFixer.Views
{
    public partial class RenamePreviewWindow : Window
    {
        public RenamePreviewViewModel ViewModel { get; }

        public RenamePreviewWindow()
            : this(Array.Empty<RenamePreviewItem>())
        {
        }

        public RenamePreviewWindow(IEnumerable<RenamePreviewItem> previewItems)
        {
            InitializeComponent();

            ViewModel = new RenamePreviewViewModel(previewItems);
            DataContext = ViewModel;

            ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        }

        public RenamePreviewWindow(
            IEnumerable<RenamePreviewItem> previewItems,
            IEnumerable<ArchiveTask> tasks)
            : this(previewItems)
        {
        }

        private void ConfirmButton_Click(object sender, RoutedEventArgs e)
        {
            CommitDataGridEdit();

            /*
             * 「询问」档的逐条确认（真实缺陷修正）：还有撞名的行没选怎么办时**不放行**。
             *
             * 为什么必须在这里拦一下：Ask 这一档的语义就是"先问你"，用户没答就走人等于
             * 又回到了"界面说询问、代码悄悄改名"的老毛病（只是这次是悄悄自动重命名而已）。
             * 拦下来的代价只有一次点击 —— 下拉框就在这一行里，备选里也给了"自动重命名（默认，不覆盖）"。
             *
             * 刻意**不再弹第二个对话框**：预览本来就是"先给你看"的那个地方（不变量 3），
             * 要问就地问在这里。
             */
            int pending = ViewModel.Items.Count(item => item.NeedsConflictChoice &&
                                                        string.IsNullOrWhiteSpace(item.ConflictChoice));

            if (pending > 0)
            {
                ViewModel.Message = $"还有 {pending} 个同名冲突没选怎么办（在「冲突选择（询问档）」列里选，或点下面的「冲突全部自动重命名」）。";

                MessageBox.Show(
                    this,
                    $"有 {pending} 个目标文件已经存在，请先在「冲突选择（询问档）」那一列里选择怎么办。\n\n" +
                    "· 自动重命名：产物落成 名字(1)，已有文件一个字节都不动（默认，不覆盖）\n" +
                    "· 跳过：这一条不改名\n" +
                    "· 覆盖：先移开旧文件再落位，中途失败不丢文件\n\n" +
                    "嫌麻烦可以直接点「冲突全部自动重命名」。",
                    "还有冲突没选",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning,
                    MessageBoxResult.OK);

                return;
            }

            ViewModel.Confirm();

            if (ViewModel.DialogResult == true)
            {
                DialogResult = true;
                Close();
            }
        }

        /// <summary>
        /// 「冲突全部自动重命名」：一次把**所有**待选的冲突都设成自动重命名。
        ///
        /// 为什么要这一下：一批几十个撞名文件时逐个点下拉框确实烦，而"全部走自动重命名"
        /// 正是 WinRAR「全部重命名」那一档的等价物（也是这里最安全的默认 —— 绝不覆盖）。
        /// </summary>
        private void ConflictAllAutoRenameButton_Click(object sender, RoutedEventArgs e)
        {
            CommitDataGridEdit();

            int count = 0;

            foreach (RenamePreviewItem item in ViewModel.Items)
            {
                if (!item.NeedsConflictChoice)
                {
                    continue;
                }

                item.ConflictChoice = "AutoRename";
                count++;
            }

            ViewModel.Message = count > 0
                ? $"已把 {count} 个同名冲突设为自动重命名（不会覆盖任何已有文件）。"
                : "当前没有需要选择的同名冲突。";
        }

        private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(RenamePreviewViewModel.DialogResult))
            {
                return;
            }

            if (ViewModel.DialogResult == false)
            {
                DialogResult = false;
                Close();
            }
        }

        private void CommitDataGridEdit()
        {
            try
            {
                RenamePreviewGrid.CommitEdit(DataGridEditingUnit.Cell, true);
                RenamePreviewGrid.CommitEdit(DataGridEditingUnit.Row, true);
            }
            catch
            {
                // 提交编辑失败不应导致窗口崩溃。
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
            base.OnClosed(e);
        }
    }
}
