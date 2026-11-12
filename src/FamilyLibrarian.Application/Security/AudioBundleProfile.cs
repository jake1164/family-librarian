using System.Globalization;
using FamilyLibrarian.Application.Acquisition;
using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Domain.Evidence;

namespace FamilyLibrarian.Application.Security;

/// <summary>Reads and writes the audio duration facts the artifact profile is built from.</summary>
public static class AudioDurations
{
    /// <summary>The measured seconds from a technical inspector's duration fact (<c>seconds=3600;basis=...</c>), if one was taken.</summary>
    public static bool TryReadSeconds(IEnumerable<IdentityFact> technicalFacts, out double seconds)
    {
        seconds = 0;
        var value = technicalFacts.FirstOrDefault(fact => fact.Code == EvidenceReasonCodes.ArtifactAudio.DurationMeasured)?.Value;
        const string prefix = "seconds=";
        if (value is null || !value.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var end = value.IndexOf(';', StringComparison.Ordinal);
        return double.TryParse(end < 0 ? value[prefix.Length..] : value[prefix.Length..end], NumberStyles.Float,
            CultureInfo.InvariantCulture, out seconds) && seconds > 0;
    }

    public static IdentityFact TotalDuration(double seconds, int tracks) =>
        new("ev-profile-duration", EvidenceDimension.Profile, EvidenceReasonCodes.ArtifactProfile.AudioTotalDuration,
            EvidencePolarity.Unknown,
            $"seconds={Math.Round(seconds).ToString(CultureInfo.InvariantCulture)};tracks={tracks.ToString(CultureInfo.InvariantCulture)}");
}

/// <summary>Records the total listening time of a multi-file audiobook on each of its tracks.</summary>
public interface IAudioBundleProfile
{
    /// <summary>Best effort: never throws, never changes a decision.</summary>
    Task RecordAsync(IReadOnlyList<MediaAsset> tracks, CancellationToken cancellationToken);
}

/// <summary>
/// Sums the measured duration of every track of an automatic audiobook set (the technical inspectors read headers only) and
/// records <c>profile.audio.total_duration</c> on each track, so the figure is available wherever one track's evidence is
/// shown. A set with an unmeasurable track records nothing: a partial sum would read as a short book.
/// </summary>
public sealed class AudioBundleProfile(
    IAssetStagingStore stagingStore,
    IEnumerable<IAssetTechnicalInspector> inspectors,
    IArtifactEvidenceRecorder evidence) : IAudioBundleProfile
{
    private static readonly TimeSpan InspectionTimeout = TimeSpan.FromSeconds(30);

    public async Task RecordAsync(IReadOnlyList<MediaAsset> tracks, CancellationToken cancellationToken)
    {
        try
        {
            if (tracks.Count < 2) return;
            var total = 0d;
            foreach (var track in tracks)
            {
                var inspector = inspectors.FirstOrDefault(candidate => candidate.Supports(track));
                if (inspector is null) return;
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(InspectionTimeout);
                await using var content = await stagingStore.OpenAsync(track.StorageState, track.StoredFilename, timeout.Token);
                var findings = await inspector.InspectAsync(track, content, timeout.Token);
                if (!AudioDurations.TryReadSeconds(findings, out var seconds)) return;
                total += seconds;
            }

            var fact = AudioDurations.TotalDuration(total, tracks.Count);
            foreach (var track in tracks)
            {
                await evidence.RecordTechnicalInspectionAsync(track, [fact], "profile:audio-bundle", cancellationToken);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Evidence is best effort and must never alter the decision it describes.
        }
    }
}
