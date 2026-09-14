using System.Net.Http.Headers;
using FamilyLibrarian.Infrastructure.Integrations;
using FamilyLibrarian.Infrastructure.Providers;

namespace FamilyLibrarian.Infrastructure.Metadata;

/// <summary>
/// Attaches the admin's Hardcover personal access token to each outbound request.
/// </summary>
/// <remarks>
/// Same rationale as <see cref="GoogleBooksApiKeyHandler"/>: resolved per request
/// rather than captured in the constructor, since a pooled handler outlives any
/// single request scope and an administrator can replace the token at any time.
/// Hardcover's docs ask for the header value <c>Bearer &lt;token&gt;</c>; the
/// stored token is expected to be the raw token, but a copy-pasted value that
/// already carries the "Bearer " prefix is tolerated rather than sent twice.
/// </remarks>
public sealed class HardcoverAuthorizationHandler(IMetadataCredentialAccessor credentials)
    : DelegatingHandler
{
    private const string BearerPrefix = "Bearer ";

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var token = await credentials.GetCredentialAsync(
            ProviderRegistry.HardcoverProviderId,
            cancellationToken);

        if (string.IsNullOrWhiteSpace(token))
        {
            // Mirrors GoogleBooksApiKeyHandler: the provider was resolved as
            // usable but the token vanished between that check and this call.
            throw new InvalidOperationException(
                "The Hardcover personal access token is not configured.");
        }

        token = token.Trim();
        if (token.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            token = token[BearerPrefix.Length..];
        }

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        return await base.SendAsync(request, cancellationToken);
    }
}
