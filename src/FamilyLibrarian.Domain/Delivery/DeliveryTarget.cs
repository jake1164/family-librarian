namespace FamilyLibrarian.Domain.Delivery;

/// <summary>
/// A user's configured destination for receiving an owned ebook -- for
/// example, their Kindle's Send-to-Kindle email address.
/// </summary>
/// <remarks>
/// A target is independent of any one request. <c>RequestParticipant.DeliveryTargetId</c>
/// references one to capture per-request delivery intent, snapshotted at
/// request/join time -- see docs/01-product-architecture-spec.md §15 and the
/// kindle delivery beta plan's "Shape reconciliation" addendum for why intent
/// lives there rather than on this type.
/// </remarks>
public sealed class DeliveryTarget
{
    public const int MaxNameLength = 128;
    public const int MaxAddressLength = 320;

    private DeliveryTarget()
    {
    }

    public DeliveryTarget(
        Guid userId,
        DeliveryTargetProvider provider,
        string name,
        string address,
        DateTimeOffset createdAtUtc)
    {
        if (userId == Guid.Empty)
        {
            throw new ArgumentException("A user ID is required.", nameof(userId));
        }

        if (!Enum.IsDefined(provider))
        {
            throw new ArgumentException("An unknown provider was given.", nameof(provider));
        }

        Id = Guid.NewGuid();
        UserId = userId;
        Provider = provider;
        SetName(name);
        SetAddress(address);
        IsEnabled = true;
        CreatedAtUtc = createdAtUtc;
        UpdatedAtUtc = createdAtUtc;
    }

    public Guid Id { get; private set; } = Guid.NewGuid();

    public Guid UserId { get; private set; }

    public DeliveryTargetProvider Provider { get; private set; }

    public string Name { get; private set; } = null!;

    public string Address { get; private set; } = null!;

    public bool IsEnabled { get; private set; }

    /// <summary>Advisory only -- the caller (application service) is responsible for unsetting any other default for this user.</summary>
    public bool IsDefault { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public uint Version { get; private set; }

    public void UpdateAddress(string address, DateTimeOffset atUtc)
    {
        SetAddress(address);
        UpdatedAtUtc = atUtc;
    }

    public void Rename(string name, DateTimeOffset atUtc)
    {
        SetName(name);
        UpdatedAtUtc = atUtc;
    }

    public void SetEnabled(bool enabled, DateTimeOffset atUtc)
    {
        IsEnabled = enabled;
        UpdatedAtUtc = atUtc;
    }

    public void SetDefault(bool isDefault, DateTimeOffset atUtc)
    {
        IsDefault = isDefault;
        UpdatedAtUtc = atUtc;
    }

    private void SetName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("A name is required.", nameof(name));
        }

        var trimmed = name.Trim();
        if (trimmed.Length > MaxNameLength)
        {
            throw new ArgumentException($"The name may not exceed {MaxNameLength} characters.", nameof(name));
        }

        Name = trimmed;
    }

    private void SetAddress(string address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            throw new ArgumentException("An address is required.", nameof(address));
        }

        var trimmed = address.Trim();
        if (trimmed.Length > MaxAddressLength)
        {
            throw new ArgumentException($"The address may not exceed {MaxAddressLength} characters.", nameof(address));
        }

        Address = trimmed;
    }
}
