namespace FamilyLibrarian.Domain.Notifications;

public static class NotificationCategories
{
    public const string RequestNeedsReview = "request.needs_review";
    public const string RequestStatusChanged = "request.status_changed";

    /// <summary>
    /// KINDLE-7: sent once a <c>DeliveryAttempt</c> reaches
    /// <c>DeliveryAttemptStatus.Submitted</c>, asking the user to confirm it
    /// actually arrived on their Kindle -- distinct from
    /// <see cref="RequestStatusChanged"/>, which only reports the book being
    /// added to the library.
    /// </summary>
    public const string KindleDeliveryConfirmationRequested = "delivery.kindle_confirmation_requested";

    /// <summary>
    /// Admin-facing counterpart to <see cref="KindleDeliveryConfirmationRequested"/>:
    /// raised when a delivery attempt has no automatic path forward left (a
    /// terminal failure, an ambiguous submission, or a user's report-missing)
    /// and needs a human to act from the /admin/publishing queue.
    /// </summary>
    public const string DeliveryNeedsAttention = "delivery.needs_attention";
}

public static class NotificationSubjectTypes
{
    public const string BookRequest = "book_request";
    public const string DeliveryAttempt = "delivery_attempt";
}
