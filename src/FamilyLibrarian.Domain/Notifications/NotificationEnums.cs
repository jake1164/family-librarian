namespace FamilyLibrarian.Domain.Notifications;

public enum NotificationAudience
{
    AdminBroadcast = 1,
    SingleUser = 2
}

public enum NotificationSeverity
{
    Info = 1,
    Warning = 2,
    Error = 3
}
