namespace FamilyLibrarian.Domain.Notifications;

public static class NotificationCategories
{
    public const string RequestNeedsReview = "request.needs_review";
    public const string RequestStatusChanged = "request.status_changed";
}

public static class NotificationSubjectTypes
{
    public const string BookRequest = "book_request";
}
