using System.IO.Pipes;
using System.Security.Principal;

namespace SqlBackup.Core;

public sealed class ServiceClient
{
    public Task<ServiceResponse> SendAsync(ServiceRequest request, CancellationToken cancellationToken = default)
    {
        // Pipe ACL inspection and JSON work can execute synchronously even around asynchronous I/O.
        return Task.Run(() => SendCoreAsync(request, cancellationToken), cancellationToken);
    }

    private static async Task<ServiceResponse> SendCoreAsync(ServiceRequest request, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        await using var pipe = new NamedPipeClientStream(
            ".", Protocol.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous,
            TokenImpersonationLevel.Identification);

        await pipe.ConnectAsync(timeout.Token);
        var owner = pipe.GetAccessControl().GetOwner(typeof(SecurityIdentifier));
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        if (!administrators.Equals(owner) && !system.Equals(owner))
        {
            throw new UnauthorizedAccessException("The management pipe has an untrusted owner.");
        }

        await Protocol.WriteAsync(pipe, request, timeout.Token);
        if (request.Operation is "CleanupPreview" or "CleanupExecute")
        {
            timeout.CancelAfter(TimeSpan.FromMinutes(10));
        }

        return await Protocol.ReadAsync<ServiceResponse>(pipe, timeout.Token);
    }
}
