namespace FamilyLibrarian.Infrastructure.Integrations;

/// <summary>
/// Carries a not-yet-saved credential through the async call chain of a single
/// connection test, so the outbound request handler can use it in place of the
/// stored value without persisting it or exposing it to any other request.
/// </summary>
/// <remarks>
/// <see cref="AsyncLocal{T}"/> flows with the logical async chain of the request
/// that starts it (the same guarantee <c>HttpContext</c> ambient state relies on),
/// so a value set here is visible only to code awaited from within
/// <see cref="Begin"/>'s scope, including the pooled <c>DelegatingHandler</c> that
/// eventually resolves the credential for the outbound call.
/// </remarks>
public static class MetadataCredentialTestOverride
{
    private static readonly AsyncLocal<(string ProviderId, string Credential)?> Current = new();

    public static IDisposable Begin(string providerId, string credential)
    {
        var previous = Current.Value;
        Current.Value = (providerId, credential);
        return new Scope(previous);
    }

    public static string? TryGet(string providerId)
    {
        var current = Current.Value;
        return current is not null &&
            string.Equals(current.Value.ProviderId, providerId, StringComparison.OrdinalIgnoreCase)
                ? current.Value.Credential
                : null;
    }

    private sealed class Scope((string ProviderId, string Credential)? previous) : IDisposable
    {
        public void Dispose() => Current.Value = previous;
    }
}
