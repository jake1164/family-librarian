namespace FamilyLibrarian.Web.Client.Settings;

/// <summary>Combines page and nested settings forms into one navigation decision.</summary>
public sealed class UnsavedChangesState
{
    private readonly HashSet<object> dirtyForms = [];
    public bool IsDirty => dirtyForms.Count != 0;
    public event Action? Changed;

    public void Set(object owner, bool dirty)
    {
        var before = IsDirty;
        if (dirty) dirtyForms.Add(owner);
        else dirtyForms.Remove(owner);
        if (before != IsDirty) Changed?.Invoke();
    }
}
