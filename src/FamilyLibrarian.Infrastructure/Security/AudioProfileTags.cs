using System.Globalization;
using FamilyLibrarian.Application.Security;
using FamilyLibrarian.Domain.Evidence;

namespace FamilyLibrarian.Infrastructure.Security;

/// <summary>A closed set of audiobook metadata, separate from title/author identity and bounded before use.</summary>
internal sealed class AudioProfileTags
{
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

    public void Add(string key, string value)
    {
        if (ArtifactEvidence.Bound(value) is null) return;
        if (key.ToUpperInvariant() is "SERIES" or "SERIES-PART" or "ISBN" or "ASIN" or "MVNM" or "MVIN")
            _values.TryAdd(key, value);
    }

    public IReadOnlyList<IdentityFact> Facts()
    {
        var facts = new List<IdentityFact>();
        string? Read(string key)
        {
            if (!_values.TryGetValue(key, out var value)) return null;
            var bounded = ArtifactEvidence.Bound(value);
            if (value.Length <= ArtifactEvidence.MaxValueLength) return bounded;
            facts.Add(Fact($"ev-audio-truncated-{facts.Count}", EvidenceReasonCodes.ArtifactProfile.AudioTagTruncated,
                $"{key}:{bounded}"));
            return null; // A shortened value must never fabricate series or identifier agreement.
        }

        var seriesKey = _values.ContainsKey("series") ? "series" : "mvnm";
        var positionKey = _values.ContainsKey("series-part") ? "series-part" : "mvin";
        var series = Read(seriesKey);
        var position = Read(positionKey);
        if (position is not null)
        {
            facts.Add(Fact("ev-audio-series-part", EvidenceReasonCodes.ArtifactProfile.AudioSeriesPosition, position));
            if (positionKey == "mvin") position = MovementPosition(position);
        }
        if (series is not null)
        {
            var value = position is null ? series : $"{series}#{position}";
            if (value.Length <= ArtifactEvidence.MaxValueLength)
                facts.Add(Fact("ev-audio-series", EvidenceReasonCodes.ArtifactProfile.PackageSeries, value));
            else
                facts.Add(Fact("ev-audio-series-truncated", EvidenceReasonCodes.ArtifactProfile.AudioTagTruncated, value));
        }
        if (Read("isbn") is { } isbn) facts.Add(Fact("ev-audio-isbn", EvidenceReasonCodes.ArtifactProfile.PackageIdentifier, isbn));
        if (Read("asin") is { } asin) facts.Add(Fact("ev-audio-asin", EvidenceReasonCodes.ArtifactProfile.AudioAsin, asin));
        return facts;
    }

    private static string? MovementPosition(string value)
    {
        var parts = value.Split('/');
        if (parts.Length is < 1 or > 2 || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var index) || index <= 0)
            return null;
        if (parts.Length == 2 && (!long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var count) || count < index))
            return null;
        return index.ToString(CultureInfo.InvariantCulture);
    }

    private static IdentityFact Fact(string id, string code, string? value) =>
        new(id, EvidenceDimension.Profile, code, EvidencePolarity.Unknown, ArtifactEvidence.Bound(value));
}
