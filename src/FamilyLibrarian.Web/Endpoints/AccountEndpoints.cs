using FamilyLibrarian.Application.Accounts;
using FamilyLibrarian.Application.Delivery;
using FamilyLibrarian.Contracts.Accounts;
using FamilyLibrarian.Contracts.Delivery;
using FamilyLibrarian.Domain.Accounts;
using FamilyLibrarian.Domain.Delivery;

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
        adminAccounts.MapPut("/{userId:guid}/delivery/kindle", SetAccountKindleAddressAsync);
        adminAccounts.MapPut("/{userId:guid}/delivery/kindle/enabled", SetAccountKindleEnabledAsync);
    }

    private static async Task<IResult> ListAccountsAsync(
        AccountAdminService accountAdmin,
        DeliveryTargetService deliveryTargets,
        CancellationToken cancellationToken)
    {
        var accounts = await accountAdmin.ListAsync(cancellationToken);
        var kindleTargets = await deliveryTargets.AdminListKindleTargetsAsync(cancellationToken);
        return Results.Ok(new FamilyAccountListResponse(
            accounts.Select(account => ToAccountResponse(account, kindleTargets)).ToArray()));
    }

    private static async Task<IResult> SetAccountKindleAddressAsync(
        Guid userId,
        AdminSetKindleAddressRequest request,
        DeliveryTargetService deliveryTargets,
        CancellationToken cancellationToken)
    {
        var result = await deliveryTargets.AdminSetKindleAddressAsync(
            userId, request.Address, request.ExpectedVersion, cancellationToken);

        return result.Outcome switch
        {
            SetKindleTargetOutcome.Success => Results.Ok(ToKindleResponse(result.Target!)),
            SetKindleTargetOutcome.Conflict => Results.Conflict(new { message = result.Error }),
            _ => Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["address"] = [result.Error ?? "That address could not be saved."]
            })
        };
    }

    private static async Task<IResult> SetAccountKindleEnabledAsync(
        Guid userId,
        SetKindleEnabledRequest request,
        DeliveryTargetService deliveryTargets,
        CancellationToken cancellationToken)
    {
        var result = await deliveryTargets.AdminSetKindleEnabledAsync(
            userId, request.Enabled, request.ExpectedVersion, cancellationToken);

        return result.Outcome switch
        {
            SetKindleTargetOutcome.Success => Results.Ok(ToKindleResponse(result.Target!)),
            SetKindleTargetOutcome.NotFound => Results.NotFound(),
            SetKindleTargetOutcome.Conflict => Results.Conflict(new { message = result.Error }),
            _ => Results.Problem(statusCode: StatusCodes.Status500InternalServerError)
        };
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

    private static FamilyAccountResponse ToAccountResponse(
        UserAccount account, IReadOnlyDictionary<Guid, DeliveryTarget> kindleTargets) => new(
        account.Id,
        account.Email,
        account.DisplayName,
        account.Status.ToString(),
        account.IsAdmin,
        account.CreatedAtUtc,
        account.LastLoginAtUtc,
        kindleTargets.TryGetValue(account.Id, out var target) ? ToKindleResponse(target) : null);

    private static KindleDeliverySummaryResponse ToKindleResponse(DeliveryTarget target) => new(
        target.Address, target.IsEnabled, target.Version);
}
