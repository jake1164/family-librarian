using System.Security.Claims;
using System.Text.Json;
using System.ComponentModel.DataAnnotations;
using System.Threading.RateLimiting;
using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Accounts;
using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Application.Feedback;
using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Application.Requests;
using FamilyLibrarian.Contracts.Accounts;
using FamilyLibrarian.Contracts.Authentication;
using FamilyLibrarian.Contracts.Catalog;
using FamilyLibrarian.Contracts.Feedback;
using FamilyLibrarian.Contracts.Integrations;
using FamilyLibrarian.Contracts.Requests;
using FamilyLibrarian.Domain;
using FamilyLibrarian.Domain.Accounts;
using FamilyLibrarian.Domain.Requests;
using FamilyLibrarian.Infrastructure;
using FamilyLibrarian.Infrastructure.Identity;
using FamilyLibrarian.Infrastructure.Integrations;
using FamilyLibrarian.Infrastructure.Persistence;
using FamilyLibrarian.Web;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddHealthChecks()
    .AddCheck<DatabaseHealthCheck>("postgresql");

// Invitation redemption is necessarily anonymous, which makes it the one write
// endpoint an unauthenticated caller can reach. The token is 256 bits, so this
// is not what stops a guess succeeding — it stops a client burning host
// resources trying, and it bounds the damage if a token ever leaks into a log.
var redemptionAttemptsPerMinute = builder.Configuration
    .GetSection(InvitationPolicy.SectionName)
    .GetValue("RedemptionAttemptsPerMinute", InvitationPolicy.DefaultRedemptionAttemptsPerMinute);

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy(InvitationEndpoints.RateLimitPolicy, httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            // Partitioned by caller so one busy address cannot lock everyone
            // else out of redeeming their invitation. Note that a household
            // behind one NAT address shares a bucket, which is why the ceiling
            // is configurable rather than fixed.
            httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = redemptionAttemptsPerMinute,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            }));
});

// The WebAssembly client cannot read an HttpOnly cookie, so the request token
// travels in a header it sets explicitly. The cookie half stays HttpOnly.
builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = AntiforgeryTokenEndpoint.HeaderName;
    options.Cookie.Name = AntiforgeryTokenEndpoint.CookieName;
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Strict;
    // Secure on HTTPS, absent on plain-HTTP local development, so the same
    // configuration works behind a TLS-terminating proxy and on localhost.
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
});

var app = builder.Build();

if (args.Contains("--migrate", StringComparer.Ordinal))
{
    await app.Services.MigrateDatabaseAsync();
    return;
}

if (app.Configuration.GetValue<bool>("Authentication:EnableLocal"))
{
    await app.Services.InitializeIdentityAsync(app.Configuration);
}

app.UseExceptionHandler("/error");

if (app.Environment.IsDevelopment())
{
    app.UseWebAssemblyDebugging();
}

app.UseHttpsRedirection();
app.UseBlazorFrameworkFiles();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();
app.UseRateLimiter();

app.MapGet("/health/live", () => Results.Ok(new { status = "live" }))
    .AllowAnonymous();
app.MapHealthChecks("/health/ready");

// The catalog stopped being an anonymous development surface when requests
// arrived: a request needs a server-verified owner, and searching now sends
// family search terms to third-party providers on an identified user's behalf.
var catalog = app.MapGroup("/api/v1/catalog")
    .RequireAuthorization()
    .AddEndpointFilter<AntiforgeryEndpointFilter>();

catalog.MapGet("/search", SearchCatalogAsync);
catalog.MapGet("/candidates/{providerId}/{externalId}", GetCatalogCandidateAsync);
catalog.MapPost("/candidates/{providerId}/{externalId}/resolve", ResolveCatalogCandidateAsync);
catalog.MapGet("/works/{workId:guid}", GetCatalogWorkAsync);

app.MapPost("/api/auth/login", LoginAsync)
    .AllowAnonymous();
app.MapPost("/api/auth/logout", LogoutAsync)
    .RequireAuthorization();

app.MapGet("/api/v1/antiforgery/token", AntiforgeryTokenEndpoint.GetToken)
    .RequireAuthorization();

app.MapGet("/api/v1/me", GetCurrentUserAsync)
    .RequireAuthorization();
app.MapGet("/api/v1/admin/ping", () => Results.Ok(new { status = "ok" }))
    .RequireAuthorization("Admin");

// Requests. Ownership is enforced inside BookRequestService, not here: these
// routes only establish that a caller is authenticated and carries a token.
app.MapGet("/api/v1/me/requests", ListMyRequestsAsync)
    .RequireAuthorization();

var requests = app.MapGroup("/api/v1/requests")
    .RequireAuthorization()
    .AddEndpointFilter<AntiforgeryEndpointFilter>();

requests.MapPost("/", CreateBookRequestAsync);
requests.MapPost("/{requestId:guid}/transitions", ChangeBookRequestStatusAsync);

// Request review is an administrative surface, distinct from the requester's
// own routes. The service enforces the state matrix; this group supplies the
// role check and anti-forgery protection for cookie-authenticated mutations.
var adminRequests = app.MapGroup("/api/v1/admin/requests")
    .RequireAuthorization("Admin")
    .AddEndpointFilter<AntiforgeryEndpointFilter>();

adminRequests.MapGet("/", ListAdminRequestsAsync);
adminRequests.MapGet("/{requestId:guid}", GetAdminRequestAsync);
adminRequests.MapPost("/{requestId:guid}/transitions", ChangeAdminRequestStatusAsync);
adminRequests.MapPut("/{requestId:guid}/note", SetAdminRequestNoteAsync);

// My Reading: a completion date and 1-5 star rating per Work, private to the
// owner. Ownership is enforced inside UserWorkFeedbackService, same as Requests.
var feedback = app.MapGroup("/api/v1/me/feedback")
    .RequireAuthorization()
    .AddEndpointFilter<AntiforgeryEndpointFilter>();

feedback.MapGet("/", ListMyFeedbackAsync);
feedback.MapGet("/{workId:guid}", GetMyFeedbackAsync);
feedback.MapPut("/{workId:guid}", SetMyFeedbackAsync);
feedback.MapDelete("/{workId:guid}", RemoveMyFeedbackAsync);

// Invitation redemption. Anonymous by necessity — the invitee has no account
// yet — so it carries no anti-forgery requirement (there are no ambient
// credentials to forge with) and is rate limited instead.
app.MapGet("/api/v1/invitations/preview", PreviewInvitationAsync)
    .AllowAnonymous()
    .RequireRateLimiting(InvitationEndpoints.RateLimitPolicy);
app.MapPost("/api/v1/invitations/redeem", RedeemInvitationAsync)
    .AllowAnonymous()
    .RequireRateLimiting(InvitationEndpoints.RateLimitPolicy);

// Admin account management.
var adminAccounts = app.MapGroup("/api/v1/admin/accounts")
    .RequireAuthorization("Admin")
    .AddEndpointFilter<AntiforgeryEndpointFilter>();

adminAccounts.MapGet("/", ListAccountsAsync);
adminAccounts.MapPut("/{userId:guid}/status", SetAccountStatusAsync);
adminAccounts.MapPut("/{userId:guid}/admin", SetAccountAdminAsync);
adminAccounts.MapPut("/{userId:guid}/password", ResetAccountPasswordAsync);

var adminInvitations = app.MapGroup("/api/v1/admin/invitations")
    .RequireAuthorization("Admin")
    .AddEndpointFilter<AntiforgeryEndpointFilter>();

adminInvitations.MapGet("/", ListInvitationsAsync);
adminInvitations.MapPost("/", CreateInvitationAsync);
adminInvitations.MapPost("/{invitationId:guid}/revoke", RevokeInvitationAsync);
adminInvitations.MapPost("/{invitationId:guid}/regenerate", RegenerateInvitationAsync);

// Admin Metadata Integrations. Every route is Admin-only and addresses a known
// installed provider id; none accepts an arbitrary target or returns a secret.
var integrations = app.MapGroup("/api/v1/admin/integrations/metadata")
    .RequireAuthorization("Admin")
    // Cookie authentication means a cross-site request would otherwise arrive
    // already authenticated; every mutation below must carry a matching token.
    .AddEndpointFilter<AntiforgeryEndpointFilter>();

integrations.MapGet("/", ListMetadataProvidersAsync);
integrations.MapPut("/{providerId}/enabled", SetMetadataProviderEnabledAsync);
integrations.MapPut("/{providerId}/credential", SetMetadataProviderCredentialAsync);
integrations.MapDelete("/{providerId}/credential", ClearMetadataProviderCredentialAsync);
integrations.MapPost("/{providerId}/test", TestMetadataProviderAsync);

app.MapFallbackToFile("index.html");

await app.RunAsync();

static async Task<IResult> LoginAsync(
    LoginRequest request,
    UserManager<AppUser> userManager,
    SignInManager<AppUser> signInManager,
    ILoggerFactory loggerFactory)
{
    var logger = loggerFactory.CreateLogger("FamilyLibrarian.Authentication");

    if (!new EmailAddressAttribute().IsValid(request.Email) || request.Password.Length < 8)
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["email"] = ["Email is required."],
            ["password"] = ["Password is required."]
        });
    }

    var user = await userManager.FindByEmailAsync(request.Email);
    if (user is null)
    {
        AuthenticationLog.LoginUnknownAccount(logger, request.Email);
        return Results.Unauthorized();
    }

    // Checked before the password so a disabled account cannot be used as an
    // oracle for whether a password is still correct. The response is identical
    // to a wrong password either way.
    if (!UserStatuses.CanSignIn(user.Status))
    {
        AuthenticationLog.LoginDisabledAccount(logger, user.Id);
        return Results.Unauthorized();
    }

    var result = await signInManager.PasswordSignInAsync(
        user,
        request.Password,
        request.RememberMe,
        lockoutOnFailure: true);

    if (result.Succeeded)
    {
        user.LastLoginAtUtc = DateTimeOffset.UtcNow;
        await userManager.UpdateAsync(user);
        AuthenticationLog.LoginSucceeded(logger, user.Id);
        return Results.NoContent();
    }

    if (result.IsLockedOut)
    {
        AuthenticationLog.LoginLockedOut(logger, user.Id);
    }
    else
    {
        AuthenticationLog.LoginInvalidPassword(logger, user.Id);
    }

    return Results.Unauthorized();
}

static async Task<IResult> LogoutAsync(
    JsonElement requestBody,
    SignInManager<AppUser> signInManager)
{
    _ = requestBody;
    await signInManager.SignOutAsync();
    return Results.NoContent();
}

static async Task<IResult> GetCurrentUserAsync(
    ClaimsPrincipal principal,
    UserManager<AppUser> userManager)
{
    var user = await userManager.GetUserAsync(principal);
    if (user is null)
    {
        return Results.Unauthorized();
    }

    var roles = await userManager.GetRolesAsync(user);
    return Results.Ok(new CurrentUserResponse(
        user.Id,
        string.IsNullOrWhiteSpace(user.DisplayName) ? user.UserName ?? user.Email ?? "Family Librarian user" : user.DisplayName,
        user.Email,
        roles.ToArray()));
}

static async Task<IResult> ListAccountsAsync(
    AccountAdminService accountAdmin,
    CancellationToken cancellationToken)
{
    var accounts = await accountAdmin.ListAsync(cancellationToken);
    return Results.Ok(new FamilyAccountListResponse(accounts.Select(ToAccountResponse).ToArray()));
}

static async Task<IResult> SetAccountStatusAsync(
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

static async Task<IResult> SetAccountAdminAsync(
    Guid userId,
    SetAccountAdminRequest request,
    AccountAdminService accountAdmin,
    CancellationToken cancellationToken) =>
    ToAccountResult(await accountAdmin.SetAdminAsync(userId, request.IsAdmin, cancellationToken));

static async Task<IResult> ResetAccountPasswordAsync(
    Guid userId,
    ResetAccountPasswordRequest request,
    AccountAdminService accountAdmin,
    CancellationToken cancellationToken) =>
    ToAccountResult(await accountAdmin.SetPasswordAsync(userId, request.Password, cancellationToken));

static IResult ToAccountResult(AccountOperationResult result) => result.Succeeded
    ? Results.NoContent()
    : Results.ValidationProblem(new Dictionary<string, string[]>
    {
        ["account"] = [result.Error ?? "That change could not be saved."]
    });

static FamilyAccountResponse ToAccountResponse(UserAccount account) => new(
    account.Id,
    account.Email,
    account.DisplayName,
    account.Status.ToString(),
    account.IsAdmin,
    account.CreatedAtUtc,
    account.LastLoginAtUtc);

static async Task<IResult> ListInvitationsAsync(
    InvitationService invitationService,
    IClock clock,
    CancellationToken cancellationToken)
{
    var invitations = await invitationService.ListAsync(cancellationToken);
    var now = clock.UtcNow;

    return Results.Ok(new InvitationListResponse(
        invitations.Select(invitation => ToInvitationResponse(invitation, now)).ToArray()));
}

static async Task<IResult> CreateInvitationAsync(
    CreateInvitationRequest request,
    InvitationService invitationService,
    HttpContext httpContext,
    CancellationToken cancellationToken) =>
    ToInvitationResult(
        await invitationService.CreateAsync(request.Email, request.AsAdmin, cancellationToken),
        httpContext);

static async Task<IResult> RegenerateInvitationAsync(
    Guid invitationId,
    InvitationService invitationService,
    HttpContext httpContext,
    CancellationToken cancellationToken) =>
    ToInvitationResult(
        await invitationService.RegenerateAsync(invitationId, cancellationToken),
        httpContext);

static IResult ToInvitationResult(CreateInvitationResult result, HttpContext httpContext)
{
    if (!result.Succeeded)
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["email"] = [result.Error ?? "That invitation could not be created."]
        });
    }

    var invitation = result.Invitation!;
    return Results.Ok(new CreatedInvitationResponse(
        invitation.Id,
        invitation.Email,
        invitation.Role,
        invitation.ExpiresAtUtc,
        result.Token!,
        InvitationEndpoints.BuildRedeemUrl(httpContext, result.Token!)));
}

static async Task<IResult> RevokeInvitationAsync(
    Guid invitationId,
    InvitationService invitationService,
    CancellationToken cancellationToken)
{
    var result = await invitationService.RevokeAsync(invitationId, cancellationToken);

    return result.Outcome switch
    {
        InvitationCommandOutcome.Success => Results.NoContent(),
        InvitationCommandOutcome.NotFound => Results.NotFound(),
        _ => Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["invitation"] = [result.Error ?? "That invitation could not be withdrawn."]
        })
    };
}

static async Task<IResult> PreviewInvitationAsync(
    string? token,
    InvitationService invitationService,
    CancellationToken cancellationToken)
{
    var preview = await invitationService.PreviewAsync(token ?? string.Empty, cancellationToken);

    // A missing token and an unusable one answer the same way, so this endpoint
    // cannot be used to test whether a guessed token exists.
    return preview is null
        ? Results.NotFound()
        : Results.Ok(new InvitationPreviewResponse(
            preview.Email,
            preview.CanBeRedeemed,
            preview.State));
}

static async Task<IResult> RedeemInvitationAsync(
    RedeemInvitationRequest request,
    InvitationService invitationService,
    CancellationToken cancellationToken)
{
    var result = await invitationService.RedeemAsync(
        request.Token,
        request.DisplayName,
        request.Password,
        cancellationToken);

    return result.Outcome switch
    {
        RedeemInvitationOutcome.Success => Results.NoContent(),
        RedeemInvitationOutcome.InvalidInvitation => Results.ValidationProblem(
            new Dictionary<string, string[]>
            {
                ["token"] = ["This invitation link is not valid. Ask for a new one."]
            }),
        _ => Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["password"] = [result.Error ?? "That account could not be created."]
        })
    };
}

static InvitationResponse ToInvitationResponse(Invitation invitation, DateTimeOffset now) => new(
    invitation.Id,
    invitation.Email,
    invitation.Role,
    invitation.DescribeState(now),
    invitation.CanBeRedeemedAt(now),
    invitation.CreatedAtUtc,
    invitation.ExpiresAtUtc,
    invitation.RedeemedAtUtc,
    invitation.RevokedAtUtc);

static async Task<IResult> ListMyRequestsAsync(
    BookRequestService requests,
    CancellationToken cancellationToken)
{
    var mine = await requests.ListMineAsync(cancellationToken);

    return Results.Ok(new BookRequestListResponse(
        mine.Where(request => request.IsActive).Select(request => ToRequestResponse(request)).ToArray(),
        mine.Where(request => !request.IsActive).Select(request => ToRequestResponse(request)).ToArray()));
}

static async Task<IResult> CreateBookRequestAsync(
    CreateBookRequestRequest request,
    BookRequestService requests,
    CancellationToken cancellationToken)
{
    if (!TryParseMediaTypes(request.Formats, out var mediaTypes))
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["formats"] = ["Choose ebook, audiobook, or both."]
        });
    }

    var result = await requests.CreateAsync(
        request.WorkId,
        mediaTypes,
        request.Note,
        request.ConfirmDuplicate,
        cancellationToken);

    return result.Outcome switch
    {
        CreateBookRequestOutcome.Created => Results.Created(
            $"/api/v1/me/requests#{result.Request!.Id}",
            ToRequestResponse(result.Request)),
        // 409, not an error: the request is legitimate but the user should see
        // the outstanding one first and confirm they still want another.
        CreateBookRequestOutcome.DuplicateWarning => Results.Conflict(
            new BookRequestDuplicateResponse(
                "You already have an outstanding request for this book.",
                result.OverlappingFormats.Select(mediaType => mediaType.ToString()).ToArray(),
                result.Request is null ? null : ToRequestResponse(result.Request))),
        CreateBookRequestOutcome.WorkNotFound => Results.NotFound(),
        CreateBookRequestOutcome.Unauthenticated => Results.Unauthorized(),
        _ => Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["request"] = [result.Error ?? "That request could not be created."]
        })
    };
}

static async Task<IResult> ChangeBookRequestStatusAsync(
    Guid requestId,
    ChangeBookRequestStatusRequest request,
    BookRequestService requests,
    CancellationToken cancellationToken)
{
    if (!Enum.TryParse<RequestStatus>(request.Status, ignoreCase: true, out var status))
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["status"] = ["That is not a request status."]
        });
    }

    var result = await requests.TransitionAsync(
        requestId,
        status,
        request.Reason,
        request.ExpectedVersion,
        cancellationToken);

    return result.Outcome switch
    {
        BookRequestCommandOutcome.Success => Results.Ok(ToRequestResponse(result.Request!)),
        BookRequestCommandOutcome.NotFound => Results.NotFound(),
        BookRequestCommandOutcome.Unauthenticated => Results.Unauthorized(),
        BookRequestCommandOutcome.Conflict => Results.Conflict(new { message = result.Error }),
        _ => Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["status"] = [result.Error ?? "That status change is not allowed."]
        })
    };
}

static async Task<IResult> ListMyFeedbackAsync(
    UserWorkFeedbackService feedback,
    CancellationToken cancellationToken)
{
    var mine = await feedback.ListMineAsync(cancellationToken);
    return Results.Ok(new WorkFeedbackListResponse(mine.Select(ToFeedbackResponse).ToArray()));
}

static async Task<IResult> GetMyFeedbackAsync(
    Guid workId,
    UserWorkFeedbackService feedback,
    CancellationToken cancellationToken)
{
    var mine = await feedback.FindMineAsync(workId, cancellationToken);
    return mine is null ? Results.NotFound() : Results.Ok(ToFeedbackResponse(mine));
}

static async Task<IResult> SetMyFeedbackAsync(
    Guid workId,
    SetWorkFeedbackRequest request,
    UserWorkFeedbackService feedback,
    CancellationToken cancellationToken)
{
    var result = await feedback.SetFeedbackAsync(
        workId,
        request.CompletedOn,
        request.Rating,
        request.ExpectedVersion,
        cancellationToken);

    return result.Outcome switch
    {
        SetFeedbackOutcome.Success => Results.Ok(ToFeedbackResponse(result.Feedback!)),
        SetFeedbackOutcome.WorkNotFound => Results.NotFound(),
        SetFeedbackOutcome.Unauthenticated => Results.Unauthorized(),
        SetFeedbackOutcome.Conflict => Results.Conflict(new { message = result.Error }),
        _ => Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["rating"] = [result.Error ?? "That rating could not be saved."]
        })
    };
}

static async Task<IResult> RemoveMyFeedbackAsync(
    Guid workId,
    [FromBody] RemoveWorkFeedbackRequest request,
    UserWorkFeedbackService feedback,
    CancellationToken cancellationToken)
{
    var result = await feedback.RemoveFeedbackAsync(workId, request.ExpectedVersion, cancellationToken);

    return result.Outcome switch
    {
        RemoveFeedbackOutcome.Success => Results.NoContent(),
        RemoveFeedbackOutcome.NotFound => Results.NotFound(),
        RemoveFeedbackOutcome.Unauthenticated => Results.Unauthorized(),
        RemoveFeedbackOutcome.Conflict => Results.Conflict(new { message = result.Error }),
        _ => Results.Problem(statusCode: StatusCodes.Status500InternalServerError)
    };
}

static WorkFeedbackResponse ToFeedbackResponse(UserWorkFeedbackView feedback) => new(
    feedback.WorkId,
    feedback.WorkTitle,
    feedback.Authors,
    feedback.CoverUrl,
    feedback.CompletedOn,
    feedback.Rating,
    feedback.Version);

static async Task<IResult> ListAdminRequestsAsync(
    string? status,
    BookRequestService requests,
    CancellationToken cancellationToken)
{
    RequestStatus? requestedStatus = null;
    if (!string.IsNullOrWhiteSpace(status))
    {
        if (!Enum.TryParse<RequestStatus>(status, ignoreCase: true, out var parsed) ||
            !Enum.IsDefined(parsed))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["status"] = ["That is not a request status."]
            });
        }

        requestedStatus = parsed;
    }

    var queue = await requests.ListForAdminAsync(requestedStatus, cancellationToken);
    return Results.Ok(new AdminBookRequestListResponse(
        queue.Select(ToAdminRequestResponse).ToArray()));
}

static async Task<IResult> GetAdminRequestAsync(
    Guid requestId,
    BookRequestService requests,
    CancellationToken cancellationToken)
{
    var request = await requests.GetForAdminAsync(requestId, cancellationToken);
    return request is null ? Results.NotFound() : Results.Ok(ToAdminRequestResponse(request));
}

static async Task<IResult> ChangeAdminRequestStatusAsync(
    Guid requestId,
    ChangeBookRequestStatusRequest request,
    BookRequestService requests,
    CancellationToken cancellationToken)
{
    if (!Enum.TryParse<RequestStatus>(request.Status, ignoreCase: true, out var status) ||
        !Enum.IsDefined(status))
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["status"] = ["That is not a request status."]
        });
    }

    if (request.ExpectedVersion is null)
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["expectedVersion"] = ["Reload the request before changing its status."]
        });
    }

    var result = await requests.AdminTransitionAsync(
        requestId,
        status,
        request.Reason,
        request.ExpectedVersion.Value,
        cancellationToken);

    return await ToAdminCommandResult(result, requestId, requests, cancellationToken);
}

static async Task<IResult> SetAdminRequestNoteAsync(
    Guid requestId,
    SetAdminBookRequestNoteRequest request,
    BookRequestService requests,
    CancellationToken cancellationToken)
{
    var result = await requests.SetAdminNoteAsync(
        requestId,
        request.Note,
        request.ExpectedVersion,
        cancellationToken);

    return await ToAdminCommandResult(result, requestId, requests, cancellationToken);
}

static async Task<IResult> ToAdminCommandResult(
    BookRequestCommandResult result,
    Guid requestId,
    BookRequestService requests,
    CancellationToken cancellationToken)
{
    if (result.Outcome == BookRequestCommandOutcome.Success)
    {
        var updated = await requests.GetForAdminAsync(requestId, cancellationToken);
        return Results.Ok(ToAdminRequestResponse(updated!));
    }

    return result.Outcome switch
    {
        BookRequestCommandOutcome.NotFound => Results.NotFound(),
        BookRequestCommandOutcome.Unauthenticated => Results.Unauthorized(),
        BookRequestCommandOutcome.Conflict => Results.Conflict(new { message = result.Error }),
        _ => Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["request"] = [result.Error ?? "That request could not be updated."]
        })
    };
}

static bool TryParseMediaTypes(
    IReadOnlyList<string>? formats,
    out RequestMediaType[] mediaTypes)
{
    mediaTypes = [];
    if (formats is null || formats.Count is 0 or > 2)
    {
        return false;
    }

    var parsed = new List<RequestMediaType>(formats.Count);
    foreach (var format in formats)
    {
        if (!Enum.TryParse<RequestMediaType>(format, ignoreCase: true, out var mediaType) ||
            !Enum.IsDefined(mediaType))
        {
            return false;
        }

        parsed.Add(mediaType);
    }

    mediaTypes = parsed.Distinct().ToArray();
    return true;
}

static BookRequestResponse ToRequestResponse(
    BookRequestView request,
    IReadOnlyList<RequestStatus>? availableTransitions = null) => new(
    request.Id,
    request.WorkId,
    request.WorkTitle,
    request.Authors,
    request.CoverUrl,
    request.Status.ToString(),
    DescribeStatus(request.Status),
    request.IsActive,
    request.Formats
        .Select(format => new BookRequestFormatResponse(
            format.MediaType.ToString(),
            format.Status.ToString()))
        .ToArray(),
    request.RequesterNote,
    request.AdminNote,
    request.RequestedAtUtc,
    request.StatusChangedAtUtc,
    (availableTransitions ?? BookRequestService.RequesterTransitionsFrom(request.Status))
        .Select(status => status.ToString())
        .ToArray(),
    request.Version);

static AdminBookRequestResponse ToAdminRequestResponse(AdminBookRequestView request) => new(
    ToRequestResponse(
        request.Request,
        BookRequestService.AdminTransitionsFrom(request.Request.Status)),
    request.RequesterDisplayName,
    request.RequesterEmail,
    request.StatusHistory
        .Select(history => new BookRequestStatusHistoryResponse(
            history.FromStatus?.ToString(),
            history.ToStatus.ToString(),
            history.Reason,
            history.OccurredAtUtc))
        .ToArray());

// Plain language for a family, not the enum name. The status itself travels
// separately so the client never has to parse this sentence.
static string DescribeStatus(RequestStatus status) => status switch
{
    RequestStatus.PendingAcquisition => "Waiting for the librarian to find this.",
    RequestStatus.NeedsReview => "The librarian has a question about this request.",
    RequestStatus.NotAvailable => "The librarian could not find this one for now.",
    RequestStatus.Cancelled => "You cancelled this request.",
    _ => status.ToString()
};

static async Task<IResult> ListMetadataProvidersAsync(
    MetadataProviderAdminService admin,
    CancellationToken cancellationToken)
{
    var statuses = await admin.GetStatusesAsync(cancellationToken);
    return Results.Ok(new MetadataProviderListResponse(
        statuses.Select(ToProviderResponse).ToArray()));
}

static async Task<IResult> SetMetadataProviderEnabledAsync(
    string providerId,
    SetMetadataProviderEnabledRequest request,
    MetadataProviderAdminService admin,
    CancellationToken cancellationToken) =>
    ToResult(await admin.SetEnabledAsync(providerId, request.Enabled, cancellationToken));

static async Task<IResult> SetMetadataProviderCredentialAsync(
    string providerId,
    SetMetadataProviderCredentialRequest request,
    MetadataProviderAdminService admin,
    CancellationToken cancellationToken) =>
    ToResult(await admin.SetCredentialAsync(providerId, request.Credential, cancellationToken));

static async Task<IResult> ClearMetadataProviderCredentialAsync(
    string providerId,
    MetadataProviderAdminService admin,
    CancellationToken cancellationToken) =>
    ToResult(await admin.ClearCredentialAsync(providerId, cancellationToken));

static async Task<IResult> TestMetadataProviderAsync(
    string providerId,
    MetadataProviderAdminService admin,
    MetadataProviderConnectionTester tester,
    CancellationToken cancellationToken)
{
    if (await admin.GetStatusAsync(providerId, cancellationToken) is null)
    {
        return Results.NotFound();
    }

    var outcome = await tester.TestAsync(providerId, cancellationToken);
    var recorded = await admin.RecordTestResultAsync(
        providerId,
        outcome.Succeeded,
        outcome.Message,
        cancellationToken);

    return recorded.Status is null
        ? Results.NotFound()
        : Results.Ok(new MetadataProviderTestResponse(
            outcome.Succeeded,
            outcome.Message,
            ToProviderResponse(recorded.Status)));
}

static IResult ToResult(MetadataProviderCommandResult result) => result.Outcome switch
{
    MetadataProviderCommandOutcome.NotFound => Results.NotFound(),
    MetadataProviderCommandOutcome.Invalid => Results.ValidationProblem(
        new Dictionary<string, string[]>
        {
            ["provider"] = [result.Error ?? "That change is not allowed."]
        }),
    _ => Results.Ok(ToProviderResponse(result.Status!))
};

static MetadataProviderStatusResponse ToProviderResponse(MetadataProviderStatus status) => new(
    status.ProviderId,
    status.DisplayName,
    status.RequiresCredential,
    status.IsEnabled,
    status.HasStoredCredential,
    status.IsExternallyManaged,
    status.IsMisconfigured,
    status.CanManageCredential,
    status.CredentialHint,
    status.CredentialSetAtUtc,
    status.LastTestedAtUtc,
    status.LastTestSucceeded,
    status.LastTestMessage);

static async Task<IResult> SearchCatalogAsync(
    string? q,
    IActiveMetadataProviderResolver providerResolver,
    ILoggerFactory loggerFactory,
    CancellationToken cancellationToken)
{
    var searchText = q?.Trim();
    if (string.IsNullOrWhiteSpace(searchText) || searchText.Length is < 2 or > 200)
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["q"] = ["Enter between 2 and 200 characters to search the catalog."]
        });
    }

    var query = new BookSearchQuery(searchText);
    var logger = loggerFactory.CreateLogger("FamilyLibrarian.MetadataSearch");
    var providers = await providerResolver.GetActiveProvidersAsync(cancellationToken);
    var searches = providers.Select(provider =>
        SearchProviderAsync(provider, query, logger, cancellationToken));
    var providerResults = await Task.WhenAll(searches);

    return Results.Ok(new CatalogSearchResponse(
        BookCandidateGrouper.GroupExactIsbnMatches(providerResults
            .Where(result => result.Succeeded)
            .SelectMany(result => result.Candidates)
            .ToArray())
            .Select(ToResponse)
            .ToArray(),
        providerResults
            .Select(result => new CatalogProviderSearchStatusResponse(
                result.ProviderId,
                result.ProviderName,
                result.Succeeded))
            .ToArray()));
}

static async Task<IResult> GetCatalogCandidateAsync(
    string providerId,
    string externalId,
    IActiveMetadataProviderResolver providerResolver,
    ILoggerFactory loggerFactory,
    CancellationToken cancellationToken)
{
    // Resolving through the active set means a disabled provider cannot be
    // reached by addressing its id directly.
    var provider = await providerResolver.FindActiveProviderAsync(providerId, cancellationToken);
    if (provider is null)
    {
        return Results.NotFound();
    }

    try
    {
        var candidate = await provider.GetDetailsAsync(externalId, cancellationToken);
        return candidate is null ? Results.NotFound() : Results.Ok(ToResponse(candidate));
    }
    catch (HttpRequestException exception)
    {
        LogProviderFailure(loggerFactory, provider, exception);
        return Results.Problem(
            title: "Catalog provider unavailable",
            detail: "The selected catalog source is temporarily unavailable.",
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
    catch (JsonException exception)
    {
        LogProviderFailure(loggerFactory, provider, exception);
        return Results.Problem(
            title: "Catalog provider unavailable",
            detail: "The selected catalog source returned an invalid response.",
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
    catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
    {
        LogProviderFailure(loggerFactory, provider, exception);
        return Results.Problem(
            title: "Catalog provider unavailable",
            detail: "The selected catalog source timed out.",
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
}

static async Task<IResult> ResolveCatalogCandidateAsync(
    string providerId,
    string externalId,
    CatalogWorkResolver resolver,
    ILoggerFactory loggerFactory,
    CancellationToken cancellationToken)
{
    try
    {
        var result = await resolver.ResolveAsync(providerId, externalId, cancellationToken);
        var response = await ToWorkResponseAsync(result.Work, resolver, cancellationToken);
        return result.WasCreated
            ? Results.Created($"/api/v1/catalog/works/{result.Work.Id}", response)
            : Results.Ok(response);
    }
    catch (UnknownMetadataProviderException)
    {
        return Results.NotFound();
    }
    catch (CatalogCandidateNotFoundException)
    {
        return Results.NotFound();
    }
    catch (ArgumentException)
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["providerReference"] = ["The provider reference is invalid."]
        });
    }
    catch (HttpRequestException exception)
    {
        MetadataProviderLog.CandidateDetailsUnavailable(
            loggerFactory.CreateLogger("FamilyLibrarian.MetadataSearch"),
            providerId,
            exception);
        return Results.Problem(
            title: "Catalog provider unavailable",
            detail: "The selected catalog source is temporarily unavailable.",
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
    catch (JsonException exception)
    {
        MetadataProviderLog.CandidateDetailsUnavailable(
            loggerFactory.CreateLogger("FamilyLibrarian.MetadataSearch"),
            providerId,
            exception);
        return Results.Problem(
            title: "Catalog provider unavailable",
            detail: "The selected catalog source returned an invalid response.",
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
    catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
    {
        MetadataProviderLog.CandidateDetailsUnavailable(
            loggerFactory.CreateLogger("FamilyLibrarian.MetadataSearch"),
            providerId,
            exception);
        return Results.Problem(
            title: "Catalog provider unavailable",
            detail: "The selected catalog source timed out.",
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
}

static async Task<IResult> GetCatalogWorkAsync(
    Guid workId,
    CatalogWorkResolver resolver,
    CancellationToken cancellationToken)
{
    var work = await resolver.GetWorkAsync(workId, cancellationToken);
    return work is null
        ? Results.NotFound()
        : Results.Ok(await ToWorkResponseAsync(work, resolver, cancellationToken));
}

static async Task<ProviderSearchResult> SearchProviderAsync(
    IBookMetadataProvider provider,
    BookSearchQuery query,
    ILogger logger,
    CancellationToken cancellationToken)
{
    try
    {
        var candidates = await provider.SearchAsync(query, cancellationToken);
        return new ProviderSearchResult(
            provider.Id,
            provider.DisplayName,
            true,
            candidates);
    }
    catch (HttpRequestException exception)
    {
        MetadataProviderLog.SearchUnavailable(logger, provider.Id, exception);
    }
    catch (JsonException exception)
    {
        MetadataProviderLog.SearchReturnedInvalidJson(logger, provider.Id, exception);
    }
    catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
    {
        MetadataProviderLog.SearchTimedOut(logger, provider.Id, exception);
    }

    return new ProviderSearchResult(provider.Id, provider.DisplayName, false, []);
}

static void LogProviderFailure(
    ILoggerFactory loggerFactory,
    IBookMetadataProvider provider,
    Exception exception) =>
    MetadataProviderLog.CandidateDetailsUnavailable(
        loggerFactory.CreateLogger("FamilyLibrarian.MetadataSearch"),
        provider.Id,
        exception);

static CatalogBookCandidateResponse ToResponse(BookCandidate candidate) => new(
    candidate.ProviderId,
    candidate.ProviderName,
    candidate.ExternalId,
    candidate.Title,
    candidate.Authors,
    candidate.Description,
    candidate.CoverUrl,
    candidate.PublicationDate,
    candidate.Editions.Select(edition => new CatalogEditionResponse(
        edition.Title,
        edition.Isbn13,
        edition.Format,
        edition.PublicationDate)).ToArray(),
    candidate.Series.Select(series => new CatalogSeriesResponse(
        series.Name,
        series.PositionLabel,
        series.IsPrimary)).ToArray());

static async Task<CatalogWorkResponse> ToWorkResponseAsync(
    FamilyLibrarian.Domain.Catalog.Work work,
    CatalogWorkResolver resolver,
    CancellationToken cancellationToken)
{
    var sources = await resolver.GetWorkSourcesAsync(work.Id, cancellationToken);
    return new CatalogWorkResponse(
        work.Id,
        work.CanonicalTitle,
        work.Authors
            .OrderBy(author => author.Ordinal)
            .Select(author => author.Author.CanonicalName)
            .ToArray(),
        work.Description,
        work.CoverUrl,
        work.FirstPublicationDate,
        work.Editions
            .OrderBy(edition => edition.PublicationDate)
            .ThenBy(edition => edition.Title, StringComparer.Ordinal)
            .Select(edition => new CatalogEditionResponse(
                edition.Title,
                edition.Isbn13,
                edition.Format.ToString(),
                edition.PublicationDate))
            .ToArray(),
        work.SeriesEntries
            .OrderByDescending(entry => entry.IsPrimary)
            .ThenBy(entry => entry.PositionSort)
            .ThenBy(entry => entry.PositionLabel, StringComparer.Ordinal)
            .Select(entry => new CatalogSeriesResponse(
                entry.Series.Name,
                entry.PositionLabel,
                entry.IsPrimary))
            .ToArray(),
        sources.Select(source => new CatalogWorkSourceResponse(
            source.ProviderId,
            source.ExternalId,
            source.ObservedAtUtc)).ToArray());
}

internal sealed record ProviderSearchResult(
    string ProviderId,
    string ProviderName,
    bool Succeeded,
    IReadOnlyList<BookCandidate> Candidates);

internal static partial class MetadataProviderLog
{
    [LoggerMessage(
        EventId = 1001,
        Level = LogLevel.Warning,
        Message = "Metadata provider {ProviderId} was unavailable during search.")]
    internal static partial void SearchUnavailable(
        ILogger logger,
        string providerId,
        Exception exception);

    [LoggerMessage(
        EventId = 1002,
        Level = LogLevel.Warning,
        Message = "Metadata provider {ProviderId} returned invalid JSON during search.")]
    internal static partial void SearchReturnedInvalidJson(
        ILogger logger,
        string providerId,
        Exception exception);

    [LoggerMessage(
        EventId = 1003,
        Level = LogLevel.Warning,
        Message = "Metadata provider {ProviderId} timed out during search.")]
    internal static partial void SearchTimedOut(
        ILogger logger,
        string providerId,
        Exception exception);

    [LoggerMessage(
        EventId = 1004,
        Level = LogLevel.Warning,
        Message = "Metadata provider {ProviderId} could not return candidate details.")]
    internal static partial void CandidateDetailsUnavailable(
        ILogger logger,
        string providerId,
        Exception exception);
}

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
}
