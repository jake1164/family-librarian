using System.Text.Json.Nodes;

namespace FamilyLibrarian.Application.Providers;

/// <summary>One provider entry as declared in a repository catalog document.</summary>
public sealed record ProviderCatalogEntry(
    string Id,
    string Name,
    string? ProtocolVersion,
    IReadOnlyList<string> Capabilities,
    string? License,
    string? Publisher,
    string? TrustLabel,
    string? OciImageDigest,
    string? HomepageUrl,
    string? Description);

/// <summary>
/// Parses a catalog's cached entries JSON into a displayable, installable
/// list. Defensive by design: this is third-party content an admin opted
/// into, not a trusted schema — an entry missing an <c>id</c> is skipped
/// rather than failing the whole catalog, and malformed content yields an
/// empty list rather than an exception.
/// </summary>
public static class ProviderCatalogEntryParser
{
    public static IReadOnlyList<ProviderCatalogEntry> Parse(string? entriesJson)
    {
        if (string.IsNullOrWhiteSpace(entriesJson))
        {
            return [];
        }

        JsonArray? array;
        try
        {
            array = JsonNode.Parse(entriesJson) as JsonArray;
        }
        catch (System.Text.Json.JsonException)
        {
            return [];
        }

        if (array is null)
        {
            return [];
        }

        var results = new List<ProviderCatalogEntry>();
        foreach (var node in array)
        {
            var id = node?["id"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            var capabilities = node!["capabilities"]?.AsArray()
                ?.Select(capability => capability?.GetValue<string>() ?? string.Empty)
                .Where(capability => capability.Length > 0)
                .ToArray() ?? [];

            results.Add(new ProviderCatalogEntry(
                id,
                node["name"]?.GetValue<string>() ?? id,
                node["protocolVersion"]?.GetValue<string>(),
                capabilities,
                node["license"]?.GetValue<string>(),
                node["publisher"]?.GetValue<string>(),
                node["trustLabel"]?.GetValue<string>(),
                node["ociImageDigest"]?.GetValue<string>(),
                node["homepageUrl"]?.GetValue<string>(),
                node["description"]?.GetValue<string>()));
        }

        return results;
    }
}
