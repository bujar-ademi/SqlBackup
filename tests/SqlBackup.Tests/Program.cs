using System.IO.Pipes;
using System.IO;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using SqlBackup.Core;
using SqlBackup.Infrastructure;
using SqlBackup.Service;
using SqlBackup.Tests;

if (args.Length == 2 && args[0] == "--installed-service")
{
    await InstalledServiceChecks.RunAsync(args[1]);
    return;
}

if (args.Length == 2 && args[0] == "--render-desktop")
{
    DesktopChecks.Render(args[1]);
    return;
}

if (args.Length == 2 && args[0] == "--sql-instance")
{
    await LocalSqlChecks.RunAsync(args[1]);
    return;
}

string testDirectory = Path.Combine(Path.GetTempPath(), "SqlBackup.Tests", Guid.NewGuid().ToString("N"));
var store = new ConfigurationStore(testDirectory);
await store.InitializeAsync(CancellationToken.None);
Assert(await store.ReadAsync(CancellationToken.None) is null, "New schema has no configuration");

const string samplePassword = "Fictional-Test-Secret-123!";
var configuration = new ConnectionConfiguration("Test", "fictional-server", false, "test-user");
await store.SaveAsync(configuration, samplePassword, CancellationToken.None);
var reopened = new ConfigurationStore(testDirectory);
await reopened.InitializeAsync(CancellationToken.None);
var loaded = await reopened.ReadAsync(CancellationToken.None);
Assert(loaded?.HasPassword == true && loaded.Server == configuration.Server, "Configuration survives store recreation");

await using (var connection = new SqliteConnection($"Data Source={Path.Combine(testDirectory, "configuration.db")};Pooling=False"))
{
    await connection.OpenAsync();
    await using var command = connection.CreateCommand();
    command.CommandText = "SELECT ProtectedPassword FROM Configuration WHERE Id = 1";
    var encrypted = (byte[])(await command.ExecuteScalarAsync())!;
    Assert(!Encoding.UTF8.GetString(encrypted).Contains(samplePassword), "Password is not persisted as plaintext");
    byte[] decrypted = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
    Assert(Encoding.UTF8.GetString(decrypted) == samplePassword, "DPAPI round trip under the service identity");
    CryptographicOperations.ZeroMemory(decrypted);
}

await reopened.SaveAsync(configuration, null, CancellationToken.None);
Assert((await reopened.ReadAsync(CancellationToken.None))?.HasPassword == true, "Blank password preserves existing credentials");
await ExpectAsync<ArgumentException>(
    () => reopened.SaveAsync(configuration with { Server = "different-server" }, null, CancellationToken.None),
    "Changing the credential target requires a replacement password");
await ExpectAsync<ArgumentException>(
    () => reopened.SaveAsync(configuration with { Name = "" }, samplePassword, CancellationToken.None),
    "Invalid configuration is rejected");
await reopened.SaveAsync(configuration with { UseWindowsAuthentication = true }, null, CancellationToken.None);
Assert((await reopened.ReadAsync(CancellationToken.None))?.HasPassword == false, "Windows authentication removes stored SQL credentials");

using (var invalidFrame = new MemoryStream(BitConverter.GetBytes(Protocol.MaximumFrameSize + 1)))
{
    await ExpectAsync<InvalidDataException>(
        async () =>
        {
            await Protocol.ReadAsync<ServiceRequest>(invalidFrame, CancellationToken.None);
        },
        "Oversized IPC frame is rejected before allocation");
}

await Phase2Checks.RunAsync();
await ScheduleChecks.RunAsync();
await StorageChecks.RunAsync();
await StagingChecks.RunAsync();
await SetupChecks.RunAsync();
await OperationsChecks.RunAsync();
await PerformanceChecks.RunAsync();

using var identity = WindowsIdentity.GetCurrent();
using (var ownerRegressionPipe = NamedPipeServerStreamAcl.Create(
    "SqlBackup.OwnerRegression." + Guid.NewGuid().ToString("N"),
    PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
    PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, 4096, 4096,
    ManagementSecurity.CreatePipeSecurity()))
{
    var owner = ownerRegressionPipe.GetAccessControl().GetOwner(typeof(SecurityIdentifier));
    Assert(owner?.Equals(identity.Owner) == true,
        "Production pipe security creates a real pipe using the Windows token's valid default owner");
}

var securityRules = ManagementSecurity.CreatePipeSecurity().GetAccessRules(true, true, typeof(SecurityIdentifier));
bool networkAccessDenied = false;
foreach (PipeAccessRule rule in securityRules)
{
    var sid = (SecurityIdentifier)rule.IdentityReference;
    if (rule.AccessControlType == AccessControlType.Allow)
    {
        Assert(sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid)
            || sid.IsWellKnown(WellKnownSidType.LocalSystemSid), "Production pipe ACL grants access only to administrators and SYSTEM");
    }

    if (sid.IsWellKnown(WellKnownSidType.NetworkSid) && rule.AccessControlType == AccessControlType.Deny)
    {
        networkAccessDenied = true;
    }
}

Assert(networkAccessDenied, "Production pipe ACL denies network identities");
if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
{
    Assert(!ManagementSecurity.IsAuthorized(identity), "The current non-elevated caller fails the service authorization policy");
    await ExpectAsync<UnauthorizedAccessException>(() =>
    {
        ManagementSecurity.EnsureHostAuthorized();
        return Task.CompletedTask;
    }, "Unelevated service startup fails with an actionable authorization error");
    await using var untrustedPipe = new NamedPipeServerStream(
        Protocol.PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | PipeOptions.FirstPipeInstance);
    var connected = untrustedPipe.WaitForConnectionAsync();
    await ExpectAsync<UnauthorizedAccessException>(
        async () =>
        {
            await new ServiceClient().SendAsync(new ServiceRequest("Save", configuration, samplePassword));
        },
        "Client rejects a pipe owned by an untrusted non-elevated identity");
    await connected;
    byte[] received = new byte[1];
    int receivedCount = await untrustedPipe.ReadAsync(received);
    Assert(receivedCount == 0, "No credential payload is sent to the untrusted pipe");
    Console.WriteLine("SKIP: Live IPC and worker restart checks require an elevated local administrator; they have NOT been verified.");
    if (args.Contains("--require-ipc"))
    {
        Environment.ExitCode = 1;
    }

    return;
}

using var worker = CreateManagementWorker(reopened);
await worker.StartAsync(CancellationToken.None);
try
{
    var client = new ServiceClient();
    var health = await client.SendAsync(new ServiceRequest("Health"));
    Assert(health.Success, "Authenticated local IPC returns service health");
    var ipcJob = new BackupJob(Guid.NewGuid(), "IPC job", ["FictionalDatabase"], @"C:\FictionalBackups", Connection: configuration);
    var saved = await client.SendAsync(new ServiceRequest("SaveJob", Job: ipcJob, Password: samplePassword));
    Assert(saved.Success, "Job and its connection can be saved through IPC");
    var response = await client.SendAsync(new ServiceRequest("Jobs"));
    Assert(response.Jobs?.Single().Connection?.HasPassword == true && !response.Message.Contains(samplePassword), "IPC returns job connection metadata without the password");
    var unknown = await client.SendAsync(new ServiceRequest("ExecuteArbitrarySql"));
    Assert(!unknown.Success, "Unsupported operations are rejected");

    await using var pipe = new NamedPipeClientStream(".", Protocol.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
    await pipe.ConnectAsync(3000);
    var rules = pipe.GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier));
    var networkSid = new SecurityIdentifier(WellKnownSidType.NetworkSid, null);
    bool networkDenied = false;
    foreach (PipeAccessRule rule in rules)
    {
        if (rule.AccessControlType == AccessControlType.Allow)
        {
            var sid = (SecurityIdentifier)rule.IdentityReference;
            Assert(sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid)
                || sid.IsWellKnown(WellKnownSidType.LocalSystemSid), "Pipe grants access only to administrators and SYSTEM");
        }

        if (rule.IdentityReference.Equals(networkSid) && rule.AccessControlType == AccessControlType.Deny)
        {
            networkDenied = true;
        }
    }

    Assert(networkDenied, "Network callers are explicitly denied by the pipe ACL");
}
finally
{
    await worker.StopAsync(CancellationToken.None);
}

using var restartedWorker = CreateManagementWorker(new ConfigurationStore(testDirectory));
await restartedWorker.StartAsync(CancellationToken.None);
try
{
    var response = await new ServiceClient().SendAsync(new ServiceRequest("Jobs"));
    Assert(response.Jobs?.Single().Connection?.Server == configuration.Server, "Job connection survives management worker restart");
}
finally
{
    await restartedWorker.StopAsync(CancellationToken.None);
}

Console.WriteLine($"All foundation checks passed. Isolated fixture retained at {testDirectory}");

static void Assert(bool condition, string description)
{
    if (!condition)
    {
        throw new InvalidOperationException(description);
    }

    Console.WriteLine($"PASS: {description}");
}

static ManagementWorker CreateManagementWorker(ConfigurationStore store)
{
    var jobs = new JobStore(store);
    var runs = new RunStore(store);
    var secrets = new ConnectionSecrets(store);
    var engine = new SqlBackupEngine();
    var coordinator = new BackupCoordinator(store, jobs, runs, secrets, engine, NullLogger<BackupCoordinator>.Instance);
    var handler = new ManagementHandler(jobs, runs, secrets, engine, new DestinationDiagnostics(),
        coordinator, NullLogger<ManagementHandler>.Instance,
        history: new HistoryStore(store), operations: new OperationsStore(jobs, runs),
        notifications: new NotificationStore(store));
    return new ManagementWorker(store, handler, NullLogger<ManagementWorker>.Instance);
}

static async Task ExpectAsync<TException>(Func<Task> action, string description) where TException : Exception
{
    try
    {
        await action();
    }
    catch (TException)
    {
        Console.WriteLine($"PASS: {description}");
        return;
    }

    throw new InvalidOperationException(description);
}
