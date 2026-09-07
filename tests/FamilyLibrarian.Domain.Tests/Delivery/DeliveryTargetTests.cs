using FamilyLibrarian.Domain.Delivery;

namespace FamilyLibrarian.Domain.Tests.Delivery;

[TestClass]
public sealed class DeliveryTargetTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 7, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid UserId = Guid.NewGuid();

    [TestMethod]
    public void ANewTargetStartsEnabledAndNotDefault()
    {
        var target = new DeliveryTarget(UserId, DeliveryTargetProvider.CwaKindleEmail, "Kindle", "jason@kindle.com", Now);

        Assert.IsTrue(target.IsEnabled);
        Assert.IsFalse(target.IsDefault);
        Assert.AreEqual("jason@kindle.com", target.Address);
        Assert.AreEqual(Now, target.CreatedAtUtc);
        Assert.AreEqual(Now, target.UpdatedAtUtc);
    }

    [TestMethod]
    public void AnEmptyUserIdIsRejected() =>
        Assert.ThrowsExactly<ArgumentException>(() =>
            new DeliveryTarget(Guid.Empty, DeliveryTargetProvider.CwaKindleEmail, "Kindle", "jason@kindle.com", Now));

    [TestMethod]
    public void AnUnknownProviderIsRejected() =>
        Assert.ThrowsExactly<ArgumentException>(() =>
            new DeliveryTarget(UserId, (DeliveryTargetProvider)99, "Kindle", "jason@kindle.com", Now));

    [TestMethod]
    public void ABlankNameIsRejected() =>
        Assert.ThrowsExactly<ArgumentException>(() =>
            new DeliveryTarget(UserId, DeliveryTargetProvider.CwaKindleEmail, " ", "jason@kindle.com", Now));

    [TestMethod]
    public void ABlankAddressIsRejected() =>
        Assert.ThrowsExactly<ArgumentException>(() =>
            new DeliveryTarget(UserId, DeliveryTargetProvider.CwaKindleEmail, "Kindle", " ", Now));

    [TestMethod]
    public void ANameLongerThanTheColumnIsRejected() =>
        Assert.ThrowsExactly<ArgumentException>(() =>
            new DeliveryTarget(
                UserId, DeliveryTargetProvider.CwaKindleEmail, new string('x', DeliveryTarget.MaxNameLength + 1), "jason@kindle.com", Now));

    [TestMethod]
    public void UpdateAddressTrimsAndStampsUpdatedAt()
    {
        var target = new DeliveryTarget(UserId, DeliveryTargetProvider.CwaKindleEmail, "Kindle", "jason@kindle.com", Now);

        target.UpdateAddress(" jason.new@kindle.com ", Now.AddMinutes(5));

        Assert.AreEqual("jason.new@kindle.com", target.Address);
        Assert.AreEqual(Now.AddMinutes(5), target.UpdatedAtUtc);
    }

    [TestMethod]
    public void SetEnabledTogglesTheFlag()
    {
        var target = new DeliveryTarget(UserId, DeliveryTargetProvider.CwaKindleEmail, "Kindle", "jason@kindle.com", Now);

        target.SetEnabled(false, Now.AddMinutes(1));

        Assert.IsFalse(target.IsEnabled);
    }

    [TestMethod]
    public void SetDefaultTogglesTheFlag()
    {
        var target = new DeliveryTarget(UserId, DeliveryTargetProvider.CwaKindleEmail, "Kindle", "jason@kindle.com", Now);

        target.SetDefault(true, Now.AddMinutes(1));

        Assert.IsTrue(target.IsDefault);
    }
}
