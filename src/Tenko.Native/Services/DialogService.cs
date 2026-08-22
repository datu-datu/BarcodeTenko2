using System.Windows;

namespace Tenko.Native.Services
{
    public class DialogService : IDialogService
    {
        public bool Confirm(string message, string title, MessageBoxImage icon = MessageBoxImage.Question)
        {
            return MessageBox.Show(message, title, MessageBoxButton.YesNo, icon) == MessageBoxResult.Yes;
        }

        public void ShowMessage(string message, string title = "情報", MessageBoxImage icon = MessageBoxImage.Information)
        {
            MessageBox.Show(message, title, MessageBoxButton.OK, icon);
        }
    }
}
