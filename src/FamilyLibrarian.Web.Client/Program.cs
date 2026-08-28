using FamilyLibrarian.Web.Client;
using FamilyLibrarian.Web.Client.Accounts;
using FamilyLibrarian.Web.Client.Acquisition;
using FamilyLibrarian.Web.Client.Authentication;
using FamilyLibrarian.Web.Client.Catalog;
using FamilyLibrarian.Web.Client.Feedback;
using FamilyLibrarian.Web.Client.Integrations;
using FamilyLibrarian.Web.Client.Notifications;
using FamilyLibrarian.Web.Client.Operations;
using FamilyLibrarian.Web.Client.Policy;
using FamilyLibrarian.Web.Client.Providers;
using FamilyLibrarian.Web.Client.Publishing;
using FamilyLibrarian.Web.Client.Requests;
using FamilyLibrarian.Web.Client.SettingsBackups;
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
builder.Services.AddScoped<AdminTasksApiClient>();
builder.Services.AddScoped<NotificationsApiClient>();
builder.Services.AddScoped<FeedbackApiClient>();
builder.Services.AddScoped<MediaAssetsApiClient>();
builder.Services.AddScoped<CwaSettingsApiClient>();
builder.Services.AddScoped<AudiobookshelfSettingsApiClient>();
builder.Services.AddScoped<LibraryPublishingApiClient>();
builder.Services.AddScoped<PolicyApiClient>();
builder.Services.AddScoped<OidcSettingsApiClient>();
builder.Services.AddScoped<ExternalProviderApiClient>();
builder.Services.AddScoped<SettingsBackupApiClient>();

await builder.Build().RunAsync();
