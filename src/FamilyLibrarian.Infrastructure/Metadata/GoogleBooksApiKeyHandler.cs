using Microsoft.Extensions.Options;

namespace FamilyLibrarian.Infrastructure.Metadata;

public sealed class GoogleBooksApiKeyHandler(IOptions<GoogleBooksMetadataOptions> options)
    : DelegatingHandler
{
    private readonly string _apiKey = options.Value.ApiKey
        ?? throw new InvalidOperationException("Google Books API key is required when the provider is enabled.");

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request.RequestUri);

        var separator = string.IsNullOrEmpty(request.RequestUri.Query) ? "?" : "&";
        request.RequestUri = new Uri(
            string.Concat(
                request.RequestUri.OriginalString,
                separator,
                "key=",
                Uri.EscapeDataString(_apiKey)),
            UriKind.RelativeOrAbsolute);

        return base.SendAsync(request, cancellationToken);
    }
}
