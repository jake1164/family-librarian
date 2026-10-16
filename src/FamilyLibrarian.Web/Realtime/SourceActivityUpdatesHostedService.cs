using System.Threading.Channels;
using FamilyLibrarian.Application.Acquisition;
using FamilyLibrarian.Contracts.Realtime;

namespace FamilyLibrarian.Web.Realtime;

/// <summary>Coalesces transient source activity into the existing administrator-only invalidations.</summary>
internal sealed class SourceActivityUpdatesHostedService(
    SourceSearchTracker searches, ActiveAcquisitionTracker acquisitions, LiveUpdatesPublisher publisher) : BackgroundService
{
    private readonly Channel<bool> changes = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropWrite,
        SingleReader = true
    });

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        searches.Changed += OnChanged;
        acquisitions.Changed += OnChanged;
        try
        {
            await foreach (var _ in changes.Reader.ReadAllAsync(stoppingToken))
                await publisher.PublishAsync(new LiveChanges { AdminTopics = LiveUpdateTopics.Sources });
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            searches.Changed -= OnChanged;
            acquisitions.Changed -= OnChanged;
        }
    }

    private void OnChanged() => changes.Writer.TryWrite(true);
}
