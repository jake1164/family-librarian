using System.Net.Mail;
using FamilyLibrarian.Domain.Communications;

namespace FamilyLibrarian.Domain.Setup;

/// <summary>
/// What an administrator tells family members while they set up their own delivery and messaging: which address to
/// approve for Kindle, which Amazon store to open, and how to sign in to the messaging server. One row, created on the
/// first save.
/// </summary>
/// <remarks>
/// Every value here is shown to any signed-in user, so none of it may be a secret. It is kept apart from the CWA and
/// Matrix connection settings so that editing guidance never discards a connection test result.
/// </remarks>
public sealed class SetupGuidanceSettings
{
    /// <summary>
    /// The only row's key. Using one fixed value makes the primary key refuse a second row, however two first saves
    /// are timed.
    /// </summary>
    public static readonly Guid SingletonId = new("5e7d1c0a-9b3f-4f64-8c2a-71d6a0b4e9f2");

    public const int MaxEmailLength = 320;
    public const int MaxStoreDomainLength = 64;
    public const int MaxServerNameLength = 255;
    public const int MaxInstructionsLength = 1_000;
    public const int MaxUrlLength = 512;

    private SetupGuidanceSettings()
    {
    }

    public SetupGuidanceSettings(DateTimeOffset createdAtUtc)
    {
        Id = SingletonId;
        CreatedAtUtc = createdAtUtc;
        UpdatedAtUtc = createdAtUtc;
    }

    public Guid Id { get; private set; }

    /// <summary>The address Kindle books are sent from, which each recipient has to approve in Amazon.</summary>
    public string? KindleSenderAddress { get; private set; }

    /// <summary><see langword="null"/> means <see cref="AmazonStore.DefaultDomain"/>.</summary>
    public string? AmazonStoreDomain { get; private set; }

    /// <summary><see langword="null"/> means the server part of the bot's Matrix ID.</summary>
    public string? MessagingServerName { get; private set; }

    public string? MessagingSignInInstructions { get; private set; }

    public string? MessagingHelpUrl { get; private set; }

    public string? MessagingWebAppUrl { get; private set; }

    /// <summary>Whether to walk people through installing Element X. Off for servers that app cannot use.</summary>
    public bool ShowElementHelp { get; private set; } = true;

    public Guid? UpdatedByUserId { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public uint Version { get; private set; }

    /// <summary>Returns why the values cannot be saved, or <see langword="null"/> after saving them.</summary>
    public string? TrySetKindle(string? senderAddress, string? amazonStore, Guid? actorUserId, DateTimeOffset updatedAtUtc)
    {
        var sender = Trim(senderAddress);
        if (sender is not null &&
            (sender.Length > MaxEmailLength || !MailAddress.TryCreate(sender, out var parsed) || parsed.Address != sender))
        {
            return "Enter the sender as a plain email address, such as library@example.com.";
        }

        if (!AmazonStore.TryNormalize(amazonStore, out var domain))
        {
            return "Enter an Amazon store such as amazon.co.uk, or paste any link from that store.";
        }

        KindleSenderAddress = sender;
        AmazonStoreDomain = domain;
        Touch(actorUserId, updatedAtUtc);
        return null;
    }

    /// <summary>Returns why the values cannot be saved, or <see langword="null"/> after saving them.</summary>
    public string? TrySetMessaging(
        string? serverName,
        string? signInInstructions,
        string? helpUrl,
        string? webAppUrl,
        Guid? actorUserId,
        DateTimeOffset updatedAtUtc)
    {
        var server = Trim(serverName);
        if (server is not null && !MatrixUserIds.IsValidServerName(server))
        {
            return "Enter the server name only, such as example.org, without https:// or a path.";
        }

        var instructions = Trim(signInInstructions?.ReplaceLineEndings("\n"));
        if (instructions is { Length: > MaxInstructionsLength })
        {
            return $"Keep the sign-in instructions to {MaxInstructionsLength:N0} characters or fewer.";
        }

        if (!TryNormalizeWebUrl(helpUrl, out var help))
        {
            return "The help link must be a full web address starting with https:// or http://.";
        }

        if (!TryNormalizeWebUrl(webAppUrl, out var webApp))
        {
            return "The web app address must be a full web address starting with https:// or http://.";
        }

        MessagingServerName = server;
        MessagingSignInInstructions = instructions;
        MessagingHelpUrl = help;
        MessagingWebAppUrl = webApp;
        Touch(actorUserId, updatedAtUtc);
        return null;
    }

    public void SetShowElementHelp(bool show, Guid? actorUserId, DateTimeOffset updatedAtUtc)
    {
        ShowElementHelp = show;
        Touch(actorUserId, updatedAtUtc);
    }

    private static bool TryNormalizeWebUrl(string? value, out string? normalized)
    {
        normalized = Trim(value);
        return normalized is null ||
            (normalized.Length <= MaxUrlLength &&
             Uri.TryCreate(normalized, UriKind.Absolute, out var uri) &&
             uri.Scheme is "http" or "https" &&
             uri.UserInfo.Length == 0);
    }

    private void Touch(Guid? actorUserId, DateTimeOffset updatedAtUtc)
    {
        UpdatedByUserId = actorUserId;
        UpdatedAtUtc = updatedAtUtc;
    }

    private static string? Trim(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
