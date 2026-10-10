using FamilyLibrarian.Application.Abstractions;

namespace FamilyLibrarian.Application.Acquisition;

/// <summary>
/// Starts one member of an automatic audiobook set. Implemented by
/// <see cref="DirectAcquisitionService"/>; a seam so the orchestration below
/// can be tested without a provider.
/// </summary>
public interface IAudiobookPartSetMemberAcquirer
{
    Task<ManualImportResult> AcquireMemberAsync(
        Guid requestId,
        Guid requestFormatId,
        string providerId,
        string providerResultId,
        AudiobookPartSetMember member,
        CancellationToken cancellationToken);
}

public sealed record AudiobookPartSetStartResult(bool Started, Guid? SetId, string? Error)
{
    public static AudiobookPartSetStartResult Success(Guid setId) => new(true, setId, null);

    public static AudiobookPartSetStartResult Failure(string error) => new(false, null, error);
}

/// <summary>
/// Submits every part of a selected complete audiobook set as its own durable
/// provider job, all-or-nothing: if any part cannot be started, the parts that
/// already were are cancelled, so a set is never left half-acquired.
/// </summary>
public sealed class AudiobookPartSetAcquisitionService(
    IAudiobookPartSetMemberAcquirer members,
    IProviderAcquisitionJobStore jobs,
    IClock clock)
{
    public async Task<AudiobookPartSetStartResult> StartAsync(
        Guid requestId,
        Guid requestFormatId,
        string providerId,
        AudiobookPartSetSelection selection,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(selection);

        var setId = Guid.NewGuid();
        try
        {
            foreach (var part in selection.Parts)
            {
                var member = new AudiobookPartSetMember(
                    setId, part.AudiobookPart!.Number, selection.Total, selection.MemberResultIds);
                var result = await members.AcquireMemberAsync(
                    requestId, requestFormatId, providerId, part.ProviderResultId, member, cancellationToken);

                if (result.Outcome != ManualImportOutcome.AcquisitionInProgress)
                {
                    var reason = $"Part {member.Number} of {member.Total} could not be started: " +
                        (result.Error ?? "the provider did not accept it.");
                    await AbandonAsync(setId, reason);
                    return AudiobookPartSetStartResult.Failure(reason);
                }
            }
        }
        catch
        {
            // Whatever went wrong, parts already submitted must not keep
            // downloading on their own. Not tied to the caller's token: the
            // usual cause of reaching here is that it was cancelled.
            await AbandonAsync(setId, "The set could not be fully started.");
            throw;
        }

        return AudiobookPartSetStartResult.Success(setId);
    }

    private async Task AbandonAsync(Guid setId, string reason)
    {
        var members = await jobs.ListByPartSetAsync(setId, CancellationToken.None);
        foreach (var job in members)
        {
            job.Cancel(reason, clock.UtcNow);
        }

        await jobs.SaveChangesAsync(CancellationToken.None);
    }
}
