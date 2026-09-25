using FamilyLibrarian.Contracts.Realtime;
using FamilyLibrarian.Domain.Communications;
using FamilyLibrarian.Infrastructure.Persistence;
using FamilyLibrarian.Web.Realtime;
using Microsoft.EntityFrameworkCore;

namespace FamilyLibrarian.Web.Tests;

[TestClass]
public sealed class LiveChangesCaptureTests
{
    [TestMethod]
    public void MatrixDestinationChangesCreateAnOwnerScopedCommunicationsInvalidation()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=unused;Username=unused;Password=unused")
            .Options;
        using var database = new AppDbContext(options);
        var ownerId = Guid.NewGuid();
        var destination = new UserMatrixDestination(ownerId, DateTimeOffset.UtcNow);
        destination.RequestVerification("@reader:example.test", "!room:example.test", "123456", DateTimeOffset.UtcNow);
        database.UserMatrixDestinations.Add(destination);

        var added = LiveChanges.Capture(database);
        Assert.AreEqual(LiveUpdateTopics.Communications, added.UserTopics[ownerId]);
        Assert.AreEqual(LiveUpdateTopics.None, added.AdminTopics);
        Assert.AreEqual(LiveUpdateTopics.None, added.SharedTopics);

        database.ChangeTracker.AcceptAllChanges();
        destination.Verify(DateTimeOffset.UtcNow);
        var verified = LiveChanges.Capture(database);
        Assert.AreEqual(LiveUpdateTopics.Communications, verified.UserTopics[ownerId]);
        Assert.AreEqual(LiveUpdateTopics.None, verified.AdminTopics);
        Assert.AreEqual(LiveUpdateTopics.None, verified.SharedTopics);
    }
}
