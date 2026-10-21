using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace SqlBackup.Infrastructure;

public static class ShareAccess
{
    public static async Task<T> RunAsync<T>(ShareCredentialSnapshot? credential, Func<Task<T>> operation)
    {
        if (credential is null)
        {
            // A service-account destination must not inherit an outer source share's outbound logon.
            return await WindowsIdentity.RunImpersonatedAsync(SafeAccessTokenHandle.InvalidHandle, operation);
        }

        // DPAPI belongs to the service, not to an outer NEW_CREDENTIALS impersonation session.
        using var token = WindowsIdentity.RunImpersonated(SafeAccessTokenHandle.InvalidHandle,
            () => CreateToken(credential));
        return await WindowsIdentity.RunImpersonatedAsync(token, operation);
    }

    private static SafeAccessTokenHandle CreateToken(ShareCredentialSnapshot credential)
    {
        byte[] plaintext = ProtectedData.Unprotect(credential.ProtectedPassword, null, DataProtectionScope.CurrentUser);
        try
        {
            string userName = credential.UserName;
            string? domain = null;
            int separator = userName.IndexOf('\\');
            if (separator >= 0)
            {
                domain = userName[..separator];
                userName = userName[(separator + 1)..];
            }

            // A separate outbound logon avoids global mapped drives and cross-job SMB credential collisions.
            if (!LogonUser(userName, domain, Encoding.UTF8.GetString(plaintext), 9, 3, out var token))
            {
                token?.Dispose();
                throw new IOException("Network credential logon failed. Check the configured share credentials.");
            }

            return token;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    [DllImport("advapi32.dll", EntryPoint = "LogonUserW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LogonUser(string userName, string? domain, string password,
        int logonType, int logonProvider, out SafeAccessTokenHandle token);
}
