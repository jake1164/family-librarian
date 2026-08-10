using System.Security.Claims;
using System.Text.Json;
using System.ComponentModel.DataAnnotations;
using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Contracts.Authentication;
using FamilyLibrarian.Contracts.Catalog;
using FamilyLibrarian.Domain;
using FamilyLibrarian.Infrastructure;
using FamilyLibrarian.Infrastructure.Identity;
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

app.MapGet("/health/live", () => Results.Ok(new { status = "live" }))
    .AllowAnonymous();
app.MapHealthChecks("/health/ready");

app.MapGet("/api/v1/catalog/search", SearchCatalogAsync)
    .AllowAnonymous();
app.MapGet("/api/v1/catalog/candidates/{providerId}/{externalId}", GetCatalogCandidateAsync)
    .AllowAnonymous();

app.MapPost("/api/auth/login", LoginAsync)
    .AllowAnonymous();
app.MapPost("/api/auth/logout", LogoutAsync)
    .RequireAuthorization();

app.MapGet("/api/v1/me", GetCurrentUserAsync)
    .RequireAuthorization();
app.MapGet("/api/v1/admin/ping", () => Results.Ok(new { status = "ok" }))
    .RequireAuthorization("Admin");

app.MapFallbackToFile("index.html");

await app.RunAsync();

static async Task<IResult> LoginAsync(
    LoginRequest request,
    UserManager<AppUser> userManager,
    SignInManager<AppUser> signInManager)
{
    if (!new EmailAddressAttribute().IsValid(request.Email) || request.Password.Length < 12)
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
        return Results.NoContent();
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

static async Task<IResult> SearchCatalogAsync(
    string? q,
    IEnumerable<IBookMetadataProvider> providers,
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
    var searches = providers.Select(provider =>
        SearchProviderAsync(provider, query, logger, cancellationToken));
    var providerResults = await Task.WhenAll(searches);

    return Results.Ok(new CatalogSearchResponse(
        providerResults
            .Where(result => result.Succeeded)
            .SelectMany(result => result.Candidates)
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
    IEnumerable<IBookMetadataProvider> providers,
    ILoggerFactory loggerFactory,
    CancellationToken cancellationToken)
{
    var provider = providers.SingleOrDefault(candidate =>
        string.Equals(candidate.Id, providerId, StringComparison.OrdinalIgnoreCase));
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
