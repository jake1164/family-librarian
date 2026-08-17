namespace FamilyLibrarian.Web.Logging;

internal static partial class AuthenticationLog
{
    [LoggerMessage(
        EventId = 2001,
        Level = LogLevel.Warning,
        Message = "Login attempt for unknown email {Email}.")]
    internal static partial void LoginUnknownAccount(ILogger logger, string email);

    [LoggerMessage(
        EventId = 2002,
        Level = LogLevel.Warning,
        Message = "Login attempt for disabled account {UserId}.")]
    internal static partial void LoginDisabledAccount(ILogger logger, Guid userId);

    [LoggerMessage(
        EventId = 2003,
        Level = LogLevel.Warning,
        Message = "Account {UserId} is locked out after too many failed attempts.")]
    internal static partial void LoginLockedOut(ILogger logger, Guid userId);

    [LoggerMessage(
        EventId = 2004,
        Level = LogLevel.Warning,
        Message = "Invalid password for account {UserId}.")]
    internal static partial void LoginInvalidPassword(ILogger logger, Guid userId);

    [LoggerMessage(
        EventId = 2005,
        Level = LogLevel.Information,
        Message = "Account {UserId} signed in.")]
    internal static partial void LoginSucceeded(ILogger logger, Guid userId);

    [LoggerMessage(
        EventId = 2006,
        Level = LogLevel.Warning,
        Message = "The OIDC callback carried no external login ticket.")]
    internal static partial void OidcCallbackWithoutTicket(ILogger logger);

    [LoggerMessage(
        EventId = 2007,
        Level = LogLevel.Information,
        Message = "Account {UserId} signed in via OIDC.")]
    internal static partial void OidcSignInSucceeded(ILogger logger, Guid userId);

    [LoggerMessage(
        EventId = 2008,
        Level = LogLevel.Warning,
        Message = "OIDC sign-in was rejected: {Reason}.")]
    internal static partial void OidcSignInRejected(ILogger logger, string reason);
}
