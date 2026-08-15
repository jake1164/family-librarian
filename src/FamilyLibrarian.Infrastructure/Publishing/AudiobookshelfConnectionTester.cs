using System.Net;
using System.Net.Http.Headers;
using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Application.Publishing;
using FamilyLibrarian.Domain.Publishing;

namespace FamilyLibrarian.Infrastructure.Publishing;

public sealed class AudiobookshelfConnectionTester(
    IHttpClientFactory httpClientFactory, ICredentialProtector protector) : IAudiobookshelfConnectionTester
{
    public async Task<ConnectionTestOutcome> TestAsync(AudiobookshelfSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (string.IsNullOrWhiteSpace(settings.BaseUrl))
        {
            return new ConnectionTestOutcome(false, "No base URL is configured.");
        }

        if (string.IsNullOrWhiteSpace(settings.LibraryId))
        {
            return new ConnectionTestOutcome(false, "No library ID is configured.");
        }

        if (!settings.HasApiToken)
        {
            return new ConnectionTestOutcome(false, "No API token is configured.");
        }

        var token = protector.Unprotect(
            PublishingSecretPurposes.AudiobookshelfApiToken, settings.ProtectedApiToken!, settings.ApiTokenFormatVersion);
        if (token is null)
        {
            return new ConnectionTestOutcome(false, "The stored API token could not be decrypted.");
        }

        try
        {
            var client = httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(10);

            using var request = new HttpRequestMessage(
                HttpMethod.Get, $"{settings.BaseUrl.TrimEnd('/')}/api/libraries/{settings.LibraryId}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var response = await client.SendAsync(request, cancellationToken);
            return response.StatusCode switch
            {
                HttpStatusCode.OK => new ConnectionTestOutcome(true, "Connected and found the configured library."),
                HttpStatusCode.Unauthorized => new ConnectionTestOutcome(false, "The API token was rejected."),
                HttpStatusCode.NotFound => new ConnectionTestOutcome(false, "The configured library ID was not found."),
                _ => new ConnectionTestOutcome(false, $"Audiobookshelf responded with status {(int)response.StatusCode}.")
            };
        }
        catch (HttpRequestException exception)
        {
            return new ConnectionTestOutcome(false, $"Audiobookshelf is unreachable: {exception.Message}");
        }
    }
}
