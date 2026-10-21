using System.Windows;
using SqlBackup.Core;

namespace SqlBackup.Desktop;

public partial class DatabaseSelectionDialog : Window
{
    public DatabaseSelectionItem[] Items { get; private set; } = [];
    public string[] SelectedDatabases { get; private set; }

    private Func<CancellationToken, Task<ServerInspection>>? loadAsync;
    private CancellationTokenSource? refreshCancellation;
    private bool busy;
    private bool closed;
    public ServerInspection? LatestInspection { get; private set; }

    private DatabaseSelectionDialog(string server, string[] selectedNames)
    {
        InitializeComponent();
        ServerLabel.Text = server;
        SelectedDatabases = selectedNames.ToArray();
        Closing += (_, _) =>
        {
            closed = true;
            refreshCancellation?.Cancel();
            loadAsync = null;
        };
    }

    public DatabaseSelectionDialog(ServerInspection inspection, string server, string[] selectedNames)
        : this(server, selectedNames)
    {
        ApplyInspection(inspection, selectedNames);
        RefreshButton.Visibility = Visibility.Collapsed;
    }

    public DatabaseSelectionDialog(string server, string[] selectedNames,
        Func<CancellationToken, Task<ServerInspection>> loadAsync, ServerInspection? initialInspection = null)
        : this(server, selectedNames)
    {
        this.loadAsync = loadAsync;
        if (initialInspection is not null)
        {
            ApplyInspection(initialInspection, selectedNames);
            Status.Text = "Using the recent connection inspection. Refresh to check for changes on SQL Server.";
        }
        else
        {
            AcceptButton.IsEnabled = false;
            SelectionControls.IsEnabled = false;
            Loading.Visibility = Visibility.Visible;
            Status.Text = "Loading databases from SQL Server… You can cancel while waiting.";
            Loaded += async (_, _) => await RefreshAsync();
        }
    }

    private void ApplyInspection(ServerInspection inspection, string[] selectedNames)
    {
        LatestInspection = inspection;
        var databases = inspection.Databases.ToList();
        foreach (string name in selectedNames)
        {
            if (!databases.Any(database => database.Name == name))
            {
                databases.Add(new DatabaseInfo(name, "Unavailable", "Unknown", false, "Saved selection is not currently available on this server."));
            }
        }

        Items = databases.Select(database => new DatabaseSelectionItem(database,
            selectedNames.Contains(database.Name, StringComparer.Ordinal))).ToArray();
        ShowSystem.IsChecked = ShowSystem.IsChecked == true || Items.Any(item => item.IsSystem && item.IsSelected);
        UpdateFilter();
    }

    public async Task RefreshAsync()
    {
        if (busy || closed || loadAsync is null)
        {
            return;
        }

        string[] selected = LatestInspection is null ? SelectedDatabases
            : Items.Where(item => item.IsSelected).Select(item => item.Database.Name).ToArray();
        using var cancellation = new CancellationTokenSource();
        refreshCancellation = cancellation;
        busy = true;
        AcceptButton.IsEnabled = false;
        RefreshButton.IsEnabled = false;
        SelectionControls.IsEnabled = false;
        Loading.Visibility = Visibility.Visible;
        Status.Text = "Loading databases from SQL Server… You can cancel while waiting.";
        try
        {
            // Yield first so the dialog paints even if a transport performs synchronous setup work.
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
            cancellation.Token.ThrowIfCancellationRequested();
            var inspection = await loadAsync(cancellation.Token);
            if (!closed && !cancellation.IsCancellationRequested)
            {
                ApplyInspection(inspection, selected);
                Status.Text = "Database list refreshed. Selections remain a draft until Save & Close.";
            }
        }
        catch (OperationCanceledException)
        {
            if (!closed)
            {
                Status.Text = "Database loading was cancelled. Refresh to try again.";
            }
        }
        catch (ArgumentException exception)
        {
            if (!closed)
            {
                Status.Text = exception.Message;
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            if (!closed)
            {
                Status.Text = "Could not load databases. Check the service, SQL connection and network, then Refresh.";
            }
        }
        finally
        {
            busy = false;
            refreshCancellation = null;
            if (!closed)
            {
                Loading.Visibility = Visibility.Collapsed;
                RefreshButton.IsEnabled = true;
                SelectionControls.IsEnabled = LatestInspection is not null;
                AcceptButton.IsEnabled = LatestInspection is not null;
            }
        }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        await RefreshAsync();
    }

    private void UpdateFilter()
    {
        if (Items is not null && DatabaseList is not null)
        {
            DatabaseList.ItemsSource = Items.Where(item => ShowSystem.IsChecked == true || !item.IsSystem).ToArray();
        }
    }

    private void Filter_Changed(object sender, RoutedEventArgs e)
    {
        UpdateFilter();
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        var available = Items.Where(item => !item.IsSystem && item.Database.CanBackUp).ToArray();
        int alreadySelected = Items.Count(item => item.IsSelected);
        int extra = available.Count(item => !item.IsSelected);
        if (alreadySelected + extra > 16)
        {
            Status.Text = "This version supports up to 16 selected databases per job. Select them individually.";
            return;
        }

        foreach (var item in available)
        {
            item.IsSelected = true;
        }
    }

    public bool TryApplySelection()
    {
        if (busy || LatestInspection is null)
        {
            Status.Text = "Wait for database inspection to finish or cancel the dialog.";
            return false;
        }

        string[] selected = Items.Where(item => item.IsSelected).Select(item => item.Database.Name).ToArray();
        if (selected.Length is < 1 or > 16)
        {
            Status.Text = "Select between one and sixteen databases.";
            return false;
        }

        SelectedDatabases = selected;
        return true;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (TryApplySelection())
        {
            DialogResult = true;
        }
    }
}
