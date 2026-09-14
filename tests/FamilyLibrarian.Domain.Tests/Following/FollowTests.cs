using FamilyLibrarian.Domain.Following;

namespace FamilyLibrarian.Domain.Tests.Following;

[TestClass]
public sealed class FollowTests
{
    private static readonly DateTimeOffset CreatedAt = new(2026, 9, 13, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly Guid SubjectId = Guid.NewGuid();

    [TestMethod]
    public void ANewFollowRecordsTheUserSubjectTypeAndSubject()
    {
        var follow = new Follow(UserId, FollowSubjectType.Series, SubjectId, CreatedAt);

        Assert.AreEqual(UserId, follow.UserId);
        Assert.AreEqual(FollowSubjectType.Series, follow.SubjectType);
        Assert.AreEqual(SubjectId, follow.SubjectId);
        Assert.AreEqual(CreatedAt, follow.CreatedAtUtc);
        Assert.AreEqual(CreatedAt, follow.UpdatedAtUtc);
    }

    [TestMethod]
    public void AnEmptyUserIdIsRejected() =>
        Assert.ThrowsExactly<ArgumentException>(() =>
            new Follow(Guid.Empty, FollowSubjectType.Author, SubjectId, CreatedAt));

    [TestMethod]
    public void AnEmptySubjectIdIsRejected() =>
        Assert.ThrowsExactly<ArgumentException>(() =>
            new Follow(UserId, FollowSubjectType.Author, Guid.Empty, CreatedAt));
}
