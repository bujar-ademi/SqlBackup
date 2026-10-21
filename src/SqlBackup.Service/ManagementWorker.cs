using System.IO.Pipes;
using System.Security.Principal;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SqlBackup.Core;
using SqlBackup.Infrastructure;

namespace SqlBackup.Service;

public sealed class ManagementWorker(
    ConfigurationStore store,
    ManagementHandler handler,
    ILogger<ManagementWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        ManagementSecurity.EnsureHostAuthorized();
        await store.InitializeAsync(stoppingToken);
        logger.LogInformation("Management service ready. EventCode={EventCode}", "ServiceReady");

        while (!stoppingToken.IsCancellationRequested)
        {
            await using var pipe = CreatePipe();
            try
            {
                await pipe.WaitForConnectionAsync(stoppingToken);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(30));

                bool authorized = false;
                pipe.RunAsClient(() =>
                {
                    using var identity = WindowsIdentity.GetCurrent(true);
                    if (identity is not null)
                    {
                        authorized = ManagementSecurity.IsAuthorized(identity);
                    }
                });

                ServiceResponse response;
                if (!authorized)
                {
                    response = new ServiceResponse(false, "An elevated local administrator is required.");
                    logger.LogWarning("Management request rejected. EventCode={EventCode}", "AccessDenied");
                }
                else
                {
                    var request = await Protocol.ReadAsync<ServiceRequest>(pipe, timeout.Token);
                    if (request.Operation is "CleanupPreview" or "CleanupExecute")
                    {
                        timeout.CancelAfter(TimeSpan.FromMinutes(10));
                    }

                    response = await handler.HandleAsync(request, timeout.Token);
                }

                await Protocol.WriteAsync(pipe, response, timeout.Token);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception) when (exception is IOException or OperationCanceledException
                or System.Text.Json.JsonException or UnauthorizedAccessException)
            {
                // Payloads and exception messages can contain credentials; log only a fixed event and type.
                logger.LogWarning("Management connection failed. EventCode={EventCode} ErrorType={ErrorType}",
                    "ConnectionFailed", exception.GetType().Name);
            }
        }
    }

    private static NamedPipeServerStream CreatePipe()
    {
        return NamedPipeServerStreamAcl.Create(
            Protocol.PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, 4096, 4096,
            ManagementSecurity.CreatePipeSecurity());
    }
}
