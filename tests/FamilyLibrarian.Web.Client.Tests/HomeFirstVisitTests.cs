using System.Net;
using System.Text;
using System.Text.Json;
using Bunit;
using Bunit.TestDoubles;
using FamilyLibrarian.Contracts.Setup;
using FamilyLibrarian.Web.Client.Authentication;
using FamilyLibrarian.Web.Client.Pages;
using FamilyLibrarian.Web.Client.Setup;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace FamilyLibrarian.Web.Client.Tests;

/// <summary>The home page may open the setup wizard, but never over somewhere the person is already going.</summary>
[TestClass]
public sealed class HomeFirstVisitTests
{
    [TestMethod]
    public async Task ANewMemberWhoStaysOnTheHomePageIsTakenToTheWizard()
    {
        await using var context = NewContext(out var server);
        var navigation = context.Services.GetRequiredService<NavigationManager>();

        var home = context.Render<Home>();
        server.AnswerSeen();

        home.WaitForAssertion(() => StringAssert.EndsWith(navigation.Uri, "/welcome"));
    }

    [TestMethod]
    public async Task AClickThatHasNotBeenAppliedYetIsNotOverriddenByTheWizard()
    {
        await using var context = NewContext(out var server);
        var navigation = context.Services.GetRequiredService<NavigationManager>();
        var home = context.Render<Home>();
        home.WaitForAssertion(() => Assert.IsTrue(server.SeenRequested));

        // As with the app's unsaved-changes guard: a click is held for a moment, and until it is applied the
        // address still says the home page.
        var guard = new TaskCompletionSource();
        using var registration = navigation.RegisterLocationChangingHandler(async change =>
        {
            if (change.TargetLocation.EndsWith("requests", StringComparison.Ordinal))
            {
                await guard.Task;
            }
        });
        await home.InvokeAsync(() => navigation.NavigateTo("requests"));
        Assert.AreEqual("http://localhost/", navigation.Uri);

        server.AnswerSeen();
        await server.SeenAnswered;
        await home.InvokeAsync(() => Task.CompletedTask);
        guard.SetResult();

        await SettleAsync(navigation);
        StringAssert.EndsWith(navigation.Uri, "/requests");
        Assert.IsFalse(((BunitNavigationManager)navigation).History.Any(entry => entry.Uri.Contains("welcome")),
            "The wizard must not even be attempted over a click in progress.");
    }

    // Waits on the address itself: a component helper only looks again after a render, and navigating causes none.
    private static async Task SettleAsync(NavigationManager navigation)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (navigation.Uri == "http://localhost/" && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        // Long enough for a late redirect to show itself.
        await Task.Delay(200);
    }

    private static BunitContext NewContext(out FakeServer server)
    {
        var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddMudServices();
        context.AddAuthorization().SetAuthorized("reader");
        var fake = new FakeServer();
        var client = new HttpClient(fake) { BaseAddress = new Uri("http://localhost/") };
        context.Services.AddSingleton(new SetupApiClient(client, new AntiforgeryTokenProvider(client)));
        server = fake;
        return context;
    }

    private sealed class FakeServer : HttpMessageHandler
    {
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource answered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool SeenRequested { get; private set; }

        public Task SeenAnswered => answered.Task;

        public void AnswerSeen() => release.TrySetResult();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/antiforgery/token", StringComparison.Ordinal))
            {
                return Json(new { token = "test-token", headerName = "X-CSRF-TOKEN" });
            }

            if (path.EndsWith("/me/setup/seen", StringComparison.Ordinal))
            {
                SeenRequested = true;
                await release.Task;
                answered.TrySetResult();
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            return Json(new UserSetupResponse(
                false, true, [new UserSetupStepResponse("Formats", true, false, false, false)], [], null, null));
        }

        private static HttpResponseMessage Json(object body) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(body, JsonSerializerOptions.Web), Encoding.UTF8, "application/json")
        };
    }
}
