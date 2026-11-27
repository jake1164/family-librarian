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
    public async Task TurningOnWithNoRecordedTestSendsOneToTheAdministratorAndEnables()
    {
        var context = new TestContext();
        await context.ConfigureAsync();

        var enabled = await context.Service.SetEnabledAsync(true, "admin@example.test", CancellationToken.None);

        Assert.IsTrue(enabled.Succeeded);
        Assert.IsTrue(enabled.Status!.IsEnabled);
        Assert.AreEqual(1, context.Sender.Sends);
        Assert.AreEqual("admin@example.test", context.Sender.LastRecipientAddress);
        Assert.AreEqual(true, enabled.Status.LastTestSucceeded);
    }

    [TestMethod]
    public async Task EmailStaysOffWhenTheTurnOnTestFails()
    {
        var context = new TestContext();
        await context.ConfigureAsync();
        context.Sender.NextOutcome = new ConnectionTestOutcome(false, "The server refused the password.");

        var enabled = await context.Service.SetEnabledAsync(true, "admin@example.test", CancellationToken.None);

        Assert.IsFalse(enabled.Succeeded);
        StringAssert.Contains(enabled.Error, "Email stayed off");
        StringAssert.Contains(enabled.Error, "The server refused the password.");
        Assert.IsFalse(context.Store.Current!.IsEnabled);
        Assert.AreEqual(false, context.Store.Current.LastTestSucceeded);
    }

    [TestMethod]
    public async Task TurningOnWithoutAnAddressToTestIsRefusedWithoutSending()
    {
        var context = new TestContext();
        await context.ConfigureAsync();

        var enabled = await context.Service.SetEnabledAsync(true, null, CancellationToken.None);

        Assert.IsFalse(enabled.Succeeded);
        StringAssert.Contains(enabled.Error, "Send a test email below");
        Assert.AreEqual(0, context.Sender.Sends);
    }

    [TestMethod]
    public async Task ARecordedPassIsNotRepeatedAndSavingNewSettingsMeansTestingAgain()
    {
        var context = new TestContext();
        await context.ConfigureAsync();
        await context.Service.SetEnabledAsync(true, "admin@example.test", CancellationToken.None);
        await context.Service.SetEnabledAsync(false, null, CancellationToken.None);

        await context.Service.SetEnabledAsync(true, "admin@example.test", CancellationToken.None);
        Assert.AreEqual(1, context.Sender.Sends, "The saved settings had already passed.");

        await context.Service.SetSettingsAsync(
            "smtp.other.example.test", 587, SmtpSecurityMode.StartTls, "mailer",
            "library@example.test", "Family Librarian", null, CancellationToken.None);
        await context.Service.SetEnabledAsync(false, null, CancellationToken.None);
        await context.Service.SetEnabledAsync(true, "admin@example.test", CancellationToken.None);

        Assert.AreEqual(2, context.Sender.Sends);
    }

    [TestMethod]
    [DataRow("smtp.other.example.test", null)]
    [DataRow("smtp.example.test", "a-different-password")]
    public async Task TestingUnsavedValuesIsNotRecordedAgainstTheSavedSettings(string host, string? password)
    {
        var context = new TestContext();
        await context.ConfigureAsync();

        var test = await context.Service.SendTestAsync(
            "admin@example.test", host, 587, SmtpSecurityMode.StartTls, "mailer", password,
            "library@example.test", "Family Librarian", CancellationToken.None);

        Assert.IsTrue(test.Outcome!.Succeeded);
        StringAssert.Contains(test.Outcome.Message, "not saved yet");
        Assert.IsNull(context.Store.Current!.LastTestSucceeded);

        // So turning email on still has to try the settings that are actually saved.
        context.Sender.NextOutcome = new ConnectionTestOutcome(false, "The saved settings do not work.");
        var enabled = await context.Service.SetEnabledAsync(true, "admin@example.test", CancellationToken.None);
        Assert.IsFalse(enabled.Succeeded);
    }

    [TestMethod]
    public async Task TestingTheSavedValuesIsRecorded()
    {
        var context = new TestContext();
        await context.ConfigureAsync();

        var test = await context.Service.SendTestAsync(
            "admin@example.test", "smtp.example.test", 587, SmtpSecurityMode.StartTls, "mailer", "super-secret",
            "library@example.test", "Family Librarian", CancellationToken.None);

        Assert.AreEqual(true, test.Status!.LastTestSucceeded);
    }

    [TestMethod]
    public async Task SavingSettingsStoresATypedPasswordAndABlankOneKeepsIt()
    {
        var context = new TestContext();

        await context.Service.SetSettingsAsync(
            "smtp.example.test", 587, SmtpSecurityMode.StartTls, "mailer",
            "library@example.test", null, " typed-password ", CancellationToken.None);
        Assert.AreEqual("typed-password", context.Store.Current!.ProtectedPassword);

        await context.Service.SetSettingsAsync(
            "smtp.example.test", 587, SmtpSecurityMode.StartTls, "mailer",
            "library@example.test", null, "  ", CancellationToken.None);
        Assert.AreEqual("typed-password", context.Store.Current.ProtectedPassword);
    }

    [TestMethod]
    public async Task UsernameAndPasswordMustBeConfiguredAsAPair()
    {
        var context = new TestContext();
        await context.Service.SetSettingsAsync(
            "smtp.example.test", 587, SmtpSecurityMode.StartTls, "mailer",
            "library@example.test", null, null, CancellationToken.None);

        var test = await context.Service.SendTestAsync(
            "admin@example.test", "smtp.example.test", 587, SmtpSecurityMode.StartTls, "mailer", null,
            "library@example.test", null, CancellationToken.None);

        Assert.IsFalse(test.Succeeded);
        StringAssert.Contains(test.Error, "username and password");
    }

    [TestMethod]
    public async Task SendTestAsyncFallsBackToTheSavedSettingsWhenOnlyARecipientIsGiven()
    {
        var context = new TestContext();
        await context.ConfigureAsync();

        var test = await context.Service.SendTestAsync(
            "admin@example.test", null, null, null, null, null, null, null, CancellationToken.None);

        Assert.IsTrue(test.Succeeded);
        Assert.IsTrue(test.Outcome!.Succeeded);
        Assert.AreEqual("admin@example.test", context.Sender.LastRecipientAddress);
    }

    [TestMethod]
    public async Task InvalidTestRecipientDoesNotCallTheTransport()
    {
        var context = new TestContext();
        await context.ConfigureAsync();

        var test = await context.Service.SendTestAsync(
            "not-an-email", "smtp.example.test", 587, SmtpSecurityMode.StartTls, "mailer", null,
            "library@example.test", "Family Librarian", CancellationToken.None);

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
                "library@example.test", "Family Librarian", null, CancellationToken.None);
            await Service.SetPasswordAsync("super-secret", CancellationToken.None);
        }
    }

    private sealed class FakeSmtpSettingsStore : ISmtpSettingsStore
    {
        private SmtpSettings? settings;

        public SmtpSettings? Current => settings;

        public Task<SmtpSettings?> FindAsync(CancellationToken cancellationToken) => Task.FromResult(settings);

        public Task<SmtpSettings> GetOrCreateAsync(CancellationToken cancellationToken) =>
            Task.FromResult(settings ??= new SmtpSettings(Now));

        public Task SaveChangesAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeSmtpTestSender : ISmtpTestSender
    {
        public string? LastRecipientAddress { get; private set; }

        public int Sends { get; private set; }

        public ConnectionTestOutcome NextOutcome { get; set; } = new(true, "Accepted.");

        public Task<ConnectionTestOutcome> SendTestAsync(
            SmtpSettings settings, string recipientAddress, CancellationToken cancellationToken)
        {
            LastRecipientAddress = recipientAddress;
            Sends++;
            return Task.FromResult(NextOutcome);
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
