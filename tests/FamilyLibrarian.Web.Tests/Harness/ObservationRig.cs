using System.Collections.Concurrent;
using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Application.Catalog.Observation;
using FamilyLibrarian.Domain.Catalog;
using FamilyLibrarian.Domain.Providers;
using FamilyLibrarian.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FamilyLibrarian.Web.Tests.Harness;

/// <summary>A pinned clock the host reads instead of the system's, so dates, statuses and last-seen times are exact.</summary>
internal sealed class RigClock(DateTimeOffset start) : IClock
{
    private long ticks = start.UtcTicks;

    public DateTimeOffset UtcNow => new(Interlocked.Read(ref ticks), TimeSpan.Zero);

    public void Advance(TimeSpan by) => Interlocked.Add(ref ticks, by.Ticks);
}

/// <summary>
/// A deterministic metadata source whose records a test edits between observations. Counts every call, can fail, stall or run code
/// at the moment a detail fetch is answered (used to retire a Work "while the provider was being asked").
/// </summary>
internal sealed class RigProvider(string id) : IBookMetadataProvider
{
    private int detailCalls;
    private int searchCalls;

    public ConcurrentDictionary<string, BookCandidate> Records { get; } = new(StringComparer.Ordinal);

    public List<BookCandidate> SearchResults { get; } = [];

    public Exception? DetailFailure { get; set; }

    public Exception? SearchFailure { get; set; }

    public TimeSpan Delay { get; set; }

    public Func<string, Task>? OnDetails { get; set; }

    public int DetailCalls => Volatile.Read(ref detailCalls);

    public int SearchCalls => Volatile.Read(ref searchCalls);

    public string Id => id;

    public string DisplayName => $"Rig {id}";

    public async Task<BookCandidateSearchPage> SearchAsync(BookSearchQuery query, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref searchCalls);
        if (Delay > TimeSpan.Zero) await Task.Delay(Delay, cancellationToken);
        if (SearchFailure is not null) throw SearchFailure;
        return new BookCandidateSearchPage(SearchResults.ToArray(), false);
    }

    public async Task<BookCandidate?> GetDetailsAsync(string externalId, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref detailCalls);
        if (Delay > TimeSpan.Zero) await Task.Delay(Delay, cancellationToken);
        if (DetailFailure is not null) throw DetailFailure;
        if (OnDetails is not null) await OnDetails(externalId);
        return Records.TryGetValue(externalId, out var record) ? record : null;
    }
}

/// <summary>One production host over its own migrated database, with scripted providers and a pinned clock.</summary>
internal sealed class ObservationRig : IAsyncDisposable
{
    internal static readonly DateTimeOffset Start = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    private ObservationRig(string connection, FamilyLibrarianAppFactory host, RigClock clock, IReadOnlyList<RigProvider> providers)
    {
        Connection = connection;
        Host = host;
        Clock = clock;
        Providers = providers;
    }

    public string Connection { get; }

    public FamilyLibrarianAppFactory Host { get; }

    public RigClock Clock { get; }

    public IReadOnlyList<RigProvider> Providers { get; }

    public RigProvider Demo => Providers.Single(provider => provider.Id == "demo");

    public RigProvider OpenLibrary => Providers.Single(provider => provider.Id == "openlibrary");

    /// <param name="enableOpenLibrary">Enables the second registered provider the way an administrator would.</param>
    public static async Task<ObservationRig> CreateAsync(
        bool enableOpenLibrary = false,
        TimeSpan? refreshDeadline = null,
        Action<IServiceCollection>? configure = null,
        params string[] extraProviders)
    {
        if (PostgresFixture.UnavailableReason is not null) Assert.Inconclusive(PostgresFixture.UnavailableReason);
        var connection = await PostgresFixture.CreateSeededDatabaseAsync();
        var clock = new RigClock(Start);
        var providers = new List<RigProvider> { new("demo"), new("openlibrary") };
        providers.AddRange(extraProviders.Select(id => new RigProvider(id)));
        var host = new FamilyLibrarianAppFactory(connection, services =>
        {
            services.RemoveAll<IBookMetadataProvider>();
            foreach (var provider in providers) services.AddSingleton<IBookMetadataProvider>(provider);
            services.RemoveAll<IClock>();
            services.AddSingleton<IClock>(clock);
            configure?.Invoke(services);
            if (refreshDeadline is { } deadline)
            {
                services.RemoveAll<CatalogObservationService>();
                services.AddScoped(sp => new CatalogObservationService(
                    sp.GetRequiredService<FamilyLibrarian.Application.Integrations.IActiveMetadataProviderResolver>(),
                    sp.GetRequiredService<ICatalogRepository>(),
                    sp.GetRequiredService<ICatalogWriteScope>(),
                    sp.GetRequiredService<FamilyLibrarian.Application.Notifications.NotificationService>(),
                    sp.GetRequiredService<IClock>(),
                    sp.GetService<ICatalogResolutionDiagnostics>(),
                    deadline));
            }
        });
        var rig = new ObservationRig(connection, host, clock, providers);
        if (enableOpenLibrary) await rig.EnableAsync("openlibrary");
        return rig;
    }

    public async Task EnableAsync(string providerId)
    {
        await using var scope = Host.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var setting = await database.ProviderSettings.SingleOrDefaultAsync(item => item.ProviderId == providerId);
        if (setting is null)
        {
            setting = new ProviderSetting(providerId, Clock.UtcNow);
            database.Add(setting);
        }

        setting.SetEnabled(true, null, Clock.UtcNow);
        await database.SaveChangesAsync();
    }

    public async Task DisableAsync(string providerId)
    {
        await using var scope = Host.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var setting = await database.ProviderSettings.SingleAsync(item => item.ProviderId == providerId);
        setting.SetEnabled(false, null, Clock.UtcNow);
        await database.SaveChangesAsync();
    }

    public async Task<CatalogWorkResolution> ResolveAsync(string providerId, string externalId)
    {
        await using var scope = Host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<CatalogWorkResolver>()
            .ResolveAsync(providerId, externalId, CancellationToken.None);
    }

    public async Task<WorkRefreshReport> RefreshAsync(Guid workId, CancellationToken cancellationToken = default)
    {
        await using var scope = Host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<CatalogObservationService>().RefreshWorkAsync(workId, cancellationToken);
    }

    public async Task<T> DbAsync<T>(Func<AppDbContext, Task<T>> action)
    {
        await using var scope = Host.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var result = await action(database);
        await database.SaveChangesAsync();
        return result;
    }

    public Task<Work> LoadWorkAsync(Guid workId) =>
        DbAsync(database => database.Works.AsNoTracking()
            .Include(work => work.Authors).ThenInclude(credit => credit.Author)
            .Include(work => work.Editions)
            .Include(work => work.SeriesEntries).ThenInclude(entry => entry.Series)
            .SingleAsync(work => work.Id == workId));

    public Task<IReadOnlyList<MetadataClaim>> ClaimsAsync(Guid workId) =>
        DbAsync<IReadOnlyList<MetadataClaim>>(async database => await database.MetadataClaims.AsNoTracking()
            .Where(claim => claim.WorkId == workId).OrderBy(claim => claim.Field).ThenBy(claim => claim.ScopeKey)
            .ThenBy(claim => claim.ProviderId).ThenBy(claim => claim.FirstSeenAtUtc).ToListAsync());

    /// <summary>
    /// Many other sources' retained history for the Work's description. Every source key is within retention (<paramref name="perSource"/>
    /// values, only the last current) and every source id sorts before the real one, so a prefix read of the Work's claims would hide
    /// the selected claim of the real source.
    /// </summary>
    public Task SeedDescriptionHistoryAsync(Guid workId, int sources, int perSource) =>
        DbAsync(async database =>
        {
            for (var source = 0; source < sources; source++)
            {
                for (var value = 0; value < perSource; value++)
                {
                    var claim = new MetadataClaim(
                        workId, MetadataClaimField.Description, "work", $"a{source:D3}", "e", $"{{\"text\":\"value {source}-{value}\"}}",
                        Guid.NewGuid().ToString("N"), Start.AddMinutes(value));
                    if (value < perSource - 1)
                    {
                        claim.Supersede();
                    }

                    database.MetadataClaims.Add(claim);
                }
            }

            await Task.CompletedTask;
            return 0;
        });

    public Task<IReadOnlyList<SourceObservation>> ObservationsAsync(Guid workId) =>
        DbAsync<IReadOnlyList<SourceObservation>>(async database => await database.SourceObservations.AsNoTracking()
            .Where(observation => observation.WorkId == workId).OrderBy(observation => observation.ObservedAtUtc).ToListAsync());

    /// <summary>Every column of every row of the catalog tables, as text, except the named bookkeeping columns.</summary>
    public async Task<string[]> CatalogRowsAsync(params string[] ignoredColumns)
    {
        await using var data = new Npgsql.NpgsqlConnection(Connection);
        await data.OpenAsync();
        var rows = new List<string>();
        foreach (var table in new[]
                 {
                     "works", "editions", "authors", "work_authors", "series", "series_entries", "external_references",
                     "source_observations", "metadata_claims"
                 })
        {
            await using var command = new Npgsql.NpgsqlCommand($"SELECT to_jsonb(t.*)::text FROM catalog.{table} t", data);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var node = System.Text.Json.Nodes.JsonNode.Parse(reader.GetString(0))!.AsObject();
                foreach (var column in ignoredColumns) node.Remove(column);
                rows.Add(table + "|" + node.ToJsonString());
            }
        }

        rows.Sort(StringComparer.Ordinal);
        return rows.ToArray();
    }

    public async ValueTask DisposeAsync() => await Host.DisposeAsync();
}

/// <summary>Builds provider records for a test without repeating the positional constructor.</summary>
internal static class RigBooks
{
    public static BookCandidate Book(
        string provider,
        string externalId,
        string title,
        string[]? authors = null,
        BookEditionCandidate[]? editions = null,
        BookSeriesCandidate[]? series = null,
        string? description = "A description.",
        DateOnly? date = null,
        DatePrecision? precision = null,
        DateBasis? basis = null,
        string? language = null,
        string? cover = null,
        int? pages = null,
        string? workTitle = null) =>
        new(provider, $"Rig {provider}", externalId, title, authors ?? ["Rig Author"], description, cover, date, editions ?? [], series ?? [],
            PageCount: pages, Language: language, WorkTitle: workTitle)
        {
            PublicationDatePrecision = precision,
            PublicationDateBasis = basis
        };

    public static BookEditionCandidate Edition(
        string title, string? isbn, string format = "Ebook", string? language = null, string? publisher = null, DateOnly? date = null) =>
        new(title, isbn, format, date, language, publisher);

    public static BookSeriesCandidate Series(string name, string? label, bool primary = true, decimal? sort = null, string? sourceId = null) =>
        new(name, label, primary, sort) { SourceSeriesId = sourceId };
}
