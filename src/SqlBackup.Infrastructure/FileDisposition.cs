using System.Runtime.InteropServices;

namespace SqlBackup.Infrastructure;

[StructLayout(LayoutKind.Sequential)]
internal struct FileDisposition
{
    public byte DeleteFile;
}
