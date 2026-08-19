using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using FamilyLibrarian.Application.Publishing;

namespace FamilyLibrarian.Infrastructure.Publishing;

/// <summary>
/// Calls Audiobookshelf's <c>GET /api/libraries</c>, which returns each
/// library's folders inline — the same IDs the upload API
/// (<see cref="AudiobookshelfApiClient"/>) requires but that Audiobookshelf's
/// own admin UI never surfaces directly, forcing administrators to guess
/// them otherwise.
/// </summary>
public sealed class AudiobookshelfLibraryDiscoveryClient(IHttpClientFactory httpClientFactory)
    : IAudiobookshelfLibraryDiscoveryClient
{
    public async Task<AudiobookshelfLibraryDiscoveryOutcome> ListLibrariesAsync(
        string baseUrl, string apiToken, CancellationToken cancellationToken)
    {
        try
        {
            var client = httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(10);

            using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl.TrimEnd('/')}/api/libraries");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiToken);

            using var response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return AudiobookshelfLibraryDiscoveryOutcome.Failure(response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized => "The API token was rejected.",
                    _ => $"Audiobookshelf responded with status {(int)response.StatusCode}."
                });
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            return AudiobookshelfLibraryDiscoveryOutcome.Success(ParseLibraries(body));
        }
        catch (HttpRequestException exception)
        {
            return AudiobookshelfLibraryDiscoveryOutcome.Failure($"Audiobookshelf is unreachable: {exception.Message}");
        }
    }

    private static List<AudiobookshelfLibraryInfo> ParseLibraries(string responseJson)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(responseJson);
        }
        catch (System.Text.Json.JsonException)
        {
            return [];
        }

        var libraries = root?["libraries"]?.AsArray();
        if (libraries is null)
        {
            return [];
        }

        var result = new List<AudiobookshelfLibraryInfo>();
        foreach (var library in libraries)
        {
            var id = library?["id"]?.GetValue<string>();
            var name = library?["name"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            result.Add(new AudiobookshelfLibraryInfo(id, name, ParseFolders(library?["folders"])));
        }

        return result;
    }

    private static List<AudiobookshelfFolderInfo> ParseFolders(JsonNode? foldersNode)
    {
        var folders = new List<AudiobookshelfFolderInfo>();
        foreach (var folder in foldersNode?.AsArray() ?? [])
        {
            var folderId = folder?["id"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(folderId))
            {
                continue;
            }

            var folderPath = folder?["fullPath"]?.GetValue<string>() ?? folder?["path"]?.GetValue<string>();
            folders.Add(new AudiobookshelfFolderInfo(folderId, string.IsNullOrWhiteSpace(folderPath) ? folderId : folderPath));
        }

        return folders;
    }
}
