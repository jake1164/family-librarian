using Microsoft.AspNetCore.Components;

namespace FamilyLibrarian.Web.Tests.Harness;

/// <summary>MudBlazor's notifications need a navigation service, which the static HTML renderer does not provide.</summary>
internal sealed class StaticNavigationManager : NavigationManager
{
    public StaticNavigationManager() => Initialize("http://localhost/", "http://localhost/");

    protected override void NavigateToCore(string uri, bool forceLoad)
    {
    }
}
