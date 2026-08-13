using FamilyLibrarian.Web.Client;
using FamilyLibrarian.Web.Client.Accounts;
using FamilyLibrarian.Web.Client.Authentication;
using FamilyLibrarian.Web.Client.Catalog;
using FamilyLibrarian.Web.Client.Integrations;
using FamilyLibrarian.Web.Client.Requests;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using MudBlazor.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");

builder.Services.AddMudServices();
builder.Services.AddAuthorizationCore();
builder.Services.AddScoped<ApiAuthenticationStateProvider>();
builder.Services.AddScoped<AuthenticationStateProvider>(
    provider => provider.GetRequiredService<ApiAuthenticationStateProvider>());
builder.Services.AddScoped(sp => new HttpClient
{
    BaseAddress = new Uri(builder.HostEnvironment.BaseAddress)
});
builder.Services.AddScoped<AntiforgeryTokenProvider>();
builder.Services.AddScoped<AccountsApiClient>();
builder.Services.AddScoped<CatalogApiClient>();
builder.Services.AddScoped<CatalogSearchState>();
builder.Services.AddScoped<MetadataIntegrationsApiClient>();
builder.Services.AddScoped<RequestsApiClient>();
builder.Services.AddScoped<AdminRequestsApiClient>();

await builder.Build().RunAsync();
