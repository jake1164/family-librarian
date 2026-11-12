using FamilyLibrarian.Domain.Acquisition;

namespace FamilyLibrarian.Application.Acquisition;

/// <summary>What the activity list needs to know about a provider download to tell whether a "submitted" row is still running.</summary>
public sealed record AttemptJobFacts(Guid JobId, ProviderAcquisitionJobLifecycleState State, Guid? PartSetId);

/// <summary>
/// Works out, when the activity list is shown, what each ledger row means <em>now</em>. The ledger is append-only, so a
/// "submitted" row is never updated when its download ends and a "trying next copy" row is never updated when the next
/// copy has been tried; read alone they look current for ever (lab: a failed ebook download and an audiobook that had
/// reached the library both showed "In progress"). The stored outcome is never changed: this only chooses the word to show.
/// </summary>
/// <remarks>
/// <see cref="Finished"/> and <see cref="MovedOn"/> are display values only. They are not
/// <see cref="ProviderAttemptOutcome"/> members, are never stored and never count as issues.
/// </remarks>
public static class ProviderAttemptDisplay
{
    /// <summary>The download this row started has ended; the rows after it say how.</summary>
    public const string Finished = "Finished";

    /// <summary>The next copy was tried after this step; the rows after it say what happened.</summary>
    public const string MovedOn = "MovedOn";

    /// <summary>
    /// The display outcome for every attempt in <paramref name="attempts"/>, keyed by attempt id. Pass every attempt that
    /// could be later than another one shown: the newest N rows overall are enough, because any row newer than a listed
    /// row is also in that list.
    /// </summary>
    public static IReadOnlyDictionary<Guid, string> Derive(
        IReadOnlyCollection<ProviderAttempt> attempts, IReadOnlyCollection<AttemptJobFacts> jobs)
    {
        ArgumentNullException.ThrowIfNull(attempts);
        ArgumentNullException.ThrowIfNull(jobs);

        var jobsById = jobs.ToDictionary(job => job.JobId);
        var result = new Dictionary<Guid, string>(attempts.Count);
        foreach (var attempt in attempts)
        {
            result[attempt.Id] = attempt.Outcome switch
            {
                ProviderAttemptOutcome.Submitted => SubmittedState(attempt, attempts, jobsById, jobs),
                ProviderAttemptOutcome.Retrying => attempts.Any(other =>
                        other.RequestFormatId == attempt.RequestFormatId && other.AttemptedAtUtc > attempt.AttemptedAtUtc)
                    ? MovedOn
                    : nameof(ProviderAttemptOutcome.Retrying),
                _ => attempt.Outcome.ToString()
            };
        }

        return result;
    }

    private static string SubmittedState(
        ProviderAttempt attempt, IReadOnlyCollection<ProviderAttempt> attempts,
        Dictionary<Guid, AttemptJobFacts> jobsById, IReadOnlyCollection<AttemptJobFacts> jobs)
    {
        const string stillRunning = nameof(ProviderAttemptOutcome.Submitted);
        if (attempt.ProviderAcquisitionJobId is { } jobId && jobsById.TryGetValue(jobId, out var job))
        {
            // A part set is one download: it is running while any member is.
            var members = job.PartSetId is { } setId ? jobs.Where(other => other.PartSetId == setId) : [job];
            return members.Any(member => IsActive(member.State)) ? stillRunning : Finished;
        }

        // Rows from before the link existed (or whose job is gone): finished once the same source has a later row for
        // the format. A different source's row says nothing about this download, so it does not count.
        return attempts.Any(other =>
                other.RequestFormatId == attempt.RequestFormatId &&
                string.Equals(other.ProviderId, attempt.ProviderId, StringComparison.OrdinalIgnoreCase) &&
                other.AttemptedAtUtc > attempt.AttemptedAtUtc)
            ? Finished
            : stillRunning;
    }

    private static bool IsActive(ProviderAcquisitionJobLifecycleState state) => state is
        ProviderAcquisitionJobLifecycleState.Queued or ProviderAcquisitionJobLifecycleState.Running or
        ProviderAcquisitionJobLifecycleState.Waiting;
}
