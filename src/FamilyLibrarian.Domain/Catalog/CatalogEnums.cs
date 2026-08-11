namespace FamilyLibrarian.Domain.Catalog;

public enum PublicationStatus
{
    Unknown = 0,
    Published = 1,
    Forthcoming = 2
}

public enum EditionFormat
{
    Unknown = 0,
    Hardcover = 1,
    Paperback = 2,
    Ebook = 3,
    Audiobook = 4,
    Other = 5
}

public enum SeriesStatus
{
    Unknown = 0,
    Active = 1,
    Completed = 2
}

public enum ExternalReferenceEntityType
{
    Work = 1,
    Edition = 2,
    Author = 3,
    Series = 4
}
