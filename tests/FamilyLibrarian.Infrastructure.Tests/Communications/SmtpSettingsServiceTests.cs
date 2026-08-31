using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Communications;
using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Application.Publishing;
using FamilyLibrarian.Domain.Communications;

namespace FamilyLibrarian.Infrastructure.Tests.Communications;

[TestClass]
public sealed class SmtpSettingsServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task SmtpCannotBeEnabledUntilTheSavedConfigurationPassesATest()
    {
        var context = new TestContext();
        await context.ConfigureAsync();

        var beforeTest = await context.Service.SetEnabledAsync(true, CancellationToken.None);
        Assert.IsFalse(beforeTest.Succeeded);
        StringAssert.Contains(beforeTest.Error, "successful test email");

        var test = await context.Service.SendTestAsync("admin@example.test", CancellationToken.None);
        Assert.IsTrue(test.Succeeded);
        Assert.IsTrue(test.Outcome!.Succeeded);
        Assert.AreEqual("admin@example.test", context.Sender.LastRecipientAddress);

        var enabled = await context.Service.SetEnabledAsync(true, CancellationToken.None);
        Assert.IsTrue(enabled.Succeeded);
        Assert.IsTrue(enabled.Status!.IsEnabled);
    }

    [TestMethod]
    public async Task ChangingTheSavedSettingsInvalidatesTheLastSuccessfulTest()
    {
        var context = new TestContext();
        await context.ConfigureAsync();
        await context.Service.SendTestAsync("admin@example.test", CancellationToken.None);
        await context.Service.SetEnabledAsync(true, CancellationToken.None);

        await context.Service.SetSettingsAsync(
            "smtp.other.example.test", 587, SmtpSecurityMode.StartTls, "mailer",
            "library@example.test", "Family Librarian", CancellationToken.None);

        var enabled = await context.Service.SetEnabledAsync(true, CancellationToken.None);
        Assert.IsFalse(enabled.Succeeded);
        StringAssert.Contains(enabled.Error, "successful test email");
    }

    [TestMethod]
    public async Task UsernameAndPasswordMustBeConfiguredAsAPair()
    {
        var context = new TestContext();
        await context.Service.SetSettingsAsync(
            "smtp.example.test", 587, SmtpSecurityMode.StartTls, "mailer",
            "library@example.test", null, CancellationToken.None);

        var test = await context.Service.SendTestAsync("admin@example.test", CancellationToken.None);

        Assert.IsFalse(test.Succeeded);
        StringAssert.Contains(test.Error, "username and password");
    }

    [TestMethod]
    public async Task InvalidTestRecipientDoesNotCallTheTransport()
    {
        var context = new TestContext();
        await context.ConfigureAsync();

        var test = await context.Service.SendTestAsync("not-an-email", CancellationToken.None);

        Assert.IsFalse(test.Succeeded);
        Assert.IsNull(context.Sender.LastRecipientAddress);
    }

    private sealed class TestContext
    {
        public FakeSmtpSettingsStore Store { get; } = new();
        public FakeSmtpTestSender Sender { get; } = new();
        public SmtpSettingsService Service { get; }

        public TestContext()
        {
            Service = new SmtpSettingsService(
                Store,
                new FakeCredentialProtector(),
                Sender,
                new NullAuditWriter(),
                new StubCurrentUser(),
                new FixedClock());
        }

        public async Task ConfigureAsync()
        {
            await Service.SetSettingsAsync(
                "smtp.example.test", 587, SmtpSecurityMode.StartTls, "mailer",
                "library@example.test", "Family Librarian", CancellationToken.None);
            await Service.SetPasswordAsync("super-secret", CancellationToken.None);
        }
    }

    private sealed class FakeSmtpSettingsStore : ISmtpSettingsStore
    {
        private SmtpSettings? settings;

        public Task<SmtpSettings?> FindAsync(CancellationToken cancellationToken) => Task.FromResult(settings);

        public Task<SmtpSettings> GetOrCreateAsync(CancellationToken cancellationToken) =>
            Task.FromResult(settings ??= new SmtpSettings(Now));

        public Task SaveChangesAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeSmtpTestSender : ISmtpTestSender
    {
        public string? LastRecipientAddress { get; private set; }

        public Task<ConnectionTestOutcome> SendTestAsync(
            SmtpSettings settings, string recipientAddress, CancellationToken cancellationToken)
        {
            LastRecipientAddress = recipientAddress;
            return Task.FromResult(new ConnectionTestOutcome(true, "Accepted."));
        }
    }

    private sealed class FakeCredentialProtector : ICredentialProtector
    {
        public int FormatVersion => 1;
        public string Protect(string providerId, string plaintext) => plaintext;
        public string? Unprotect(string providerId, string protectedValue, int formatVersion) => protectedValue;
    }

    private sealed class NullAuditWriter : IAuditWriter
    {
        public Task WriteAsync(string action, string subjectType, string? subjectId, object? detail, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    private sealed class StubCurrentUser : ICurrentUser
    {
        public Guid? UserId => null;
        public string? DisplayName => null;
    }
}
