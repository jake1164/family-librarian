namespace FamilyLibrarian.Web.Logging;

internal static partial class MetadataProviderLog
{
    [LoggerMessage(
        EventId = 1001,
        Level = LogLevel.Warning,
        Message = "Metadata provider {ProviderId} was unavailable during search.")]
    internal static partial void SearchUnavailable(
        ILogger logger,
        string providerId,
        Exception exception);

    [LoggerMessage(
        EventId = 1002,
        Level = LogLevel.Warning,
        Message = "Metadata provider {ProviderId} returned invalid JSON during search.")]
    internal static partial void SearchReturnedInvalidJson(
        ILogger logger,
        string providerId,
        Exception exception);

    [LoggerMessage(
        EventId = 1003,
        Level = LogLevel.Warning,
        Message = "Metadata provider {ProviderId} timed out during search.")]
    internal static partial void SearchTimedOut(
        ILogger logger,
        string providerId,
        Exception exception);

    [LoggerMessage(
        EventId = 1004,
        Level = LogLevel.Warning,
        Message = "Metadata provider {ProviderId} could not return candidate details.")]
    internal static partial void CandidateDetailsUnavailable(
        ILogger logger,
        string providerId,
        Exception exception);
}
