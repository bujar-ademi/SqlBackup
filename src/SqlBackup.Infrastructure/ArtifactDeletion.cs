using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace SqlBackup.Infrastructure;

public static class ArtifactDeletion
{
    public static async Task DeleteVerifiedAsync(string path, string expectedHash, CancellationToken cancellationToken)
    {
        ArtifactFiles.EnsureNoLinks(path);
        // Open the exact file with READ + DELETE access and no sharing. Hash and delete the same handle,
        // so another process cannot replace the path between verification and deletion.
        using var handle = NativeFileMethods.CreateFileW(path, 0x80010000, 0, IntPtr.Zero, 3, 0x08000000, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            throw new IOException("Cannot exclusively open the cleanup artifact.", new Win32Exception(Marshal.GetLastWin32Error()));
        }

        var resolved = new StringBuilder(32768);
        uint length = NativeFileMethods.GetFinalPathNameByHandleW(handle, resolved, (uint)resolved.Capacity, 0);
        string finalPath = resolved.ToString();
        if (finalPath.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            finalPath = @"\\" + finalPath[8..];
        }
        else if (finalPath.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            finalPath = finalPath[4..];
        }

        if (length == 0 || length >= resolved.Capacity
            || !Path.GetFullPath(path).Equals(finalPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("The opened artifact does not resolve to its reviewed managed path.");
        }

        await using var stream = new FileStream(handle, FileAccess.Read);
        string actualHash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
        if (actualHash != expectedHash)
        {
            throw new IOException("Artifact changed after preview; deletion rejected.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var disposition = new FileDisposition { DeleteFile = 1 };
        if (!NativeFileMethods.SetFileInformationByHandle(handle, 4, ref disposition, (uint)Marshal.SizeOf<FileDisposition>()))
        {
            throw new IOException("Artifact deletion was rejected.", new Win32Exception(Marshal.GetLastWin32Error()));
        }
    }
}
