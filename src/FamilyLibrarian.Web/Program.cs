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
    var searches = providers.Select(provider => provider.SearchAsync(query, cancellationToken));
    var results = await Task.WhenAll(searches);

    return Results.Ok(new CatalogSearchResponse(
        results.SelectMany(candidates => candidates).Select(ToResponse).ToArray()));
}

static async Task<IResult> GetCatalogCandidateAsync(
    string providerId,
    string externalId,
    IEnumerable<IBookMetadataProvider> providers,
    CancellationToken cancellationToken)
{
    var provider = providers.SingleOrDefault(candidate =>
        string.Equals(candidate.Id, providerId, StringComparison.OrdinalIgnoreCase));
    if (provider is null)
    {
        return Results.NotFound();
    }

    var candidate = await provider.GetDetailsAsync(externalId, cancellationToken);
    return candidate is null ? Results.NotFound() : Results.Ok(ToResponse(candidate));
}

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
