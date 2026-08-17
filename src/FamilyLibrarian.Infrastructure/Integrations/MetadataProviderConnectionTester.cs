using System.Text.Json;
using FamilyLibrarian.Application.Catalog;

namespace FamilyLibrarian.Infrastructure.Integrations;

/// <summary>
/// Probes a provider server-side and reports a short, non-secret outcome.
/// </summary>
/// <remarks>
/// The probe runs in the host, never the browser, so the credential is never
/// exposed to the client. Failure messages are deliberately generic: a provider's
/// raw error text can echo the request URL, which for Google Books carries the
/// API key as a query parameter.
/// </remarks>
public sealed class MetadataProviderConnectionTester(
    IEnumerable<IBookMetadataProvider> providers)
{
    private static readonly BookSearchQuery ProbeQuery = new("the hobbit");

    public async Task<MetadataProviderTestOutcome> TestAsync(
        string providerId,
        CancellationToken cancellationToken)
    {
        var provider = providers.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, providerId, StringComparison.OrdinalIgnoreCase));
        if (provider is null)
        {
            return new MetadataProviderTestOutcome(false, "That provider is not installed.");
        }

        try
        {
            var results = await provider.SearchAsync(ProbeQuery, cancellationToken);
            return new MetadataProviderTestOutcome(
                true,
                $"Connected. The test search returned {results.Count} result(s).");
        }
        catch (InvalidOperationException)
        {
            // Thrown by the API key handler when no credential is available.
            return new MetadataProviderTestOutcome(false, "No API key is configured.");
        }
        catch (HttpRequestException exception)
        {
            return new MetadataProviderTestOutcome(
                false,
                exception.StatusCode is null
                    ? "Could not reach the provider."
                    : $"The provider rejected the request ({(int)exception.StatusCode}).");
        }
        catch (JsonException)
        {
            return new MetadataProviderTestOutcome(false, "The provider returned an unreadable response.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new MetadataProviderTestOutcome(false, "The provider timed out.");
        }
    }
}

public sealed record MetadataProviderTestOutcome(bool Succeeded, string Message);
