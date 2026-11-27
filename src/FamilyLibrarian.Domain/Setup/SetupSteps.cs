namespace FamilyLibrarian.Domain.Setup;

/// <summary>The personal settings a new family member is walked through. Stored per account as a bit set.</summary>
[Flags]
public enum SetupSteps
{
    None = 0,
    Formats = 1,
    Kindle = 2,
    Messaging = 4
}
