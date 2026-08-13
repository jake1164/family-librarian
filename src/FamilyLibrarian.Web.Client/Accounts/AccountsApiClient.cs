using System.Net;
using System.Net.Http.Json;
using FamilyLibrarian.Contracts.Accounts;
using FamilyLibrarian.Web.Client.Authentication;

namespace FamilyLibrarian.Web.Client.Accounts;

/// <summary>
/// Typed client for family account and invitation management.
/// </summary>
/// <remarks>
/// The invitation token is readable exactly once, in the response to
/// <see cref="CreateInvitationAsync"/>. Nothing here can fetch it again, because
/// the host stores only its hash.
/// </remarks>
public sealed class AccountsApiClient(HttpClient httpClient, AntiforgeryTokenProvider antiforgery)
{
    public async Task<IReadOnlyList<FamilyAccountResponse>> GetAccountsAsync(
        CancellationToken cancellationToken = default)
    {
        var response = await httpClient.GetFromJsonAsync<FamilyAccountListResponse>(
            "api/v1/admin/accounts/",
            cancellationToken);

        return response?.Accounts ?? [];
    }

    public async Task<IReadOnlyList<InvitationResponse>> GetInvitationsAsync(
        CancellationToken cancellationToken = default)
    {
        var response = await httpClient.GetFromJsonAsync<InvitationListResponse>(
            "api/v1/admin/invitations/",
            cancellationToken);

        return response?.Invitations ?? [];
    }

    public async Task<CreateInvitationOutcome> CreateInvitationAsync(
        string email,
        bool asAdmin,
        CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(
            HttpMethod.Post,
            "api/v1/admin/invitations/",
            new CreateInvitationRequest(email, asAdmin),
            cancellationToken);

        return response.IsSuccessStatusCode
            ? new CreateInvitationOutcome(
                true,
                await response.Content.ReadFromJsonAsync<CreatedInvitationResponse>(cancellationToken),
                null)
            : new CreateInvitationOutcome(false, null, await ReadErrorAsync(response, cancellationToken));
    }

    /// <summary>
    /// Issues a replacement link, withdrawing the previous one.
    /// </summary>
    /// <remarks>
    /// Returns a new token, on the same one-time terms as
    /// <see cref="CreateInvitationAsync"/>.
    /// </remarks>
    public async Task<CreateInvitationOutcome> RegenerateInvitationAsync(
        Guid invitationId,
        CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync<object>(
            HttpMethod.Post,
            $"api/v1/admin/invitations/{invitationId}/regenerate",
            null,
            cancellationToken);

        return response.IsSuccessStatusCode
            ? new CreateInvitationOutcome(
                true,
                await response.Content.ReadFromJsonAsync<CreatedInvitationResponse>(cancellationToken),
                null)
            : new CreateInvitationOutcome(false, null, await ReadErrorAsync(response, cancellationToken));
    }

    public Task<AccountActionOutcome> RevokeInvitationAsync(
        Guid invitationId,
        CancellationToken cancellationToken = default) =>
        SendForOutcomeAsync<object>(
            HttpMethod.Post,
            $"api/v1/admin/invitations/{invitationId}/revoke",
            null,
            cancellationToken);

    public Task<AccountActionOutcome> SetStatusAsync(
        Guid userId,
        string status,
        CancellationToken cancellationToken = default) =>
        SendForOutcomeAsync(
            HttpMethod.Put,
            $"api/v1/admin/accounts/{userId}/status",
            new SetAccountStatusRequest(status),
            cancellationToken);

    public Task<AccountActionOutcome> SetAdminAsync(
        Guid userId,
        bool isAdmin,
        CancellationToken cancellationToken = default) =>
        SendForOutcomeAsync(
            HttpMethod.Put,
            $"api/v1/admin/accounts/{userId}/admin",
            new SetAccountAdminRequest(isAdmin),
            cancellationToken);

    public Task<AccountActionOutcome> ResetPasswordAsync(
        Guid userId,
        string password,
        CancellationToken cancellationToken = default) =>
        SendForOutcomeAsync(
            HttpMethod.Put,
            $"api/v1/admin/accounts/{userId}/password",
            new ResetAccountPasswordRequest(password),
            cancellationToken);

    /// <summary>Reads the invitation behind a token. Anonymous — no token header.</summary>
    public async Task<InvitationPreviewResponse?> PreviewInvitationAsync(
        string token,
        CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.GetAsync(
            $"api/v1/invitations/preview?token={Uri.EscapeDataString(token)}",
            cancellationToken);

        return response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync<InvitationPreviewResponse>(cancellationToken)
            : null;
    }

    public async Task<AccountActionOutcome> RedeemInvitationAsync(
        string token,
        string displayName,
        string password,
        CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.PostAsJsonAsync(
            "api/v1/invitations/redeem",
            new RedeemInvitationRequest(token, displayName, password),
            cancellationToken);

        return response.IsSuccessStatusCode
            ? new AccountActionOutcome(true, null)
            : new AccountActionOutcome(false, await ReadErrorAsync(response, cancellationToken));
    }

    private async Task<AccountActionOutcome> SendForOutcomeAsync<TPayload>(
        HttpMethod method,
        string path,
        TPayload? payload,
        CancellationToken cancellationToken)
        where TPayload : class
    {
        using var response = await SendAsync(method, path, payload, cancellationToken);

        return response.IsSuccessStatusCode
            ? new AccountActionOutcome(true, null)
            : new AccountActionOutcome(false, await ReadErrorAsync(response, cancellationToken));
    }

    private async Task<HttpResponseMessage> SendAsync<TPayload>(
        HttpMethod method,
        string path,
        TPayload? payload,
        CancellationToken cancellationToken)
        where TPayload : class
    {
        using var request = new HttpRequestMessage(method, path);
        await antiforgery.AttachAsync(request, cancellationToken);

        if (payload is not null)
        {
            request.Content = JsonContent.Create(payload);
        }

        return await httpClient.SendAsync(request, cancellationToken);
    }

    private static async Task<string> ReadErrorAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            return "Too many attempts. Wait a minute and try again.";
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return "That account or invitation no longer exists.";
        }

        try
        {
            var problem = await response.Content.ReadFromJsonAsync<ValidationProblemPayload>(
                cancellationToken);
            var message = problem?.Errors?.Values
                .SelectMany(messages => messages)
                .FirstOrDefault();

            if (!string.IsNullOrWhiteSpace(message))
            {
                return message;
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or NotSupportedException
            or System.Text.Json.JsonException)
        {
            // Fall through to the generic message.
        }

        return "That change could not be saved. Please try again.";
    }

    private sealed record ValidationProblemPayload(Dictionary<string, string[]>? Errors);
}

public sealed record AccountActionOutcome(bool Succeeded, string? Error);

public sealed record CreateInvitationOutcome(
    bool Succeeded,
    CreatedInvitationResponse? Invitation,
    string? Error);
