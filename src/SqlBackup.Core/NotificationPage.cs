namespace SqlBackup.Core;

public sealed record NotificationPage(NotificationMessage[] Items, int UnreadCount);
