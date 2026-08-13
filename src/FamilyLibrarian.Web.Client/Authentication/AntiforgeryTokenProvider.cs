using System.Net.Http.Json;

namespace FamilyLibrarian.Web.Client.Authentication;

/// <summary>
/// Supplies the anti-forgery request token that state-changing API calls carry.
/// </summary>
/// <remarks>
/// The paired cookie is HttpOnly, so the token cannot be read out of
/// <c>document.cookie</c> and must be requested from the host. It is cached for
/// the session and re-fetched after a rejection, because the token is tied to
/// the identity cookie and therefore changes when the user signs in or out.
/// </remarks>
public sealed class AntiforgeryTokenProvider(HttpClient httpClient)
{
    public const string HeaderName = "X-CSRF-TOKEN";

    private string? _token;

    public async Task AttachAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var token = await GetTokenAsync(cancellationToken);
        if (!string.IsNullOrEmpty(token))
        {
            request.Headers.Remove(HeaderName);
            request.Headers.Add(HeaderName, token);
        }
    }

    /// <summary>Drops the cached token so the next call fetches a fresh one.</summary>
    public void Invalidate() => _token = null;

    private async Task<string?> GetTokenAsync(CancellationToken cancellationToken)
    {
        if (_token is not null)
        {
            return _token;
        }

        try
        {
            var response = await httpClient.GetFromJsonAsync<AntiforgeryTokenPayload>(
                "api/v1/antiforgery/token",
                cancellationToken);

            _token = response?.Token;
        }
        catch (HttpRequestException)
        {
            // An anonymous caller cannot get a token. Let the call proceed and be
            // refused by the host rather than failing here with a confusing error.
            _token = null;
        }

        return _token;
    }

    private sealed record AntiforgeryTokenPayload(string Token);
}
