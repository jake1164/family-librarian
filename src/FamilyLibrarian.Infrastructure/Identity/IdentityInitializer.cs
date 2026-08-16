using FamilyLibrarian.Application.Accounts;
using FamilyLibrarian.Domain;
using FamilyLibrarian.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FamilyLibrarian.Infrastructure.Identity;

public static class IdentityInitializer
{
    /// <summary>
    /// Loads whatever OIDC settings are already saved into the in-memory cache
    /// the "oidc" scheme's options read from, so the very first request after a
    /// restart sees the last-configured state rather than the disabled default.
    /// </summary>
    public static async Task InitializeOidcRuntimeCacheAsync(
        this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var settingsService = scope.ServiceProvider.GetRequiredService<OidcSettingsService>();
        var cache = scope.ServiceProvider.GetRequiredService<IOidcRuntimeSettingsCache>();

        cache.Refresh(await settingsService.LoadRuntimeSettingsAsync(cancellationToken));
    }

    public static async Task MigrateDatabaseAsync(
        this IServiceProvider services,
        CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        await database.Database.MigrateAsync(cancellationToken);
    }

    public static async Task InitializeIdentityAsync(
        this IServiceProvider services,
        IConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();

        foreach (var roleName in new[] { RoleNames.User, RoleNames.Admin })
        {
            if (!await roleManager.RoleExistsAsync(roleName))
            {
                var result = await roleManager.CreateAsync(new IdentityRole<Guid>(roleName));
                EnsureSucceeded(result, $"create the '{roleName}' role");
            }
        }

        var bootstrapEmail = configuration["Admin_Email"];
        var bootstrapPassword = configuration["Admin_Password"];

        if (string.IsNullOrWhiteSpace(bootstrapEmail) || string.IsNullOrWhiteSpace(bootstrapPassword))
        {
            return;
        }

        var administrators = await userManager.GetUsersInRoleAsync(RoleNames.Admin);
        if (administrators.Count > 0)
        {
            return;
        }

        var user = await userManager.FindByEmailAsync(bootstrapEmail);
        if (user is null)
        {
            user = new AppUser
            {
                UserName = bootstrapEmail,
                Email = bootstrapEmail,
                EmailConfirmed = true,
                DisplayName = bootstrapEmail.Split('@', 2)[0],
                CreatedAtUtc = DateTimeOffset.UtcNow,
                IsBreakGlass = true
            };

            var createResult = await userManager.CreateAsync(user, bootstrapPassword);
            EnsureSucceeded(createResult, "create the bootstrap administrator");
        }

        var addRoleResult = await userManager.AddToRoleAsync(user, RoleNames.Admin);
        if (!addRoleResult.Succeeded && !await userManager.IsInRoleAsync(user, RoleNames.Admin))
        {
            EnsureSucceeded(addRoleResult, "assign the bootstrap administrator role");
        }

        if (!await userManager.IsInRoleAsync(user, RoleNames.User))
        {
            var addUserRoleResult = await userManager.AddToRoleAsync(user, RoleNames.User);
            EnsureSucceeded(addUserRoleResult, "assign the bootstrap user role");
        }
    }

    private static void EnsureSucceeded(IdentityResult result, string operation)
    {
        if (!result.Succeeded)
        {
            var errors = string.Join(", ", result.Errors.Select(error => error.Description));
            throw new InvalidOperationException($"Unable to {operation}: {errors}");
        }
    }
}
