namespace FamilyLibrarian.Domain.Accounts;

/// <summary>
/// Which formats a household member's request form starts with. It only
/// preselects choices: every request still states its own formats.
/// </summary>
public enum DefaultRequestFormat
{
    Ebook = 1,
    Audiobook = 2,
    Both = 3
}
