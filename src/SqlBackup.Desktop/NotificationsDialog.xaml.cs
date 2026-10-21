using System.ComponentModel;
using System.Windows;
using SqlBackup.Core;

namespace SqlBackup.Desktop;

public partial class NotificationsDialog : Window
{
    private readonly Func<ServiceRequest, Task<ServiceResponse>> sendAsync;
    private bool busy;

    public NotificationsDialog(Func<ServiceRequest, Task<ServiceResponse>> sendAsync)
    {
        InitializeComponent();
        this.sendAsync = sendAsync;
    }

    public async Task RefreshAsync()
    {
        await LoadAsync(new ServiceRequest("Notifications"));
    }

    public async Task MarkSelectedReadAsync()
    {
        if (NotificationList.SelectedItem is not NotificationMessage item)
        {
            Status.Text = "Select a notification to mark it read.";
            return;
        }

        await LoadAsync(new ServiceRequest("ReadNotification", NotificationKey: item.EventKey));
    }

    private async Task LoadAsync(ServiceRequest request)
    {
        if (busy)
        {
            return;
        }

        string? selectedKey = (NotificationList.SelectedItem as NotificationMessage)?.EventKey;
        busy = true;
        ViewButton.IsEnabled = false;
        RefreshButton.IsEnabled = false;
        ReadButton.IsEnabled = false;
        ReadAllButton.IsEnabled = false;
        CloseButton.IsEnabled = false;
        try
        {
            var response = await sendAsync(request);
            var page = response.Notifications ?? throw new ArgumentException("No notification history returned.");
            NotificationList.ItemsSource = page.Items;
            NotificationList.SelectedItem = page.Items.FirstOrDefault(item => item.EventKey == selectedKey);
            Status.Text = $"{page.UnreadCount} unread · showing up to 100 local alerts, unread first. No email is configured.";
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Status.Text = "Notifications could not be confirmed. Reconnect to the service and refresh.";
        }
        finally
        {
            busy = false;
            RefreshButton.IsEnabled = true;
            ReadButton.IsEnabled = true;
            ReadAllButton.IsEnabled = true;
            CloseButton.IsEnabled = true;
            ViewButton.IsEnabled = NotificationList.SelectedItem is NotificationMessage;
        }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        await RefreshAsync();
    }

    private async void Read_Click(object sender, RoutedEventArgs e)
    {
        await MarkSelectedReadAsync();
    }

    private async void ReadAll_Click(object sender, RoutedEventArgs e)
    {
        await LoadAsync(new ServiceRequest("ReadAllNotifications"));
    }

    private void Selection_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (ViewButton is not null)
        {
            ViewButton.IsEnabled = !busy && NotificationList.SelectedItem is NotificationMessage;
        }
    }

    private void View_Click(object sender, RoutedEventArgs e)
    {
        if (NotificationList.SelectedItem is NotificationMessage selected)
        {
            var dialog = new OperationDetailsDialog(selected, sendAsync) { Owner = this };
            dialog.Loaded += async (_, _) => await dialog.LoadAsync();
            dialog.ShowDialog();
        }
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        e.Cancel = busy;
    }
}
