using SqlBackup.Core;

namespace SqlBackup.Infrastructure;

// Internal service-side data; this type must never be returned over the management interface.
public sealed record ConnectionSnapshot(ConnectionConfiguration Configuration, byte[]? ProtectedPassword);
