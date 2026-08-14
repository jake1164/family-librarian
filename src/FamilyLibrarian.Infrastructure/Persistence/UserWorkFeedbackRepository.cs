using FamilyLibrarian.Application.Feedback;
using FamilyLibrarian.Domain.Feedback;
using Microsoft.EntityFrameworkCore;

namespace FamilyLibrarian.Infrastructure.Persistence;

public sealed class UserWorkFeedbackRepository(AppDbContext database) : IUserWorkFeedbackRepository
{
    public Task<bool> WorkExistsAsync(Guid workId, CancellationToken cancellationToken) =>
        database.Works.AnyAsync(work => work.Id == workId && !work.IsRetired, cancellationToken);

    public Task<UserWorkFeedback?> FindOwnedAsync(
        Guid userId,
        Guid workId,
        CancellationToken cancellationToken) =>
        database.UserWorkFeedback.SingleOrDefaultAsync(
            feedback => feedback.UserId == userId && feedback.WorkId == workId,
            cancellationToken);

    public async Task<IReadOnlyList<UserWorkFeedbackView>> ListForUserAsync(
        Guid userId,
        CancellationToken cancellationToken) =>
        await ProjectViews(database.UserWorkFeedback.Where(feedback => feedback.UserId == userId))
            .ToArrayAsync(cancellationToken);

    public Task<UserWorkFeedbackView?> FindViewAsync(
        Guid userId,
        Guid workId,
        CancellationToken cancellationToken) =>
        ProjectViews(database.UserWorkFeedback
                .Where(feedback => feedback.UserId == userId && feedback.WorkId == workId))
            .SingleOrDefaultAsync(cancellationToken);

    public void Add(UserWorkFeedback feedback) => database.UserWorkFeedback.Add(feedback);

    public void Remove(UserWorkFeedback feedback) => database.UserWorkFeedback.Remove(feedback);

    public Task SaveChangesAsync(CancellationToken cancellationToken) =>
        database.SaveChangesAsync(cancellationToken);

    /// <summary>
    /// Projects feedback with the Work facts My Reading and Work detail
    /// display, joined by key for the same reason <c>RequestRepository</c>
    /// joins Work rather than navigating to it: this record means "this book",
    /// not "this row and everything hanging off it".
    /// </summary>
    private IQueryable<UserWorkFeedbackView> ProjectViews(IQueryable<UserWorkFeedback> feedback) =>
        from item in feedback
        join work in database.Works on item.WorkId equals work.Id
        orderby item.CompletedOn descending
        select new UserWorkFeedbackView(
            item.WorkId,
            work.CanonicalTitle,
            work.Authors
                .OrderBy(author => author.Ordinal)
                .Select(author => author.Author.CanonicalName)
                .ToList(),
            work.CoverUrl,
            item.CompletedOn,
            item.Rating,
            item.Version);
}
