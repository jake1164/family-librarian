namespace FamilyLibrarian.Domain.Providers;

/// <summary>
/// The household's optional, deployment-controlled private-egress gateway
/// (e.g. Gluetun's HTTP proxy) — generic and provider-neutral, never a
/// specific commercial VPN's configuration.
/// </summary>
/// <remarks>
/// One row, same singleton-row shape as <c>OidcSettings</c>/<c>CwaSettings</c>.
/// "Health" here means the endpoint is listening (a TCP connect), not that it
/// can actually reach the internet — proving that would require calling out
/// through it to some third party, which this app has no reason to depend on.
/// </remarks>
public sealed class PrivateEgressGatewaySettings
{
    private PrivateEgressGatewaySettings()
    {
    }

    public PrivateEgressGatewaySettings(DateTimeOffset createdAtUtc)
    {
        Id = Guid.NewGuid();
        IsEnabled = false;
        CreatedAtUtc = createdAtUtc;
        UpdatedAtUtc = createdAtUtc;
    }

    public Guid Id { get; private set; } = Guid.NewGuid();

    public bool IsEnabled { get; private set; }

    /// <summary>An HTTP proxy endpoint, e.g. <c>http://gluetun:8888</c>.</summary>
    public string? GatewayEndpoint { get; private set; }

    public DateTimeOffset? LastTestedAtUtc { get; private set; }

    public bool? LastTestSucceeded { get; private set; }

    public string? LastTestMessage { get; private set; }

    public Guid? UpdatedByUserId { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public uint Version { get; private set; }

    public void SetEnabled(bool isEnabled, Guid? actorUserId, DateTimeOffset updatedAtUtc)
    {
        IsEnabled = isEnabled;
        Touch(actorUserId, updatedAtUtc);
    }

    public void SetGatewayEndpoint(string? gatewayEndpoint, Guid? actorUserId, DateTimeOffset updatedAtUtc)
    {
        GatewayEndpoint = string.IsNullOrWhiteSpace(gatewayEndpoint) ? null : gatewayEndpoint.Trim();
        LastTestedAtUtc = null;
        LastTestSucceeded = null;
        LastTestMessage = null;
        Touch(actorUserId, updatedAtUtc);
    }

    public void RecordTestResult(bool succeeded, string? message, Guid? actorUserId, DateTimeOffset testedAtUtc)
    {
        LastTestedAtUtc = testedAtUtc;
        LastTestSucceeded = succeeded;
        LastTestMessage = string.IsNullOrWhiteSpace(message)
            ? null
            : message.Length <= 512 ? message : message[..512];
        Touch(actorUserId, testedAtUtc);
    }

    private void Touch(Guid? actorUserId, DateTimeOffset updatedAtUtc)
    {
        UpdatedByUserId = actorUserId;
        UpdatedAtUtc = updatedAtUtc;
    }
}
