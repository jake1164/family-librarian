using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Application.Feedback;
using FamilyLibrarian.Application.Requests;
using FamilyLibrarian.Domain.Following;
using FamilyLibrarian.Domain.Requests;

namespace FamilyLibrarian.Application.Following;

/// <summary>
/// The commands and queries behind Following: subscribing to a Series or
/// Author, and the per-user read/owned view of what's followed.
/// </summary>
/// <remarks>
/// Ownership is enforced here, not at the endpoint, the same as
/// <see cref="UserWorkFeedbackService"/>: every method resolves the caller
/// from <see cref="ICurrentUser"/>, so nothing here can act on or reveal
/// another family member's follows.
/// </remarks>
public sealed class FollowService(
    IFollowRepository repository,
    ICatalogRepository catalog,
    IUserWorkFeedbackRepository feedback,
    IRequestRepository requests,
    ICurrentUser currentUser,
    IClock clock)
{
    public async Task<FollowResult> FollowSeriesAsync(Guid seriesId, CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId)
        {
            return FollowResult.Unauthenticated();
        }

        return await catalog.GetSeriesAsync(seriesId, cancellationToken) is null
            ? FollowResult.NotFound()
            : await FollowAsync(userId, FollowSubjectType.Series, seriesId, cancellationToken);
    }

    public async Task<FollowResult> FollowAuthorAsync(Guid authorId, CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId)
        {
            return FollowResult.Unauthenticated();
        }

        return await catalog.GetAuthorAsync(authorId, cancellationToken) is null
            ? FollowResult.NotFound()
            : await FollowAsync(userId, FollowSubjectType.Author, authorId, cancellationToken);
    }

    private async Task<FollowResult> FollowAsync(
        Guid userId,
        FollowSubjectType subjectType,
        Guid subjectId,
        CancellationToken cancellationToken)
    {
        var existing = await repository.FindAsync(userId, subjectType, subjectId, cancellationToken);
        if (existing is not null)
        {
            return FollowResult.Success(existing.Id);
        }

        var follow = new Follow(userId, subjectType, subjectId, clock.UtcNow);
        repository.Add(follow);
        await repository.SaveChangesAsync(cancellationToken);
        return FollowResult.Success(follow.Id);
    }

    public async Task<bool> UnfollowAsync(
        FollowSubjectType subjectType,
        Guid subjectId,
        CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId)
        {
            return false;
        }

        var existing = await repository.FindAsync(userId, subjectType, subjectId, cancellationToken);
        if (existing is null)
        {
            return false;
        }

        repository.Remove(existing);
        await repository.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<IReadOnlyList<FollowedSeriesView>> ListMyFollowedSeriesAsync(
        CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId)
        {
            return [];
        }

        var follows = (await repository.ListForUserAsync(userId, cancellationToken))
            .Where(follow => follow.SubjectType == FollowSubjectType.Series)
            .ToArray();
        if (follows.Length == 0)
        {
            return [];
        }

        var (readWorkIds, ownedWorkIds) = await GetReadAndOwnedWorkIdsAsync(userId, cancellationToken);

        var views = new List<FollowedSeriesView>(follows.Length);
        foreach (var follow in follows)
        {
            // A followed series that was later retired/merged away leaves an
            // orphaned follow row rather than a broken page — skip it.
            if (await catalog.GetSeriesAsync(follow.SubjectId, cancellationToken) is not { } series)
            {
                continue;
            }

            var entries = series.Entries
                .OrderByDescending(entry => entry.IsPrimary)
                .ThenBy(entry => entry.PositionSort)
                .ThenBy(entry => entry.PositionLabel, StringComparer.Ordinal)
                .Select(entry => new FollowedSeriesEntryView(
                    entry.WorkId,
                    entry.Work.CanonicalTitle,
                    entry.PositionLabel,
                    entry.IsPrimary,
                    readWorkIds.Contains(entry.WorkId),
                    ownedWorkIds.Contains(entry.WorkId)))
                .ToArray();

            views.Add(new FollowedSeriesView(
                follow.Id,
                series.Id,
                series.Name,
                series.Status,
                entries,
                entries.Where(entry => entry.IsPrimary).All(entry => entry.IsRead)));
        }

        return views;
    }

    public async Task<IReadOnlyList<FollowedAuthorView>> ListMyFollowedAuthorsAsync(
        CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId)
        {
            return [];
        }

        var follows = (await repository.ListForUserAsync(userId, cancellationToken))
            .Where(follow => follow.SubjectType == FollowSubjectType.Author)
            .ToArray();
        if (follows.Length == 0)
        {
            return [];
        }

        var (readWorkIds, ownedWorkIds) = await GetReadAndOwnedWorkIdsAsync(userId, cancellationToken);

        var views = new List<FollowedAuthorView>(follows.Length);
        foreach (var follow in follows)
        {
            if (await catalog.GetAuthorAsync(follow.SubjectId, cancellationToken) is not { } author)
            {
                continue;
            }

            var works = author.WorkAuthors
                .Select(workAuthor => workAuthor.Work)
                .DistinctBy(work => work.Id)
                .OrderBy(work => work.FirstPublicationDate)
                .ThenBy(work => work.CanonicalTitle, StringComparer.Ordinal)
                .Select(work => new FollowedAuthorWorkView(
                    work.Id,
                    work.CanonicalTitle,
                    readWorkIds.Contains(work.Id),
                    ownedWorkIds.Contains(work.Id)))
                .ToArray();

            views.Add(new FollowedAuthorView(follow.Id, author.Id, author.CanonicalName, works));
        }

        return views;
    }

    private async Task<(HashSet<Guid> Read, HashSet<Guid> Owned)> GetReadAndOwnedWorkIdsAsync(
        Guid userId,
        CancellationToken cancellationToken)
    {
        var readWorkIds = (await feedback.ListForUserAsync(userId, cancellationToken))
            .Select(view => view.WorkId)
            .ToHashSet();
        var ownedWorkIds = (await requests.ListForUserAsync(userId, cancellationToken))
            .Where(request => request.Status == RequestStatus.Available)
            .Select(request => request.WorkId)
            .ToHashSet();
        return (readWorkIds, ownedWorkIds);
    }
}

public sealed record FollowResult(FollowOutcome Outcome, Guid? FollowId)
{
    public static FollowResult Success(Guid followId) => new(FollowOutcome.Success, followId);

    public static FollowResult NotFound() => new(FollowOutcome.NotFound, null);

    public static FollowResult Unauthenticated() => new(FollowOutcome.Unauthenticated, null);
}

public enum FollowOutcome
{
    Success,
    NotFound,
    Unauthenticated
}
