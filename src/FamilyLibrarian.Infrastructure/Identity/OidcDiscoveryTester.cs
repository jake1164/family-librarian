using System.Text.Json.Nodes;
using FamilyLibrarian.Application.Accounts;

namespace FamilyLibrarian.Infrastructure.Identity;

/// <summary>
/// Fetches an issuer's <c>.well-known/openid-configuration</c> document purely
/// to confirm it is reachable and show the admin what it resolved — nothing
/// here is persisted; the OIDC handler resolves the same document itself,
/// continuously, at runtime.
/// </summary>
public sealed class OidcDiscoveryTester(IHttpClientFactory httpClientFactory) : IOidcDiscoveryTester
{
    public async Task<OidcDiscoveryTestOutcome> TestAsync(string? authority, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(authority) ||
            !Uri.TryCreate(authority, UriKind.Absolute, out var authorityUri) ||
            (authorityUri.Scheme != Uri.UriSchemeHttp && authorityUri.Scheme != Uri.UriSchemeHttps))
        {
            return OidcDiscoveryTestOutcome.Failure("Enter a valid issuer URL first.");
        }

        var discoveryUrl = $"{authority.TrimEnd('/')}/.well-known/openid-configuration";

        try
        {
            var client = httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(10);

            using var response = await client.GetAsync(discoveryUrl, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return OidcDiscoveryTestOutcome.Failure(
                    $"The issuer responded with status {(int)response.StatusCode} for its discovery document.");
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var document = JsonNode.Parse(body);
            if (document is null)
            {
                return OidcDiscoveryTestOutcome.Failure("The discovery document was not valid JSON.");
            }

            var endpoints = new OidcDiscoveryEndpoints(
                document["authorization_endpoint"]?.GetValue<string>(),
                document["token_endpoint"]?.GetValue<string>(),
                document["userinfo_endpoint"]?.GetValue<string>(),
                document["jwks_uri"]?.GetValue<string>(),
                document["end_session_endpoint"]?.GetValue<string>());

            return endpoints.AuthorizationEndpoint is null || endpoints.TokenEndpoint is null
                ? OidcDiscoveryTestOutcome.Failure(
                    "The discovery document is missing an authorization or token endpoint.")
                : OidcDiscoveryTestOutcome.Success(endpoints);
        }
        catch (HttpRequestException exception)
        {
            return OidcDiscoveryTestOutcome.Failure($"The issuer is unreachable: {exception.Message}");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return OidcDiscoveryTestOutcome.Failure("The issuer did not respond in time.");
        }
        catch (System.Text.Json.JsonException)
        {
            return OidcDiscoveryTestOutcome.Failure("The discovery document was not valid JSON.");
        }
    }
}
