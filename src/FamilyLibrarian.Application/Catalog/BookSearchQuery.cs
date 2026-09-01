namespace FamilyLibrarian.Application.Catalog;

public sealed record BookSearchQuery(string Text, int Page = 1)
{
    public const int MaximumPage = 100;

    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Text);
        ArgumentOutOfRangeException.ThrowIfLessThan(Page, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(Page, MaximumPage);
    }
}
