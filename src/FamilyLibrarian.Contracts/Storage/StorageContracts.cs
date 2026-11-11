namespace FamilyLibrarian.Contracts.Storage;

/// <summary>Free and total bytes of the volume holding the file zones; null when the host cannot tell.</summary>
public sealed record StorageVolumeResponse(long? FreeBytes, long? TotalBytes);

/// <summary>What is physically in one zone folder. <paramref name="UnrecognizedFiles"/> are older than a day and match no record (crash leftover candidates).</summary>
public sealed record StorageZoneResponse(
    string Zone, int Files, long Bytes, DateTimeOffset? OldestFileUtc, int UnrecognizedFiles, long UnrecognizedBytes);

/// <summary>Files the records say are held, grouped by state and media type. Numbers only: no title, name or path.</summary>
public sealed record HeldFileGroupResponse(
    string State, string MediaType, bool RuledOut, int Files, long Bytes, DateTimeOffset? OldestUpdatedAtUtc);

/// <param name="ApproximateRows">The planner's estimate; <paramref name="ApproximateBytes"/> includes indexes.</param>
public sealed record StorageTableResponse(string Schema, string Table, long ApproximateRows, long ApproximateBytes);

public sealed record StorageInsightResponse(
    DateTimeOffset AsOfUtc, StorageVolumeResponse Volume, IReadOnlyList<StorageZoneResponse> Zones,
    IReadOnlyList<HeldFileGroupResponse> HeldFiles, IReadOnlyList<StorageTableResponse> Tables, long DatabaseApproximateBytes);
