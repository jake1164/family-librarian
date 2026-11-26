namespace FamilyLibrarian.Contracts.Accounts;

/// <param name="Format">"Ebook", "Audiobook" or "Both".</param>
public sealed record DefaultRequestFormatResponse(string Format);

public sealed record SetDefaultRequestFormatRequest(string Format);
