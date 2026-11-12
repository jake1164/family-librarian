using FamilyLibrarian.Domain.ModelGateway;

namespace FamilyLibrarian.Application.ModelGateway;

/// <summary>Input rules shared by every administrator-managed model setting.</summary>
public static class ModelConfigurationRules
{
    public static string? EndpointError(string? endpointUrl)
    {
        if (string.IsNullOrWhiteSpace(endpointUrl))
        {
            return null;
        }

        var text = endpointUrl.Trim();
        if (text.Length > ModelGatewaySettings.MaxEndpointLength || !Uri.TryCreate(text, UriKind.Absolute, out var uri))
        {
            return "The endpoint URL is not valid.";
        }

        if (uri.Scheme is not ("http" or "https"))
        {
            return "The endpoint URL must use http or https.";
        }

        if (!string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            return "The endpoint URL must not contain credentials, a query string or a fragment. Use the API key field.";
        }

        return null;
    }

    public static bool IsSafeModelName(string value) =>
        value.Length is > 0 and <= ModelGatewaySettings.MaxModelNameLength &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-' or ':' or '/' or '@');

    public static string? ApiKeyError(string? apiKey)
    {
        var trimmed = apiKey?.Trim();
        return string.IsNullOrEmpty(trimmed) || trimmed.Length > 2_048 || trimmed.Any(char.IsControl)
            ? "Enter an API key (up to 2,048 characters, no control characters)."
            : null;
    }
}
