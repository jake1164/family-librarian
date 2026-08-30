using FamilyLibrarian.Application.Communications;
using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Domain.Communications;
using FamilyLibrarian.Infrastructure.Communications;

namespace FamilyLibrarian.Infrastructure.Tests.Communications;

[TestClass]
public sealed class SmtpOutboundCommunicationProviderTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task IsDisabledWhenNoSettingsHaveBeenSaved()
    {
        var provider = new SmtpOutboundCommunicationProvider(
            new FakeSmtpSettingsStore(null), new FakeUserEmailLookup(), new FakeCredentialProtector());

        Assert.IsFalse(await provider.IsEnabledAsync(CancellationToken.None));
    }

    [TestMethod]
    public async Task IsDisabledWhenSettingsAreNotEnabled()
    {
        var settings = new SmtpSettings(Now);
        var provider = new SmtpOutboundCommunicationProvider(
            new FakeSmtpSettingsStore(settings), new FakeUserEmailLookup(), new FakeCredentialProtector());

        Assert.IsFalse(await provider.IsEnabledAsync(CancellationToken.None));
    }

    [TestMethod]
    public async Task IsEnabledWhenSettingsAreEnabled()
    {
        var settings = new SmtpSettings(Now);
        settings.SetEnabled(true, actorUserId: null, Now);
        var provider = new SmtpOutboundCommunicationProvider(
            new FakeSmtpSettingsStore(settings), new FakeUserEmailLookup(), new FakeCredentialProtector());

        Assert.IsTrue(await provider.IsEnabledAsync(CancellationToken.None));
    }

    [TestMethod]
    public async Task SendFailsWhenTheRecipientHasNoEmailAddress()
    {
        var settings = new SmtpSettings(Now);
        settings.SetSettings(
            "smtp.example.test", 587, SmtpSecurityMode.StartTls, null, "library@example.test", "Family Librarian",
            actorUserId: null, Now);
        settings.SetEnabled(true, actorUserId: null, Now);
        var provider = new SmtpOutboundCommunicationProvider(
            new FakeSmtpSettingsStore(settings), new FakeUserEmailLookup(email: null), new FakeCredentialProtector());
        var communication = new OutboundCommunication(
            Guid.NewGuid(), OutboundCommunicationTypes.RequestStatusChanged, "Body", "Subject", null, null, null, Now);

        var result = await provider.SendAsync(communication, CancellationToken.None);

        Assert.IsFalse(result.Succeeded);
        StringAssert.Contains(result.Error, "email address");
    }

    private sealed class FakeSmtpSettingsStore(SmtpSettings? settings) : ISmtpSettingsStore
    {
        public Task<SmtpSettings?> FindAsync(CancellationToken cancellationToken) => Task.FromResult(settings);

        public Task<SmtpSettings> GetOrCreateAsync(CancellationToken cancellationToken) =>
            Task.FromResult(settings ?? throw new InvalidOperationException());

        public Task SaveChangesAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeUserEmailLookup(string? email = "reader@example.test") : IUserEmailLookup
    {
        public Task<string?> GetEmailAsync(Guid userId, CancellationToken cancellationToken) => Task.FromResult(email);
    }

    private sealed class FakeCredentialProtector : ICredentialProtector
    {
        public int FormatVersion => 1;
        public string Protect(string providerId, string plaintext) => plaintext;
        public string? Unprotect(string providerId, string protectedValue, int formatVersion) => protectedValue;
    }
}
