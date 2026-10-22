using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SqlBackup.Infrastructure;
using SqlBackup.Service;

try
{
    ManagementSecurity.EnsureHostAuthorized();
}
catch (UnauthorizedAccessException exception)
{
    Console.Error.WriteLine(exception.Message);
    Environment.ExitCode = 1;
    return;
}

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "AbaSqlBackup";
});
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole();
builder.Logging.AddEventLog(options =>
{
    options.SourceName = "AbaSqlBackup";
});

string dataDirectory = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SqlBackup");
var configurationStore = new ConfigurationStore(dataDirectory);
// Acquire before either worker starts, so a second host cannot reconcile another host's active runs.
using var serviceLock = new FileStream(Path.Combine(dataDirectory, "service.lock"),
    FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
builder.Services.AddSingleton(configurationStore);
builder.Services.AddSingleton<JobStore>();
builder.Services.AddSingleton<RunStore>();
builder.Services.AddSingleton<HistoryStore>();
builder.Services.AddSingleton<OperationsStore>();
builder.Services.AddSingleton<NotificationStore>();
builder.Services.AddSingleton<StorageDiagnostics>();
builder.Services.AddSingleton<JobDiagnosticsService>();
builder.Services.AddSingleton<ScheduleStore>();
builder.Services.AddSingleton<CleanupAuditStore>();
builder.Services.AddSingleton<RetentionService>();
builder.Services.AddSingleton<IBackupHistoryInspector, SqlBackupHistoryInspector>();
builder.Services.AddSingleton<ConnectionSecrets>();
builder.Services.AddSingleton<StorageSecrets>();
builder.Services.AddSingleton<DestinationStorage>();
builder.Services.AddSingleton<ISqlBackupEngine, SqlBackupEngine>();
builder.Services.AddSingleton<DestinationDiagnostics>();
builder.Services.AddSingleton<BackupCoordinator>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<BackupCoordinator>());
builder.Services.AddHostedService<ScheduleWorker>();
builder.Services.AddSingleton<ManagementHandler>();
builder.Services.AddHostedService<ManagementWorker>();
await builder.Build().RunAsync();
