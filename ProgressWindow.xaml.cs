using System;
using System.Windows;

namespace PlateauRevitImporter
{
    /// <summary>
    /// プログレス表示ウィンドウ
    /// </summary>
    public partial class ProgressWindow : Window
    {
        public ProgressWindow()
        {
            InitializeComponent();
        }

        /// <summary>
        /// プログレスバーを更新
        /// </summary>
        public void UpdateProgress(int percentage, string status)
        {
            if (Dispatcher.CheckAccess())
            {
                progressBar.Value = percentage;
                percentageText.Text = $"{percentage}%";
                statusText.Text = status;
            }
            else
            {
                Dispatcher.Invoke(() =>
                {
                    progressBar.Value = percentage;
                    percentageText.Text = $"{percentage}%";
                    statusText.Text = status;
                });
            }
        }

        /// <summary>
        /// ステータステキストのみ更新
        /// </summary>
        public void UpdateStatus(string status)
        {
            if (Dispatcher.CheckAccess())
            {
                statusText.Text = status;
            }
            else
            {
                Dispatcher.Invoke(() =>
                {
                    statusText.Text = status;
                });
            }
        }

        /// <summary>
        /// ウィンドウを閉じる
        /// </summary>
        public void CloseWindow()
        {
            if (Dispatcher.CheckAccess())
            {
                Close();
            }
            else
            {
                Dispatcher.Invoke(() => Close());
            }
        }
    }
}
