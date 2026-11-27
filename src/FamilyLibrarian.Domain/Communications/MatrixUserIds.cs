using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace FamilyLibrarian.Domain.Communications;

/// <summary>
/// Lets a person type just their username. The server part is added for them, while a full ID is still accepted as
/// typed for accounts on another server.
/// </summary>
public static partial class MatrixUserIds
{
    /// <summary>The protocol's limit for a whole user ID, and for a server name on its own.</summary>
    private const int MaxLength = 255;

    /// <summary>
    /// A DNS name, IPv4 address or bracketed IPv6 address, with an optional port from 1 to 65535. Syntax only: it
    /// does not show that such a server exists.
    /// </summary>
    public static bool IsValidServerName(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > MaxLength)
        {
            return false;
        }

        string rest;
        if (value[0] == '[')
        {
            var close = value.IndexOf(']', StringComparison.Ordinal);
            if (close < 0 || !IsIpv6(value[1..close]))
            {
                return false;
            }

            rest = value[(close + 1)..];
        }
        else
        {
            var colon = value.IndexOf(':', StringComparison.Ordinal);
            if (!IsHostName(colon < 0 ? value : value[..colon]))
            {
                return false;
            }

            rest = colon < 0 ? string.Empty : value[colon..];
        }

        return rest.Length == 0 || (rest[0] == ':' && IsPort(rest[1..]));
    }

    private static bool IsIpv6(string address) =>
        address.All(character => char.IsAsciiHexDigit(character) || character is ':' or '.') &&
        IPAddress.TryParse(address, out var parsed) && parsed.AddressFamily == AddressFamily.InterNetworkV6;

    private static bool IsHostName(string host)
    {
        var labels = host.Split('.');
        if (!labels.All(label => label.Length is > 0 and <= 63 &&
                label[0] != '-' && label[^1] != '-' &&
                label.All(character => char.IsAsciiLetterOrDigit(character) || character == '-')))
        {
            return false;
        }

        // Digits and dots only is meant to be an IPv4 address, so it has to be one.
        return !labels.All(label => label.All(char.IsAsciiDigit)) ||
            (labels.Length == 4 && labels.All(label => label.Length <= 3 && int.Parse(label, CultureInfo.InvariantCulture) <= 255));
    }

    private static bool IsPort(string port) =>
        port.Length is > 0 and <= 5 && port.All(char.IsAsciiDigit) &&
        int.Parse(port, CultureInfo.InvariantCulture) is >= 1 and <= 65535;

    /// <summary>The server part of a full ID such as <c>@bot:example.org</c>.</summary>
    public static string? ServerNameOf(string? userId)
    {
        if (userId is null || !userId.StartsWith('@'))
        {
            return null;
        }

        var separator = userId.IndexOf(':', StringComparison.Ordinal);
        return separator > 1 && separator < userId.Length - 1 ? userId[(separator + 1)..] : null;
    }

    /// <summary>The short form to show: just the username for an account on <paramref name="serverName"/>.</summary>
    public static string DisplayName(string userId, string? serverName) =>
        serverName is not null && string.Equals(ServerNameOf(userId), serverName, StringComparison.OrdinalIgnoreCase)
            ? userId[1..userId.IndexOf(':', StringComparison.Ordinal)]
            : userId;

    public static bool TryResolve(string? input, string? serverName, out string userId, out string? error)
    {
        userId = string.Empty;
        var value = input?.Trim() ?? string.Empty;
        if (value.Length == 0)
        {
            error = "Enter your username.";
            return false;
        }

        if (value.Any(char.IsWhiteSpace))
        {
            error = "A username cannot contain spaces. Enter it exactly as your messaging app shows it.";
            return false;
        }

        if (value.Length > MaxLength)
        {
            error = TooLongMessage;
            return false;
        }

        if (value.Contains(':', StringComparison.Ordinal))
        {
            var full = value.StartsWith('@') ? value : $"@{value}";
            // Older accounts may use any printable ASCII before the colon, so only the shape is checked there.
            if (ServerNameOf(full) is not { } fullServer || !IsValidServerName(fullServer) || full.LastIndexOf('@') != 0 ||
                !full[1..full.IndexOf(':', StringComparison.Ordinal)].All(character => character is > ' ' and <= '~'))
            {
                error = "That is not a complete ID. It should look like @you:example.org.";
                return false;
            }

            return Accept(full, out userId, out error);
        }

        var localPart = value.StartsWith('@') ? value[1..] : value;
        if (localPart.Contains('@', StringComparison.Ordinal))
        {
            error = "Enter your messaging username, not your email address.";
            return false;
        }

        // Phone keyboards capitalize the first letter; new usernames are always lower case.
        localPart = localPart.ToLowerInvariant();
        if (!LocalPartPattern().IsMatch(localPart))
        {
            error = "A username can only use letters, numbers and . _ = - / +";
            return false;
        }

        if (serverName is null)
        {
            error = "Enter your full ID, such as @you:example.org.";
            return false;
        }

        if (!IsValidServerName(serverName))
        {
            error = "The messaging server name isn't set up correctly. Ask an administrator, or enter your full ID.";
            return false;
        }

        return Accept($"@{localPart}:{serverName}", out userId, out error);
    }

    private const string TooLongMessage = "That is too long to be a messaging ID.";

    // The limit applies to the finished ID: adding the server to a long username can pass it.
    private static bool Accept(string candidate, out string userId, out string? error)
    {
        var fits = candidate.Length <= MaxLength;
        userId = fits ? candidate : string.Empty;
        error = fits ? null : TooLongMessage;
        return fits;
    }

    [GeneratedRegex(@"^[a-z0-9._=\-/+]+\z", RegexOptions.CultureInvariant)]
    private static partial Regex LocalPartPattern();
}
