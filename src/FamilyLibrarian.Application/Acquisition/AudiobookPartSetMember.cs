namespace FamilyLibrarian.Application.Acquisition;

/// <summary>
/// The caller's claim that a provider result is one numbered part of a
/// specific complete audiobook set. It is only a claim:
/// <see cref="DirectAcquisitionService"/> re-derives the set from a fresh
/// provider search and refuses unless exactly these parts come back.
/// </summary>
public sealed record AudiobookPartSetMember(
    Guid SetId, int Number, int Total, IReadOnlyList<string> MemberResultIds);
