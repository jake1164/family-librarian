using FamilyLibrarian.Infrastructure.Integrations;
using FamilyLibrarian.Infrastructure.Providers;

namespace FamilyLibrarian.Infrastructure.Metadata;

/// <summary>
/// Appends the Google Books API key to each outbound request.
/// </summary>
/// <remarks>
/// The key is resolved per request rather than captured in the constructor. An
/// administrator can replace it through the Metadata Providers page at any time,
/// and <c>HttpMessageHandler</c> instances are pooled and reused for minutes at a
/// time — a constructor-captured key would keep sending the superseded value
/// until the pool recycled it.
/// <para>
/// The credential arrives through <see cref="IMetadataCredentialAccessor"/>
/// because pooled handlers outlive any request scope and so cannot hold scoped
/// services such as the DbContext.
/// </para>
/// </remarks>
public sealed class GoogleBooksApiKeyHandler(IMetadataCredentialAccessor credentials)
    : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request.RequestUri);

        var apiKey = await credentials.GetCredentialAsync(
            ProviderRegistry.GoogleBooksProviderId,
            cancellationToken);

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            // Reaching here means the provider was resolved as usable but the key
            // vanished between that check and the call — a cleared credential
            // racing an in-flight search. Fail loudly rather than sending an
            // unauthenticated request that returns a confusing 403 from Google.
            throw new InvalidOperationException(
                "The Google Books API key is not configured.");
        }

        var separator = string.IsNullOrEmpty(request.RequestUri.Query) ? "?" : "&";
        request.RequestUri = new Uri(
            string.Concat(
                request.RequestUri.OriginalString,
                separator,
                "key=",
                Uri.EscapeDataString(apiKey)),
            UriKind.RelativeOrAbsolute);

        return await base.SendAsync(request, cancellationToken);
    }
}
