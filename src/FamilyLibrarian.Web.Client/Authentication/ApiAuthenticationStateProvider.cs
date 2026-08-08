using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using FamilyLibrarian.Contracts.Authentication;
using Microsoft.AspNetCore.Components.Authorization;

namespace FamilyLibrarian.Web.Client.Authentication;

public sealed class ApiAuthenticationStateProvider(HttpClient httpClient) : AuthenticationStateProvider
{
    private static readonly ClaimsPrincipal Anonymous = new(new ClaimsIdentity());

    public override async Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        try
        {
            var user = await httpClient.GetFromJsonAsync<CurrentUserResponse>("api/v1/me");
            return new AuthenticationState(user is null ? Anonymous : CreatePrincipal(user));
        }
        catch (HttpRequestException exception) when (exception.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return new AuthenticationState(Anonymous);
        }
    }

    public async Task<bool> SignInAsync(LoginRequest request)
    {
        using var response = await httpClient.PostAsJsonAsync("api/auth/login", request);
        if (!response.IsSuccessStatusCode)
        {
            return false;
        }

        NotifyAuthenticationStateChanged(GetAuthenticationStateAsync());
        return true;
    }

    public async Task SignOutAsync()
    {
        using var response = await httpClient.PostAsJsonAsync("api/auth/logout", new { });
        response.EnsureSuccessStatusCode();
        NotifyAuthenticationStateChanged(Task.FromResult(new AuthenticationState(Anonymous)));
    }

    private static ClaimsPrincipal CreatePrincipal(CurrentUserResponse user)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.DisplayName)
        };

        if (!string.IsNullOrWhiteSpace(user.Email))
        {
            claims.Add(new Claim(ClaimTypes.Email, user.Email));
        }

        claims.AddRange(user.Roles.Select(role => new Claim(ClaimTypes.Role, role)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "FamilyLibrarianCookie"));
    }
}
