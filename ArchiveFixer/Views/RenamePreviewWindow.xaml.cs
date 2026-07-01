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

            ViewModel.Confirm();

            if (ViewModel.DialogResult == true)
            {
                DialogResult = true;
                Close();
            }
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
