using FamilyLibrarian.Application.Acquisition;
using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Domain.Requests;

namespace FamilyLibrarian.Application.Providers;

/// <summary>One source's variant results, with the same record/release deduplication at selection and acquisition.</summary>
internal sealed class ExternalCandidatePool(RequestMediaType mediaType)
{
    private readonly List<FulfillmentOption> _options = [];
    private readonly Dictionary<string, SearchVariantRef> _found = new(StringComparer.Ordinal);

    public IReadOnlyList<FulfillmentOption> Options => _options;
    public IReadOnlyDictionary<string, SearchVariantRef> Found => _found;

    /// <summary>Same record id keeps the first; the same release under another id keeps the higher-ranked.</summary>
    public void Merge(IReadOnlyList<FulfillmentOption> added, SearchVariantRef variant)
    {
        foreach (var option in added)
        {
            if (_found.ContainsKey(option.ProviderResultId)) continue;

            var fingerprint = ExternalReleaseFingerprint.Compute(option.ReleaseName, option.SizeBytes);
            var twin = fingerprint is null ? null : _options.FirstOrDefault(existing =>
                ExternalReleaseFingerprint.Compute(existing.ReleaseName, existing.SizeBytes) == fingerprint);
            if (twin is null)
            {
                _options.Add(option);
                _found[option.ProviderResultId] = variant;
            }
            else if (ExternalCandidateRanker.Rank([twin, option], mediaType)[0] == option)
            {
                _options[_options.IndexOf(twin)] = option;
                _found.Remove(twin.ProviderResultId);
                _found[option.ProviderResultId] = variant;
            }
        }
    }
}
