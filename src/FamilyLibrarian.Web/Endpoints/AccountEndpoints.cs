using FamilyLibrarian.Application.Accounts;
using FamilyLibrarian.Contracts.Accounts;
using FamilyLibrarian.Domain.Accounts;

namespace FamilyLibrarian.Web.Endpoints;

internal static class AccountEndpoints
{
    public static void MapAccountEndpoints(this IEndpointRouteBuilder app)
    {
        // Admin account management.
        var adminAccounts = app.MapGroup("/api/v1/admin/accounts")
            .RequireAuthorization("Admin")
            .AddEndpointFilter<AntiforgeryEndpointFilter>();

        adminAccounts.MapGet("/", ListAccountsAsync);
        adminAccounts.MapPut("/{userId:guid}/status", SetAccountStatusAsync);
        adminAccounts.MapPut("/{userId:guid}/admin", SetAccountAdminAsync);
        adminAccounts.MapPut("/{userId:guid}/password", ResetAccountPasswordAsync);
    }

    private static async Task<IResult> ListAccountsAsync(
        AccountAdminService accountAdmin,
        CancellationToken cancellationToken)
    {
        var accounts = await accountAdmin.ListAsync(cancellationToken);
        return Results.Ok(new FamilyAccountListResponse(accounts.Select(ToAccountResponse).ToArray()));
    }

    private static async Task<IResult> SetAccountStatusAsync(
        Guid userId,
        SetAccountStatusRequest request,
        AccountAdminService accountAdmin,
        CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<UserStatus>(request.Status, ignoreCase: true, out var status))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["status"] = ["That is not an account status."]
            });
        }

        return ToAccountResult(await accountAdmin.SetStatusAsync(userId, status, cancellationToken));
    }

    private static async Task<IResult> SetAccountAdminAsync(
        Guid userId,
        SetAccountAdminRequest request,
        AccountAdminService accountAdmin,
        CancellationToken cancellationToken) =>
        ToAccountResult(await accountAdmin.SetAdminAsync(userId, request.IsAdmin, cancellationToken));

    private static async Task<IResult> ResetAccountPasswordAsync(
        Guid userId,
        ResetAccountPasswordRequest request,
        AccountAdminService accountAdmin,
        CancellationToken cancellationToken) =>
        ToAccountResult(await accountAdmin.SetPasswordAsync(userId, request.Password, cancellationToken));

    private static IResult ToAccountResult(AccountOperationResult result) => result.Succeeded
        ? Results.NoContent()
        : Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["account"] = [result.Error ?? "That change could not be saved."]
        });

    private static FamilyAccountResponse ToAccountResponse(UserAccount account) => new(
        account.Id,
        account.Email,
        account.DisplayName,
        account.Status.ToString(),
        account.IsAdmin,
        account.CreatedAtUtc,
        account.LastLoginAtUtc);
}
