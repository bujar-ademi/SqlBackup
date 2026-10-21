using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;

namespace SqlBackup.Service;

public static class ManagementSecurity
{
    public static bool IsAuthorized(WindowsIdentity identity)
    {
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    public static PipeSecurity CreatePipeSecurity()
    {
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(true, false);
        // Windows selects the token's default owner. Assigning Administrators explicitly
        // fails for a filtered UAC token with ERROR_INVALID_OWNER.
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.NetworkSid, null),
            PipeAccessRights.FullControl, AccessControlType.Deny));

        foreach (var sidType in new[] { WellKnownSidType.BuiltinAdministratorsSid, WellKnownSidType.LocalSystemSid })
        {
            security.AddAccessRule(new PipeAccessRule(
                new SecurityIdentifier(sidType, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        }

        return security;
    }

    public static void EnsureHostAuthorized()
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!identity.IsSystem && !IsAuthorized(identity))
        {
            throw new UnauthorizedAccessException(
                "AbaSqlBackup requires an elevated administrator or LocalSystem. "
                + "Start Visual Studio or the service terminal with Run as administrator.");
        }
    }
}
