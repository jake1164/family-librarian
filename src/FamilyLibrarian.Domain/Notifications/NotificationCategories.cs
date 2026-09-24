namespace FamilyLibrarian.Domain.Notifications;

public static class NotificationCategories
{
    public const string RequestNeedsReview = "request.needs_review";
    public const string RequestStatusChanged = "request.status_changed";

    /// <summary>
    /// SELFSERV-1: sent to the requester (in addition to the admin-broadcast
    /// <see cref="RequestNeedsReview"/>, which still fires unconditionally --
    /// additive, not exclusive) when a request needs a
    /// <c>RequestReviewCategory.PreferenceAmbiguity</c> decision only they, or
    /// an admin, can make.
    /// </summary>
    public const string RequestPreferenceAmbiguity = "request.preference_ambiguity";

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

    /// <summary>
    /// TRACKING-1: raised for every active follower of a Series when a newly
    /// resolved Work joins it. Keyed by subject id on the new Work (not the
    /// stable Series), so by design it is never collapsed/recurred the way
    /// every other category here is -- an explicit product decision that
    /// series-follow and author-follow notifications are independent and not
    /// deduplicated against each other or against themselves.
    /// </summary>
    public const string SeriesNewEntryDetected = "following.series_entry_added";

    /// <summary>Author-follow counterpart to <see cref="SeriesNewEntryDetected"/>:
    /// raised for every active follower of an Author when a newly resolved
    /// Work is credited to them.</summary>
    public const string AuthorNewWorkDetected = "following.author_work_added";

    /// <summary>
    /// Raised by <c>ExternalProviderHealthPollService</c>'s independent
    /// background probe when a registered external provider transitions
    /// from operational to not (unreachable or an
    /// undecryptable stored credential) -- not fired again on every
    /// still-down poll tick, only the transition, so recurring never
    /// un-dismisses a still-broken provider's notification every cycle.
    /// </summary>
    public const string ProviderHealthDegraded = "provider.health_degraded";
}

public static class NotificationSubjectTypes
{
    public const string BookRequest = "book_request";
    public const string DeliveryAttempt = "delivery_attempt";
    public const string Work = "work";
    public const string ExternalProvider = "external_provider";
}
