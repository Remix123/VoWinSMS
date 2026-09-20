using System.Windows.Controls;
using System.Windows.Input;
using System.Windows;
using VoWin.ViewModels.Pages;
using Wpf.Ui.Abstractions.Controls;

namespace VoWin.Views.Pages
{
    public partial class SettingsPage : INavigableView<SettingsViewModel>
    {
        public SettingsViewModel ViewModel { get; }

        public SettingsPage(SettingsViewModel viewModel)
        {
            ViewModel = viewModel;
            DataContext = this;
            ViewModel.NotificationSettingsLoaded += SyncNotificationSecrets;

            InitializeComponent();
            SyncNotificationSecrets();
        }

        private void SyncNotificationSecrets()
        {
            if (!IsInitialized) return;
            TelegramTokenBox.Password = ViewModel.Notifications.Telegram.BotToken;
            PushplusTokenBox.Password = ViewModel.Notifications.Pushplus.Token;
            WebhookSecretBox.Password = ViewModel.Notifications.Webhook.Secret;
            LarkSecretBox.Password = ViewModel.Notifications.Lark.Secret;
        }

        private void OnTelegramTokenChanged(object sender, RoutedEventArgs e)
        {
            ViewModel.Notifications.Telegram.BotToken = ((PasswordBox)sender).Password;
        }

        private void OnPushplusTokenChanged(object sender, RoutedEventArgs e)
        {
            ViewModel.Notifications.Pushplus.Token = ((PasswordBox)sender).Password;
        }

        private void OnWebhookSecretChanged(object sender, RoutedEventArgs e)
        {
            ViewModel.Notifications.Webhook.Secret = ((PasswordBox)sender).Password;
        }

        private void OnLarkSecretChanged(object sender, RoutedEventArgs e)
        {
            ViewModel.Notifications.Lark.Secret = ((PasswordBox)sender).Password;
        }

        private void OnPagePreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (SettingsScrollViewer is ScrollViewer scv)
            {
                scv.ScrollToVerticalOffset(scv.VerticalOffset - (e.Delta / 3.0));
                e.Handled = true;
            }
        }
    }
}
