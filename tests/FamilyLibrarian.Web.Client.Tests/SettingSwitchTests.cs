using Bunit;
using FamilyLibrarian.Web.Client.Settings;
using Microsoft.AspNetCore.Components;
using MudBlazor;
using MudBlazor.Services;

namespace FamilyLibrarian.Web.Client.Tests;

/// <summary>A switch that saves on click must end up showing what was saved, whatever happened to the click.</summary>
[TestClass]
public sealed class SettingSwitchTests
{
    private static BunitContext NewContext()
    {
        var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddMudServices();
        return context;
    }

    private static bool IsChecked(IRenderedComponent<SettingSwitch> cut) => cut.Find("input").HasAttribute("checked");

    private static Task ClickAsync(IRenderedComponent<SettingSwitch> cut, bool wanted) =>
        cut.Find("input").ChangeAsync(new ChangeEventArgs { Value = wanted });

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ARefusedChangeShowsTheSavedStateAgainAndKeepsTheReason(bool refusedAfterAWait)
    {
        await using var context = NewContext();
        var cut = context.Render<SettingSwitch>(parameters => parameters
            .Add(component => component.Value, false)
            .Add(component => component.Changing, async (SettingSwitchChange change) =>
            {
                // Both timings matter: a refusal that returns at once must not leave the click on screen either.
                if (refusedAfterAWait) await Task.Delay(20);
                change.Refuse("Set the server address first.");
            }));

        await ClickAsync(cut, true);

        cut.WaitForAssertion(() =>
        {
            Assert.IsFalse(IsChecked(cut), "A refused switch must not look on.");
            StringAssert.Contains(cut.Find("[role=alert]").TextContent, "Set the server address first.");
        });
        cut.Render();
        Assert.IsFalse(IsChecked(cut));
        StringAssert.Contains(cut.Markup, "Set the server address first.");
    }

    [TestMethod]
    public async Task AnUnreachableHostLeavesTheSwitchAsItWas()
    {
        await using var context = NewContext();
        var cut = context.Render<SettingSwitch>(parameters => parameters
            .Add(component => component.Value, true)
            .Add(component => component.Changing, (SettingSwitchChange _) => throw new HttpRequestException("offline")));

        await ClickAsync(cut, false);

        cut.WaitForAssertion(() =>
        {
            Assert.IsTrue(IsChecked(cut));
            StringAssert.Contains(cut.Markup, "nothing changed");
        });
    }

    [TestMethod]
    public async Task ASavedChangeStaysAndCanBeClickedBackAgain()
    {
        await using var context = NewContext();
        var requested = new List<bool>();
        var cut = context.Render<SwitchHost>(parameters => parameters
            .Add(host => host.OnRequest, (bool wanted) => requested.Add(wanted)));
        var inner = cut.FindComponent<SettingSwitch>();

        await ClickAsync(inner, true);
        cut.WaitForAssertion(() => Assert.IsTrue(IsChecked(inner)));
        await ClickAsync(inner, false);
        cut.WaitForAssertion(() => Assert.IsFalse(IsChecked(inner)));

        Assert.AreEqual("True,False", string.Join(',', requested));
        Assert.AreEqual(0, cut.FindAll("[role=alert]").Count);
    }

    [TestMethod]
    [DataRow("Cancel", false)]
    [DataRow("Turn on", true)]
    public async Task ARiskySwitchSavesOnlyAfterTheConfirmationIsAccepted(string answer, bool expectSaved)
    {
        await using var context = NewContext();
        var dialogs = context.Render<MudDialogProvider>();
        var saves = 0;
        var cut = context.Render<SettingSwitch>(parameters => parameters
            .Add(component => component.Value, false)
            .Add(component => component.ConfirmOn, "Book data can leave this server.")
            .Add(component => component.Changing, (SettingSwitchChange _) => saves++));

        var click = ClickAsync(cut, true);
        dialogs.WaitForAssertion(() => StringAssert.Contains(dialogs.Markup, "Book data can leave this server."));
        Assert.AreEqual(0, saves, "Nothing is saved while the question is open.");
        await dialogs.FindAll("button").Single(button => button.TextContent.Trim() == answer).ClickAsync(new());
        await click;

        cut.WaitForAssertion(() => Assert.AreEqual(expectSaved ? 1 : 0, saves));
        // The value was never updated by this stub, so the switch returns to it either way.
        cut.WaitForAssertion(() => Assert.IsFalse(IsChecked(cut)));
    }

    [TestMethod]
    public async Task TurningARiskySwitchOffNeverAsks()
    {
        await using var context = NewContext();
        var dialogs = context.Render<MudDialogProvider>();
        var saved = new List<bool>();
        var cut = context.Render<SettingSwitch>(parameters => parameters
            .Add(component => component.Value, true)
            .Add(component => component.ConfirmOn, "Book data can leave this server.")
            .Add(component => component.Changing, (SettingSwitchChange change) => saved.Add(change.Value)));

        await ClickAsync(cut, false);

        cut.WaitForAssertion(() => Assert.AreEqual("False", string.Join(',', saved)));
        Assert.IsFalse(dialogs.Markup.Contains("Book data can leave this server.", StringComparison.Ordinal));
    }

    /// <summary>A page in miniature: it owns the saved value and updates it when a change succeeds.</summary>
    private sealed class SwitchHost : ComponentBase
    {
        private bool saved;

        [Parameter] public Action<bool>? OnRequest { get; set; }

        protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder builder)
        {
            builder.OpenComponent<SettingSwitch>(0);
            builder.AddAttribute(1, nameof(SettingSwitch.Value), saved);
            builder.AddAttribute(2, nameof(SettingSwitch.Changing),
                EventCallback.Factory.Create<SettingSwitchChange>(this, async change =>
                {
                    await Task.Yield();
                    OnRequest?.Invoke(change.Value);
                    saved = change.Value;
                }));
            builder.CloseComponent();
        }
    }
}
