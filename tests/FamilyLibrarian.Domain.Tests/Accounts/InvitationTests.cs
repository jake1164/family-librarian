using FamilyLibrarian.Domain.Accounts;

namespace FamilyLibrarian.Domain.Tests.Accounts;

/// <summary>
/// The rules that decide whether an invitation link still opens the door.
/// </summary>
[TestClass]
public sealed class InvitationTests
{
    private static readonly DateTimeOffset CreatedAt =
        new(2026, 8, 13, 9, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset ExpiresAt = CreatedAt.AddDays(7);
    private static readonly Guid Admin = Guid.NewGuid();
    private static readonly Guid Invitee = Guid.NewGuid();

    [TestMethod]
    public void ANewInvitationIsRedeemableUntilItExpires()
    {
        var invitation = Create();

        Assert.IsTrue(invitation.CanBeRedeemedAt(CreatedAt));
        Assert.IsTrue(invitation.CanBeRedeemedAt(ExpiresAt.AddSeconds(-1)));
        Assert.IsFalse(invitation.IsRedeemed);
        Assert.IsFalse(invitation.IsRevoked);
    }

    [TestMethod]
    public void ExpiryIsInclusiveOfTheExpiryMomentItself()
    {
        var invitation = Create();

        Assert.IsTrue(invitation.IsExpiredAt(ExpiresAt));
        Assert.IsFalse(invitation.CanBeRedeemedAt(ExpiresAt));
    }

    [TestMethod]
    public void AnInvitationCannotExpireBeforeItIsCreated() =>
        Assert.ThrowsExactly<ArgumentException>(() => new Invitation(
            "reader@example.test",
            "hash",
            RoleNames.User,
            Admin,
            CreatedAt,
            CreatedAt));

    [TestMethod]
    public void TheAddressIsKeptAsTypedAndNormalizedSeparatelyForMatching()
    {
        var invitation = new Invitation(
            "  Reader@Example.Test  ",
            "hash",
            RoleNames.User,
            Admin,
            CreatedAt,
            ExpiresAt);

        // Trimmed but not shouted back: this is the address the family member
        // sees, and the one their account is created with.
        Assert.AreEqual("Reader@Example.Test", invitation.Email);
        Assert.AreEqual("READER@EXAMPLE.TEST", invitation.NormalizedEmail);
    }

    [TestMethod]
    public void AnOverlongAddressIsRejected() =>
        Assert.ThrowsExactly<ArgumentException>(() => new Invitation(
            new string('x', Invitation.MaxEmailLength) + "@example.test",
            "hash",
            RoleNames.User,
            Admin,
            CreatedAt,
            ExpiresAt));

    [TestMethod]
    public void RedeemingOnceMarksItUsed()
    {
        var invitation = Create();

        invitation.Redeem(Invitee, CreatedAt.AddHours(1));

        Assert.IsTrue(invitation.IsRedeemed);
        Assert.AreEqual(Invitee, invitation.RedeemedByUserId);
        Assert.IsFalse(invitation.CanBeRedeemedAt(CreatedAt.AddHours(2)));
    }

    [TestMethod]
    public void RedeemingTwiceIsRefused()
    {
        var invitation = Create();
        invitation.Redeem(Invitee, CreatedAt.AddHours(1));

        Assert.ThrowsExactly<InvitationNotRedeemableException>(() =>
            invitation.Redeem(Guid.NewGuid(), CreatedAt.AddHours(2)));
    }

    [TestMethod]
    public void AnExpiredInvitationCannotBeRedeemed()
    {
        var invitation = Create();

        Assert.ThrowsExactly<InvitationNotRedeemableException>(() =>
            invitation.Redeem(Invitee, ExpiresAt.AddSeconds(1)));
    }

    [TestMethod]
    public void ARevokedInvitationCannotBeRedeemed()
    {
        var invitation = Create();
        invitation.Revoke(Admin, CreatedAt.AddHours(1));

        Assert.IsTrue(invitation.IsRevoked);
        Assert.ThrowsExactly<InvitationNotRedeemableException>(() =>
            invitation.Redeem(Invitee, CreatedAt.AddHours(2)));
    }

    [TestMethod]
    public void RevokingTwiceIsHarmless()
    {
        var invitation = Create();
        invitation.Revoke(Admin, CreatedAt.AddHours(1));

        // A double click must not fail; the second call keeps the first timestamp.
        invitation.Revoke(Admin, CreatedAt.AddHours(2));

        Assert.AreEqual(CreatedAt.AddHours(1), invitation.RevokedAtUtc);
    }

    [TestMethod]
    public void ARedeemedInvitationCannotBeRevoked()
    {
        var invitation = Create();
        invitation.Redeem(Invitee, CreatedAt.AddHours(1));

        // The account it created already exists; disabling that account is the
        // operation that actually removes access.
        Assert.ThrowsExactly<InvitationNotRedeemableException>(() =>
            invitation.Revoke(Admin, CreatedAt.AddHours(2)));
    }

    [TestMethod]
    public void TheStateDescriptionExplainsWhyALinkFailed()
    {
        Assert.AreEqual("it is valid", Create().DescribeState(CreatedAt));
        Assert.AreEqual("it has expired", Create().DescribeState(ExpiresAt));

        var redeemed = Create();
        redeemed.Redeem(Invitee, CreatedAt.AddHours(1));
        Assert.AreEqual("it has already been used", redeemed.DescribeState(CreatedAt.AddHours(2)));

        var revoked = Create();
        revoked.Revoke(Admin, CreatedAt.AddHours(1));
        Assert.AreEqual("it was withdrawn", revoked.DescribeState(CreatedAt.AddHours(2)));
    }

    private static Invitation Create() => new(
        "reader@example.test",
        "hash",
        RoleNames.User,
        Admin,
        CreatedAt,
        ExpiresAt);
}

[TestClass]
public sealed class UserStatusTests
{
    [TestMethod]
    [DataRow(UserStatus.Active, true)]
    [DataRow(UserStatus.Invited, false)]
    [DataRow(UserStatus.PendingApproval, false)]
    [DataRow(UserStatus.Disabled, false)]
    public void OnlyAnActiveAccountMayHoldASession(UserStatus status, bool expected) =>
        Assert.AreEqual(expected, UserStatuses.CanSignIn(status));
}
