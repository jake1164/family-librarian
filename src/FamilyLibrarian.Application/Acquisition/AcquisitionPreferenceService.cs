using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Matching;
using FamilyLibrarian.Domain.Requests;

namespace FamilyLibrarian.Application.Acquisition;

public interface IAcquisitionPreferenceStore
{
    Task<IReadOnlyList<RequesterAcquisitionPreference>> ListAsync(IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken);
    Task EnsureAsync(RequesterAcquisitionPreference preference, CancellationToken cancellationToken);
    Task<bool> RemoveAsync(Guid userId, Guid preferenceId, CancellationToken cancellationToken);
}

/// <summary>Uses a fresh scope so the work lock reloads the current question rather than a recheck's tracked snapshot.</summary>
public interface ISavedAcquisitionQuestionAcceptor
{
    Task<bool> TryAcceptAsync(Guid requestId, CancellationToken cancellationToken);
}

public sealed record AcquisitionPreferenceView(Guid Id, string Kind, string? Value, DateTimeOffset CreatedAtUtc);
public enum PreferenceRemovalOutcome { Removed, NotFound, Unauthenticated }

public sealed class AcquisitionPreferenceService(IAcquisitionPreferenceStore store, ICurrentUser currentUser, IClock clock)
{
    public static (RequesterAcquisitionPreferenceKind Kind, string Value)? Key(AcquisitionQuestionKind kind, string? language) => kind switch
    {
        AcquisitionQuestionKind.CollectionContaining => (RequesterAcquisitionPreferenceKind.AcceptCollectionsContaining, string.Empty),
        AcquisitionQuestionKind.OtherLanguageOnly when LanguageAcceptance.PreferenceCode(language) is { Length: <= 16 } code =>
            (RequesterAcquisitionPreferenceKind.AcceptLanguage, code),
        _ => null
    };

    public async Task<IReadOnlyList<Guid>> MissingAsync(IEnumerable<Guid> activeRequesters, AcquisitionQuestionKind kind, string? language, CancellationToken cancellationToken)
    {
        var users = activeRequesters.Distinct().ToArray();
        if (Key(kind, language) is not { } key) return users;
        var preferences = await store.ListAsync(users, cancellationToken);
        return users.Where(user => !preferences.Any(p => p.UserId == user && p.Kind == key.Kind && p.Value == key.Value)).ToArray();
    }

    public async Task<bool> WasAskedAsync(BookRequest request, Guid userId, CancellationToken cancellationToken)
    {
        if (request.QuestionKind is not { } kind) return true;
        var candidate = request.ReviewCandidates.Single(c => c.RequestFormatId == request.QuestionFormatId);
        return (await MissingAsync(request.ActiveRequesterIds, kind, candidate.Language, cancellationToken)).Contains(userId);
    }

    public Task RememberAsync(Guid userId, AcquisitionQuestionKind kind, string? language, CancellationToken cancellationToken)
    {
        var key = Key(kind, language) ?? throw new InvalidOperationException("This choice cannot be saved as a preference.");
        return store.EnsureAsync(new(userId, key.Kind, key.Value, clock.UtcNow), cancellationToken);
    }

    public async Task<IReadOnlyList<AcquisitionPreferenceView>?> ListOwnAsync(CancellationToken cancellationToken) =>
        currentUser.UserId is not { } userId ? null : (await store.ListAsync([userId], cancellationToken))
            .Select(p => new AcquisitionPreferenceView(p.Id, p.Kind.ToString(), p.Value, p.CreatedAtUtc)).ToArray();

    public async Task<PreferenceRemovalOutcome> RemoveOwnAsync(Guid preferenceId, CancellationToken cancellationToken) =>
        currentUser.UserId is not { } userId ? PreferenceRemovalOutcome.Unauthenticated :
            await store.RemoveAsync(userId, preferenceId, cancellationToken) ? PreferenceRemovalOutcome.Removed : PreferenceRemovalOutcome.NotFound;
}
