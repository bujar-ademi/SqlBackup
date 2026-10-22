using SqlBackup.Core;
using SqlBackup.Infrastructure;
using Microsoft.Extensions.Logging;

namespace SqlBackup.Service;

public sealed class ManagementHandler(
    JobStore jobs,
    RunStore runs,
    ConnectionSecrets secrets,
    ISqlBackupEngine engine,
    DestinationDiagnostics diagnostics,
    BackupCoordinator coordinator,
    ILogger<ManagementHandler> logger,
    RetentionService? retention = null,
    HistoryStore? history = null,
    OperationsStore? operations = null,
    NotificationStore? notifications = null,
    JobDiagnosticsService? jobDiagnostics = null)
{
    public async Task<ServiceResponse> HandleAsync(ServiceRequest request, CancellationToken cancellationToken)
    {
        string stage = "Dispatch";
        try
        {
            if (coordinator.IsMaintenanceActive && request.Operation is not
                ("Health" or "SetupReadiness" or "BeginMaintenance" or "Jobs" or "History" or "QueryHistory"
                or "RunDetails" or "Operations" or "Notifications" or "CleanupHistory"))
            {
                throw new ArgumentException("Installation maintenance is active. Configuration and operational changes are temporarily blocked.");
            }

            switch (request.Operation)
            {
                case "BeginMaintenance":
                    await coordinator.BeginMaintenanceAsync(cancellationToken);
                    return new ServiceResponse(true, "Maintenance enabled; no queued/running backup exists.");
                case "SetupReadiness":
                    await coordinator.CheckSetupReadinessAsync(cancellationToken);
                    return new ServiceResponse(true, "Storage initialization and active credential checks passed under the service identity.");
                case "Health":
                    return new ServiceResponse(true, "AbaSqlBackup online — Full, Differential and Log scheduling available.");
                case "Inspect" when request.Job is not null:
                    var snapshot = await secrets.PrepareAsync(request.Job, request.Password, cancellationToken);
                    var inspection = await engine.InspectAsync(snapshot, cancellationToken);
                    return new ServiceResponse(true, "Connection tested and database capabilities inspected.", Inspection: inspection);
                case "Jobs":
                    return new ServiceResponse(true, "Jobs loaded.", Jobs: await jobs.ListAsync(cancellationToken));
                case "SaveJob" when request.Job is not null:
                    stage = "SaveJob.Configuration";
                    JobValidation.Validate(request.Job);
                    if (request.Job.Enabled && request.Job.Schedule?.Enabled == true)
                    {
                        stage = "SaveJob.Schedule";
                        ScheduleCalculator.Validate(request.Job.Schedule);
                        var scheduleConnection = await secrets.PrepareAsync(request.Job, request.Password, cancellationToken);
                        var capabilities = await engine.InspectAsync(scheduleConnection, cancellationToken);
                        JobScheduleValidator.Validate(request.Job, capabilities);
                    }

                    stage = "SaveJob.Persistence";
                    await jobs.SaveAsync(request.Job, cancellationToken, request.Password, request.StoragePasswords);
                    return new ServiceResponse(true, "Job saved.", Jobs: await jobs.ListAsync(cancellationToken));
                case "DeleteJob" when request.Job is not null:
                    await jobs.DeleteAsync(request.Job, cancellationToken);
                    return new ServiceResponse(true, "Job configuration removed. Backup files and run history are retained.", Jobs: await jobs.ListAsync(cancellationToken));
                case "RunJob" when request.JobId.HasValue && request.Job is not null && request.Configuration is not null:
                    Guid runId = await coordinator.EnqueueAsync(request.JobId.Value, cancellationToken,
                        request.Job.Revision, request.Configuration);
                    return new ServiceResponse(true, "Manual Full backup queued in the service.", RunId: runId);
                case "CleanupPreview" when request.JobId.HasValue && retention is not null:
                    return new ServiceResponse(true, "Cleanup preview ready.",
                        Cleanup: await retention.PreviewAsync(request.JobId.Value, cancellationToken));
                case "CleanupExecute" when request.JobId.HasValue && request.PreviewToken is not null && retention is not null:
                    return new ServiceResponse(true, "Cleanup reviewed; inspect each audit outcome.",
                        CleanupHistory: await retention.ExecuteAsync(request.JobId.Value, request.PreviewToken, cancellationToken));
                case "CleanupHistory" when request.JobId.HasValue && retention is not null:
                    return new ServiceResponse(true, "Cleanup audit loaded.",
                        CleanupHistory: await retention.HistoryAsync(request.JobId.Value, cancellationToken));
                case "Operations" when operations is not null:
                    return new ServiceResponse(true, "Operational status loaded.",
                        Operations: await operations.ReadAsync(request.JobId, cancellationToken));
                case "QueryHistory" when history is not null:
                    return new ServiceResponse(true, "Filtered history loaded.",
                        History: await history.QueryAsync(request.History ?? new HistoryQuery(), cancellationToken));
                case "RunDetails" when history is not null && request.RunId.HasValue:
                    return new ServiceResponse(true, "Run details loaded.",
                        Runs: [await history.GetAsync(request.RunId.Value, cancellationToken)],
                        Log: await history.ReadLogAsync(request.RunId.Value, cancellationToken));
                case "Notifications" when notifications is not null:
                    return new ServiceResponse(true, "Local notifications loaded.",
                        Notifications: await notifications.ListAsync(cancellationToken));
                case "ReadAllNotifications" when notifications is not null:
                    await notifications.MarkAllReadAsync(cancellationToken);
                    return new ServiceResponse(true, "All local notifications marked read.",
                        Notifications: await notifications.ListAsync(cancellationToken));
                case "ReadNotification" when notifications is not null && request.NotificationKey is not null:
                    await notifications.MarkReadAsync(request.NotificationKey, cancellationToken);
                    return new ServiceResponse(true, "Notification marked read.",
                        Notifications: await notifications.ListAsync(cancellationToken));
                case "JobDiagnostics" when jobDiagnostics is not null && request.JobId.HasValue:
                    return new ServiceResponse(true, "Diagnostics completed; inspect each check.",
                        Diagnostics: await jobDiagnostics.CheckAsync(request.JobId.Value, cancellationToken));
                case "History":
                    return new ServiceResponse(true, "Latest 20 runs loaded.", Runs: await runs.ListAsync(cancellationToken));
                case "CheckDestination" when request.Job is not null:
                    var connection = await secrets.PrepareAsync(request.Job, request.Password, cancellationToken);
                    JobValidation.ValidateDestination(request.Job.Destination);
                    bool useDirectDestination = await engine.CanUseDirectDestinationAsync(connection, request.Job, cancellationToken);
                    string sqlFolder = useDirectDestination ? request.Job.Destination : BackupLocations.SqlFolder(request.Job);
                    string message = await diagnostics.CheckAsync(connection, sqlFolder, cancellationToken);
                    return new ServiceResponse(true, message);
                case "CheckStorage" when request.Job is not null:
                    JobValidation.ValidateStorage(request.Job.Destination, request.Job.Storage);
                    JobValidation.ValidateTemporaryFolder(request.Job);
                    var access = await jobs.PrepareStorageAsync(request.Job, request.StoragePasswords, cancellationToken);
                    var storageChecks = await new StorageDiagnostics().CheckAsync(request.Job, cancellationToken, access);
                    return new ServiceResponse(storageChecks.All(check => check.Status != StageStatus.Failed),
                        string.Join("\n", storageChecks.Select(check => $"{check.Name}: {check.Message}")), Diagnostics: storageChecks);
                default:
                    return new ServiceResponse(false, "Unsupported management operation.");
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            string code = exception is JobValidationException validation ? validation.Code : "ManagementRequestFailed";
            logger.LogWarning("Management operation failed. EventCode={EventCode} Stage={Stage} ErrorCode={ErrorCode} ErrorType={ErrorType}",
                "ManagementOperationFailed", stage, code, exception.GetType().Name);
            return new ServiceResponse(false, SqlFailure.Describe(exception), ErrorCode: code);
        }
    }
}
