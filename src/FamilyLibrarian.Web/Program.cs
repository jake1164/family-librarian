using System.Security.Claims;
using System.Text.Json;
using System.ComponentModel.DataAnnotations;
using FamilyLibrarian.Contracts.Authentication;
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

await app.Services.InitializeIdentityAsync(app.Configuration);

app.UseExceptionHandler("/error");
app.UseHttpsRedirection();
app.UseBlazorFrameworkFiles();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health/live", () => Results.Ok(new { status = "live" }))
    .AllowAnonymous();
app.MapHealthChecks("/health/ready");

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
