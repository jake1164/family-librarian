namespace FamilyLibrarian.Contracts.Realtime;

[Flags]
public enum LiveUpdateTopics
{
    None = 0,
    Requests = 1,
    Security = 2,
    Publishing = 4,
    Notifications = 8,
    Sources = 16,
    System = 32,
    All = Requests | Security | Publishing | Notifications | Sources | System
}

public static class LiveUpdates
{
    public const string HubPath = "/api/v1/live";
    public const string Changed = "Changed";
}
