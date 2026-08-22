using System.Windows;

namespace Tenko.Native.Services
{
    public interface IDialogService
    {
        bool Confirm(string message, string title, MessageBoxImage icon = MessageBoxImage.Question);
        void ShowMessage(string message, string title = "情報", MessageBoxImage icon = MessageBoxImage.Information);
    }
}
