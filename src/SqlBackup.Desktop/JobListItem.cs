using System.ComponentModel;
using SqlBackup.Core;

namespace SqlBackup.Desktop;

public sealed class JobListItem(BackupJob job) : INotifyPropertyChanged
{
    public BackupJob Job { get; } = job;
    public Guid Id => Job.Id;
    public string Name => Job.Name;
    public ConnectionConfiguration? Connection => Job.Connection;
    public bool Enabled => Job.Enabled;
    public string StatusText { get; private set; } = "No execution status loaded";
    public string StatusColor { get; private set; } = "#9AAEC1";
    public string NextRun { get; private set; } = "";
    public event PropertyChangedEventHandler? PropertyChanged;

    public void Update(JobOverview overview)
    {
        StatusText = overview.StatusText;
        StatusColor = overview.LastStatus switch
        {
            RunStatus.Succeeded => "#61C69B",
            RunStatus.Failed => "#FF9696",
            RunStatus.Interrupted => "#F0C477",
            RunStatus.Queued or RunStatus.Running => "#90C7FF",
            _ => "#9AAEC1"
        };
        NextRun = overview.NextRunUtc.HasValue
            ? $"Next {overview.NextRunUtc.Value.ToLocalTime():MMM dd HH:mm}" : overview.ScheduleMessage;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }

    public void MarkUnavailable()
    {
        StatusText = "Service unavailable · status stale";
        StatusColor = "#9AAEC1";
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }
}
