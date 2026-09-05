using FamilyLibrarian.Contracts.Realtime;
using Microsoft.AspNetCore.Components;

namespace FamilyLibrarian.Web.Client.Realtime;

public abstract class LiveComponentBase : ComponentBase, IDisposable
{
    [Inject] protected LiveUpdatesService LiveUpdates { get; set; } = null!;
    private LiveSubscription? subscription;
    private bool disposed;

    protected void Observe(LiveUpdateTopics topics, Func<Task> refresh)
    {
        subscription?.Dispose();
        subscription = LiveUpdates.Subscribe(topics, token => InvokeAsync(async () =>
        {
            if (disposed || token.IsCancellationRequested) return;
            await refresh();
            if (!disposed && !token.IsCancellationRequested) StateHasChanged();
        }));
    }

    protected Task RefreshLiveAsync() => subscription?.RefreshAsync() ?? Task.CompletedTask;

    public virtual void Dispose()
    {
        disposed = true;
        subscription?.Dispose();
        GC.SuppressFinalize(this);
    }
}
