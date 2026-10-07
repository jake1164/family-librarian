namespace FamilyLibrarian.Application.Acquisition;

/// <summary>
/// Where one numbered part of an automatic audiobook set belongs: the file is
/// staged as track <see cref="Number"/> of <see cref="Total"/> in the bundle
/// named by <see cref="SetId"/>, so the existing bundle rule (publish only once
/// every sibling is trusted) covers the set without a separate mechanism.
/// </summary>
public sealed record AudiobookPartSetSlot(Guid SetId, int Number, int Total);
