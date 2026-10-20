using FamilyLibrarian.Web.Client.Settings;

namespace FamilyLibrarian.Web.Tests;

[TestClass]
public sealed class SettingsChangesTests
{
    [TestMethod]
    public void SavingOneSectionDoesNotAcceptAnotherAndRevertingRestoresCleanState()
    {
        var state = new SettingsSnapshot();
        state.Accept("smtp", new { Host = "old" });
        state.Accept("matrix", new { Server = "old" });
        Assert.IsTrue(state.HasChanges("smtp", new { Host = "new" }));
        state.Accept("matrix", new { Server = "new" });
        Assert.IsTrue(state.HasChanges("smtp", new { Host = "new" }));
        Assert.IsFalse(state.HasChanges("smtp", new { Host = "old" }));
        Assert.IsFalse(state.HasChanges("matrix", new { Server = "new" }));
    }

    [TestMethod]
    public void NestedDirtyFormsRemainProtectedUntilEveryFormIsSavedOrDisposed()
    {
        var state = new UnsavedChangesState();
        var first = new object();
        var second = new object();
        state.Set(first, true);
        state.Set(second, true);
        state.Set(first, false);
        Assert.IsTrue(state.IsDirty);
        state.Set(second, false);
        Assert.IsFalse(state.IsDirty);
    }

    [TestMethod]
    public void ClearingTextBackToEmptyDoesNotKeepAnOtherwiseUnchangedFormDirty()
    {
        var state = new SettingsSnapshot();
        state.Accept("section", new { Host = string.Empty });
        Assert.IsFalse(state.HasChanges("section", new { Host = (string?)null }));
        Assert.IsTrue(state.HasChanges("section", new { Host = " " }), "Whitespace remains a typed edit.");
    }
}
