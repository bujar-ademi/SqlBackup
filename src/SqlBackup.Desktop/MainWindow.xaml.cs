using System.Windows.Media;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SqlBackup.Core;

namespace SqlBackup.Desktop;

public partial class MainWindow : Window
{
    private readonly Func<ServiceRequest, CancellationToken, Task<ServiceResponse>> sendAsync;
    private readonly DispatcherTimer historyTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private BackupJob? editingJob;
    private ServerInspection? inspection;
    private ConnectionConfiguration? inspectedConnection;
    private ConnectionConfiguration? draftConnection;
    private string pendingPassword = string.Empty;
    private string[] selectedDatabases = [];
    private string draftDestination = string.Empty;
    private BackupSchedule? draftSchedule;
    private StoragePolicy? draftStorage;
    private string? draftTemporaryFolder;
    private BackupDestination[]? draftDestinations;
    private DestinationPassword[] pendingDestinationPasswords = [];
    private StoragePasswords pendingStoragePasswords = new();
    private bool busy;
    private bool connected;
    private bool updatingSelection;
    private int historyOffset;
    private Guid? appliedHistoryJob;
    private HistoryQuery appliedHistory = new();
    private bool appliedSelectedJobOnly = true;
    private bool polling;
    private CancellationTokenSource? pollCancellation;
    private DateTimeOffset inspectionUtc;

    public MainWindow() : this((request, token) => new ServiceClient().SendAsync(request, token))
    {
    }

    public MainWindow(Func<ServiceRequest, Task<ServiceResponse>> sendAsync) : this((request, _) => sendAsync(request))
    {
    }

    public MainWindow(Func<ServiceRequest, CancellationToken, Task<ServiceResponse>> sendAsync)
    {
        InitializeComponent();
        this.sendAsync = sendAsync;
        HistoryStatus.ItemsSource = new object[] { "Any status" }.Concat(Enum.GetValues<RunStatus>().Cast<object>());
        HistoryType.ItemsSource = new object[] { "Any type" }.Concat(Enum.GetValues<BackupType>().Cast<object>());
        HistoryStatus.SelectedIndex = 0;
        HistoryType.SelectedIndex = 0;
        historyTimer.Tick += HistoryTimer_Tick;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        await PerformAsync(LoadWorkspaceAsync);
        historyTimer.Start();
    }

    private void PausePolling()
    {
        historyTimer.Stop();
        pollCancellation?.Cancel();
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        PausePolling();
        pendingPassword = string.Empty;
        // Execution belongs to the service; closing this window never cancels a run.
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmDiscardChanges())
        {
            return;
        }

        await PerformAsync(LoadWorkspaceAsync);
    }

    private async Task PerformAsync(Func<Task> operation, bool showValidation = false)
    {
        if (busy)
        {
            return;
        }

        busy = true;
        pollCancellation?.Cancel();
        Workspace.IsEnabled = false;
        try
        {
            ResultBanner.Background = new SolidColorBrush(Color.FromRgb(238, 244, 250));
            await operation();
        }
        catch (ArgumentException exception)
        {
            ResultBanner.Background = new SolidColorBrush(Color.FromRgb(255, 234, 234));
            Result.Text = exception.Message;
            if (showValidation)
            {
                MessageBox.Show(this, exception.Message, "Job could not be saved", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            MarkServiceUnavailable();
            ResultBanner.Background = new SolidColorBrush(Color.FromRgb(255, 234, 234));
            Result.Text = "The operation could not be confirmed. Refresh and reload before retrying a save or run request.";
        }
        finally
        {
            busy = false;
            Workspace.IsEnabled = true;
            NewJobButton.IsEnabled = connected;
            JobEditor.IsEnabled = connected && editingJob is not null;
        }
    }

    private Task<ServiceResponse> SendCheckedAsync(ServiceRequest request)
    {
        return SendCheckedAsync(request, CancellationToken.None);
    }

    private async Task<ServiceResponse> SendCheckedAsync(ServiceRequest request, CancellationToken cancellationToken)
    {
        var response = await sendAsync(request, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!response.Success)
        {
            if (response.Message == "Unsupported management operation.")
            {
                throw new ArgumentException("The desktop and service versions do not match. Stop both, rebuild, and restart AbaSqlBackup and the desktop.");
            }

            throw new ArgumentException(response.ErrorCode is null ? response.Message : $"[{response.ErrorCode}] {response.Message}");
        }

        return response;
    }

    private async Task LoadWorkspaceAsync()
    {
        var health = await SendCheckedAsync(new ServiceRequest("Health"));
        ServiceStatus.Text = health.Message;
        var response = await SendCheckedAsync(new ServiceRequest("Jobs"));
        SetJobs(response.Jobs ?? [], editingJob?.Id);
        await LoadDashboardAsync();
        connected = true;
        Result.Text = "Select a job or create a new one. Each job owns its SQL connection, databases, and destination.";
    }

    private void UpdateSummaries()
    {
        ConnectionSummary.Text = string.IsNullOrWhiteSpace(draftConnection?.Server)
            ? "Configure this job's SQL Server connection"
            : draftConnection.Server + (draftConnection.UseWindowsAuthentication ? " · Windows authentication" : " · SQL authentication");
        DatabaseSummary.Text = selectedDatabases.Length == 0 ? "No databases selected" : string.Join(", ", selectedDatabases);
        DestinationSummary.Text = string.IsNullOrWhiteSpace(draftDestination) ? "No folder configured" : draftDestination;
        if (draftTemporaryFolder is not null)
        {
            DestinationSummary.Text += "\nTemporary: " + draftTemporaryFolder;
        }
        if (!string.IsNullOrWhiteSpace(draftStorage?.SecondaryFolder))
        {
            DestinationSummary.Text += "\nSecondary: " + draftStorage.SecondaryFolder;
        }

        if (draftDestinations is not null)
        {
            DestinationSummary.Text = draftDestinations.Length == 0 ? "No destinations configured"
                : string.Join("\n", draftDestinations.Select(item => $"{item.Name} · {item.Kind} · {item.Location}"));
        }

        UpdateScheduleSummary();
    }

    private void UpdateScheduleSummary()
    {
        ScheduleSummary.Text = "Automatic backups are disabled";
        if (JobEnabled.IsChecked != true)
        {
            ScheduleSummary.Text = "Job disabled · no automatic backups";
            return;
        }

        if (draftSchedule?.Enabled == true)
        {
            try
            {
                var next = ScheduleCalculator.Next(draftSchedule, DateTimeOffset.UtcNow);
                var zone = TimeZoneInfo.FindSystemTimeZoneById(draftSchedule.TimeZoneId);
                ScheduleSummary.Text = next is null ? "No eligible occurrence in the next five years"
                    : $"Next: {TimeZoneInfo.ConvertTime(next.DueUtc, zone):yyyy-MM-dd HH:mm zzz} · {next.Type}\n{zone.DisplayName}";
            }
            catch (ArgumentException)
            {
                ScheduleSummary.Text = "Saved schedule needs attention. Review Advanced Schedule before enabling automatic backups.";
            }
        }
    }

    private void JobEnabled_Changed(object sender, RoutedEventArgs e)
    {
        if (ScheduleSummary is not null)
        {
            UpdateScheduleSummary();
        }
    }

    private void SetJobs(BackupJob[] jobs, Guid? selectedId)
    {
        updatingSelection = true;
        var rows = jobs.Select(job => new JobListItem(job)).ToArray();
        JobsList.ItemsSource = rows;
        var selected = rows.FirstOrDefault(item => item.Id == selectedId) ?? rows.FirstOrDefault();
        JobsList.SelectedItem = selected;
        updatingSelection = false;
        ShowJob(selected?.Job);
    }

    private void ShowJob(BackupJob? job)
    {
        editingJob = job;
        inspection = null;
        inspectedConnection = null;
        draftConnection = job?.Connection;
        pendingPassword = string.Empty;
        selectedDatabases = job?.Databases.ToArray() ?? [];
        JobEditor.IsEnabled = connected && job is not null;
        SelectedJobTitle.Text = job?.Name ?? "Select or create a job";
        JobName.Text = job?.Name ?? string.Empty;
        draftDestination = job?.Destination ?? string.Empty;
        draftSchedule = job?.Schedule;
        NotifyFailure.IsChecked = job?.Notifications?.OnFailure ?? true;
        NotifySuccess.IsChecked = job?.Notifications?.OnSuccess ?? false;
        DatabaseHealth.ItemsSource = null;
        HistoryList.ItemsSource = null;
        historyOffset = 0;
        draftStorage = job?.Storage;
        draftTemporaryFolder = job?.TemporaryFolder;
        draftDestinations = job?.Destinations?.ToArray();
        pendingDestinationPasswords = [];
        pendingStoragePasswords = new StoragePasswords();
        JobEnabled.IsChecked = job?.Enabled ?? true;
        Checksum.IsChecked = job?.Checksum ?? true;
        Verify.IsChecked = job?.Verify ?? true;
        Compression.IsChecked = job?.Compression ?? false;
        CopyOnly.IsChecked = job?.CopyOnly ?? true;

        UpdateSummaries();
    }

    private BackupJob ReadJob()
    {
        if (editingJob is null)
        {
            throw new ArgumentException("Create or select a job first.");
        }

        return editingJob with
        {
            Name = JobName.Text.Trim(),
            Destination = draftDestination,
            Databases = selectedDatabases.ToArray(),
            Enabled = JobEnabled.IsChecked == true,
            Checksum = Checksum.IsChecked == true,
            Verify = Verify.IsChecked == true,
            Compression = Compression.IsChecked == true,
            CopyOnly = CopyOnly.IsChecked == true,
            Connection = draftConnection,
            Schedule = draftSchedule,
            Storage = draftStorage,
            TemporaryFolder = draftTemporaryFolder,
            Destinations = draftDestinations,
            Notifications = new JobNotificationPolicy(NotifySuccess.IsChecked == true, NotifyFailure.IsChecked == true)
        };
    }

    private bool HasUnsavedChanges()
    {
        if (editingJob is null)
        {
            return false;
        }

        var edited = ReadJob();
        return edited.Name != editingJob.Name || edited.Destination != editingJob.Destination
            || edited.Enabled != editingJob.Enabled || edited.Checksum != editingJob.Checksum
            || edited.Verify != editingJob.Verify || edited.Compression != editingJob.Compression
            || edited.CopyOnly != editingJob.CopyOnly || edited.Connection != editingJob.Connection
            || edited.Schedule != editingJob.Schedule || edited.Storage != editingJob.Storage
            || edited.TemporaryFolder != editingJob.TemporaryFolder || pendingStoragePasswords != new StoragePasswords()
            || (edited.Notifications ?? new JobNotificationPolicy()) != (editingJob.Notifications ?? new JobNotificationPolicy())
            || pendingPassword.Length > 0
            || pendingDestinationPasswords.Length > 0
            || !(edited.Destinations ?? []).SequenceEqual(editingJob.Destinations ?? [])
            || !edited.Databases.Order().SequenceEqual(editingJob.Databases.Order());
    }

    private bool ConfirmDiscardChanges()
    {
        return !HasUnsavedChanges() || MessageBox.Show(this,
            "Discard unsaved changes to this job, including its SQL connection?", "Unsaved job",
            MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
    }

    private void NewJob_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmDiscardChanges())
        {
            return;
        }

        updatingSelection = true;
        JobsList.SelectedItem = null;
        updatingSelection = false;
        ShowJob(new BackupJob(Guid.NewGuid(), "New backup job", [], string.Empty,
            Connection: new ConnectionConfiguration("SQL Server", string.Empty, true, null),
            TemporaryFolder: BackupLocations.DefaultTemporaryFolder, Destinations: []));
        JobName.Focus();
        JobName.SelectAll();
    }

    private async void Jobs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (updatingSelection)
        {
            return;
        }

        if (!ConfirmDiscardChanges())
        {
            updatingSelection = true;
            JobsList.SelectedItem = JobsList.Items.Cast<JobListItem>().FirstOrDefault(item => item.Id == editingJob?.Id);
            updatingSelection = false;
            return;
        }

        ShowJob((JobsList.SelectedItem as JobListItem)?.Job);
        if (connected && !busy)
        {
            await PerformAsync(LoadDashboardAsync);
        }
    }

    private async void SaveJob_Click(object sender, RoutedEventArgs e)
    {
        await PerformAsync(async () =>
        {
            var job = ReadJob();
            JobValidation.Validate(job);
            ConnectionValidation.Validate(job.Connection);
            if (job.Connection != editingJob?.Connection
                && (inspection is null || inspectedConnection != job.Connection))
            {
                throw new ArgumentException("Test the changed SQL connection and review its database selection before saving.");
            }

            var response = await SendCheckedAsync(new ServiceRequest("SaveJob", Password: pendingPassword, Job: job,
                StoragePasswords: pendingStoragePasswords, DestinationPasswords: pendingDestinationPasswords));
            SetJobs(response.Jobs ?? [], job.Id);
            Result.Text = response.Message;
        }, showValidation: true);
    }

    private async void DeleteJob_Click(object sender, RoutedEventArgs e)
    {
        if (editingJob is null || editingJob.Revision == 0)
        {
            Result.Text = "Select a saved job to delete.";
            return;
        }

        if (MessageBox.Show(this, "Delete this job and its saved credentials? Backup files and execution history will be kept.",
            "Delete job", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        await PerformAsync(async () =>
        {
            var response = await SendCheckedAsync(new ServiceRequest("DeleteJob", Job: editingJob));
            SetJobs(response.Jobs ?? [], null);
            Result.Text = response.Message;
        });
    }

    private async void RunJob_Click(object sender, RoutedEventArgs e)
    {
        if (editingJob is null || editingJob.Revision == 0 || HasUnsavedChanges())
        {
            Result.Text = "Save the job and its SQL connection before running it.";
            return;
        }

        string mode = editingJob.CopyOnly ? "Copy-only Full" : "Regular Full — changes the Differential base";
        string review = $"{mode}\nServer: {editingJob.Connection?.Server}\nDatabases: {string.Join(", ", editingJob.Databases)}\nFinal folder: {editingJob.Destination}";
        if (editingJob.Destinations is not null)
        {
            review = $"{mode}\nServer: {editingJob.Connection?.Server}\nDatabases: {string.Join(", ", editingJob.Databases)}\nDestinations:\n"
                + string.Join("\n", editingJob.Destinations.Select(item => $"{item.Name} · {item.Kind} · {item.Location}"));
        }
        if (editingJob.TemporaryFolder is not null)
        {
            review += "\nTemporary SQL folder: " + editingJob.TemporaryFolder;
        }

        review += "\n\nStart this saved job now?";
        if (MessageBox.Show(this, review, "Run manual Full backup", MessageBoxButton.YesNo,
            MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        await PerformAsync(async () =>
        {
            var response = await SendCheckedAsync(new ServiceRequest("RunJob", Configuration: editingJob.Connection,
                Job: editingJob, JobId: editingJob.Id));
            Result.Text = response.Message;
            await LoadHistoryAsync();
        });
    }

    private void Connection_Click(object sender, RoutedEventArgs e)
    {
        if (editingJob is null || busy)
        {
            return;
        }

        PausePolling();
        try
        {
            var dialog = new ConnectionDialog(ReadJob(), pendingPassword, SendCheckedAsync) { Owner = this };
            if (dialog.ShowDialog() == true && dialog.Result is { } result)
            {
                bool targetChanged = draftConnection?.Server != result.Connection.Server
                    || draftConnection?.UserName != result.Connection.UserName
                    || draftConnection?.UseWindowsAuthentication != result.Connection.UseWindowsAuthentication;
                draftConnection = result.Connection;
                pendingPassword = result.Password;
                inspection = result.Inspection;
                inspectionUtc = DateTimeOffset.UtcNow;
                inspectedConnection = result.Connection;
                if (targetChanged)
                {
                    selectedDatabases = [];
                }

                UpdateSummaries();
                Result.Text = "Connection settings applied to this job. Select databases and Save Job to keep your changes.";
            }
        }
        finally
        {
            historyTimer.Start();
        }
    }

    private void Databases_Click(object sender, RoutedEventArgs e)
    {
        if (editingJob is null || busy)
        {
            return;
        }

        if (draftConnection is null || string.IsNullOrWhiteSpace(draftConnection.Server))
        {
            Result.Text = "Configure this job's SQL connection first.";
            return;
        }

        PausePolling();
        try
        {
            var job = ReadJob();
            string password = pendingPassword;
            var inspectionAge = DateTimeOffset.UtcNow - inspectionUtc;
            var cached = inspectedConnection == draftConnection && inspectionAge >= TimeSpan.Zero
                && inspectionAge < TimeSpan.FromSeconds(60) ? inspection : null;
            var dialog = new DatabaseSelectionDialog(draftConnection.Server, selectedDatabases, async token =>
            {
                var response = await SendCheckedAsync(new ServiceRequest("Inspect", Job: job, Password: password), token);
                return response.Inspection ?? throw new ArgumentException("The service returned no database list.");
            }, cached)
            { Owner = this };
            bool accepted = dialog.ShowDialog() == true;
            if (dialog.LatestInspection is not null)
            {
                inspection = dialog.LatestInspection;
                inspectedConnection = draftConnection;
                if (!ReferenceEquals(cached, inspection))
                {
                    inspectionUtc = DateTimeOffset.UtcNow;
                }
            }

            if (accepted)
            {
                selectedDatabases = dialog.SelectedDatabases;
                UpdateSummaries();
                Result.Text = "Database selection applied to this job. Save Job to keep your changes.";
            }
        }
        finally
        {
            historyTimer.Start();
        }
    }

    private void AddDestination_Click(object sender, RoutedEventArgs e)
    {
        if (editingJob is null || busy)
        {
            return;
        }

        var menu = DestinationKindMenu.Create(kind => OpenDestinationDialog(kind));
        menu.PlacementTarget = sender as UIElement;
        menu.IsOpen = true;
    }

    private void Destination_Click(object sender, RoutedEventArgs e)
    {
        OpenDestinationDialog();
    }

    private void OpenDestinationDialog(DestinationKind? newDestinationKind = null)
    {
        if (editingJob is null || busy)
        {
            return;
        }

        PausePolling();
        try
        {
            var dialog = new JobDestinationsDialog(ReadJob(), pendingPassword, SendCheckedAsync,
                pendingDestinationPasswords, pendingStoragePasswords, newDestinationKind)
            { Owner = this };
            if (dialog.ShowDialog() == true && dialog.SelectedJob is { } configured)
            {
                draftDestinations = configured.Destinations;
                draftStorage = configured.Storage;
                draftTemporaryFolder = configured.TemporaryFolder;
                pendingDestinationPasswords = dialog.SelectedPasswords;
                pendingStoragePasswords = dialog.SelectedSourcePasswords;
                UpdateSummaries();
                Result.Text = "Destination applied to this job. Save Job to keep your changes.";
            }
        }
        finally
        {
            historyTimer.Start();
        }
    }

    private void Schedule_Click(object sender, RoutedEventArgs e)
    {
        if (editingJob is null || busy)
        {
            return;
        }

        PausePolling();
        try
        {
            var dialog = new ScheduleDialog(draftSchedule) { Owner = this };
            if (dialog.ShowDialog() == true && dialog.SelectedSchedule is { } schedule)
            {
                draftSchedule = schedule;
                UpdateSummaries();
                Result.Text = "Schedule applied to this job. Save Job to persist and enable it in the service.";
            }
        }
        finally
        {
            historyTimer.Start();
        }
    }

    private void Cleanup_Click(object sender, RoutedEventArgs e)
    {
        if (editingJob is null || editingJob.Revision == 0 || HasUnsavedChanges())
        {
            Result.Text = "Save and reload the job before cleanup preview.";
            return;
        }

        PausePolling();
        try
        {
            new CleanupDialog(editingJob, SendCheckedAsync) { Owner = this }.ShowDialog();
        }
        finally
        {
            historyTimer.Start();
        }
    }

    private Task LoadDashboardAsync()
    {
        return LoadDashboardAsync(CancellationToken.None);
    }

    private async Task LoadDashboardAsync(CancellationToken cancellationToken)
    {
        var operations = await SendCheckedAsync(new ServiceRequest("Operations", JobId: editingJob?.Id), cancellationToken);
        if (operations.Operations is { } snapshot)
        {
            foreach (JobListItem item in JobsList.Items)
            {
                var overview = snapshot.Jobs.SingleOrDefault(candidate => candidate.JobId == item.Id);
                if (overview is not null)
                {
                    item.Update(overview);
                }
            }

            DatabaseHealth.ItemsSource = snapshot.Databases;
        }

        var notifications = await SendCheckedAsync(new ServiceRequest("Notifications"), cancellationToken);
        NotificationsButton.Content = $"Notifications ({notifications.Notifications?.UnreadCount ?? 0})";
        await LoadHistoryAsync(cancellationToken);
    }

    public HistoryQuery ReadHistoryFilters()
    {
        Guid? jobId = HistoryThisJob.IsChecked == true ? editingJob?.Id : null;
        DateTimeOffset? from = HistoryFrom.SelectedDate.HasValue
            ? new DateTimeOffset(HistoryFrom.SelectedDate.Value.Date).ToUniversalTime() : null;
        DateTimeOffset? to = HistoryTo.SelectedDate.HasValue
            ? new DateTimeOffset(HistoryTo.SelectedDate.Value.Date.AddDays(1)).ToUniversalTime() : null;
        return new HistoryQuery(jobId,
            HistoryStatus.SelectedItem is RunStatus status ? status : null,
            HistoryType.SelectedItem is BackupType type ? type : null,
            HistoryDatabase.Text.Trim(), from, to);
    }

    private Task LoadHistoryAsync()
    {
        return LoadHistoryAsync(CancellationToken.None);
    }

    private async Task LoadHistoryAsync(CancellationToken cancellationToken)
    {
        UpdateScheduleSummary();
        Guid? jobId = appliedSelectedJobOnly ? editingJob?.Id : null;
        if (jobId != appliedHistoryJob)
        {
            appliedHistory = appliedHistory with { JobId = jobId };
            appliedHistoryJob = jobId;
            historyOffset = 0;
        }

        Guid? selectedId = (HistoryList.SelectedItem as RunSummary)?.Id;
        var response = await SendCheckedAsync(new ServiceRequest("QueryHistory", History: appliedHistory with { Offset = historyOffset }), cancellationToken);
        var page = response.History ?? throw new ArgumentException("The service returned no history page. Restart both applications after updating.");
        HistoryList.ItemsSource = page.Items;
        HistoryList.SelectedItem = page.Items.FirstOrDefault(run => run.Id == selectedId) ?? page.Items.FirstOrDefault();

        PreviousHistoryButton.IsEnabled = historyOffset > 0;
        NextHistoryButton.IsEnabled = page.HasMore;
        HistoryPageLabel.Text = page.Items.Length == 0 ? "No matching executions" : $"{historyOffset + 1}–{historyOffset + page.Items.Length}";
    }

    private async void History_Click(object sender, RoutedEventArgs e)
    {
        await PerformAsync(LoadDashboardAsync);
    }

    private async void HistoryApply_Click(object sender, RoutedEventArgs e)
    {
        await PerformAsync(async () =>
        {
            var query = ReadHistoryFilters();
            HistoryValidation.Validate(query);
            appliedHistory = query;
            appliedSelectedJobOnly = HistoryThisJob.IsChecked == true;
            appliedHistoryJob = query.JobId;
            historyOffset = 0;
            await LoadHistoryAsync();
        });
    }

    private async void HistoryNext_Click(object sender, RoutedEventArgs e)
    {
        if (!busy)
        {
            historyOffset += 20;
            await PerformAsync(LoadHistoryAsync);
        }
    }

    private async void HistoryPrevious_Click(object sender, RoutedEventArgs e)
    {
        if (!busy)
        {
            historyOffset = Math.Max(0, historyOffset - 20);
            await PerformAsync(LoadHistoryAsync);
        }
    }

    private async void HistoryTimer_Tick(object? sender, EventArgs e)
    {
        if (busy || polling || !connected)
        {
            return;
        }

        polling = true;
        using var cancellation = new CancellationTokenSource();
        pollCancellation = cancellation;
        try
        {
            await LoadDashboardAsync(cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // Foreground actions take priority; cancellation is not a service outage.
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            MarkServiceUnavailable();
        }
        finally
        {
            pollCancellation = null;
            polling = false;
        }
    }

    private void MarkServiceUnavailable()
    {
        connected = false;
        JobEditor.IsEnabled = false;
        NewJobButton.IsEnabled = false;
        foreach (JobListItem item in JobsList.Items)
        {
            item.MarkUnavailable();
        }

        ServiceStatus.Text = "Service unavailable or busy. Displayed results are stale. Refresh before making changes.";
    }

    private void History_OpenLog(object sender, RoutedEventArgs e)
    {
        if (HistoryList.SelectedItem is RunSummary selected)
        {
            OpenJobLog(selected.Id);
        }
    }

    private void HistoryMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: RunSummary selected })
        {
            HistoryList.SelectedItem = selected;
            OpenJobLog(selected.Id);
        }
    }

    private void OpenJobLog(Guid runId)
    {
        PausePolling();
        try
        {
            var dialog = new OperationDetailsDialog(runId, SendCheckedAsync) { Owner = this };
            dialog.Loaded += async (_, _) => await dialog.LoadAsync();
            dialog.ShowDialog();
        }
        finally
        {
            historyTimer.Start();
        }
    }

    private void About_Click(object sender, RoutedEventArgs e)
    {
        new AboutDialog { Owner = this }.ShowDialog();
    }

    private void Notifications_Click(object sender, RoutedEventArgs e)
    {
        PausePolling();
        try
        {
            var dialog = new NotificationsDialog(SendCheckedAsync) { Owner = this };
            dialog.Loaded += async (_, _) => await dialog.RefreshAsync();
            dialog.ShowDialog();
        }
        finally
        {
            historyTimer.Start();
        }
    }

    private void Diagnostics_Click(object sender, RoutedEventArgs e)
    {
        if (editingJob is null || editingJob.Revision == 0 || HasUnsavedChanges())
        {
            Result.Text = "Save and reload the job before running diagnostics.";
            return;
        }

        PausePolling();
        try
        {
            new DiagnosticsDialog(editingJob.Id, SendCheckedAsync) { Owner = this }.ShowDialog();
        }
        finally
        {
            historyTimer.Start();
        }
    }
}
