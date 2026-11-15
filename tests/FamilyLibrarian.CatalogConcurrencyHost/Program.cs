using System.Collections.Concurrent;
using System.Text.Json;
using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Infrastructure;
using FamilyLibrarian.Infrastructure.Catalog;
using FamilyLibrarian.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

// Line protocol. stdin: one JSON command per line. stdout: one JSON event per line. Nothing else is written to stdout, and the
// connection string is read from the environment, never from a command-line argument, so it cannot appear in a process listing.
//   {"id":1,"op":"resolve","provider":"demo","externalId":"x","candidate":{...BookCandidate...}}
//   {"id":2,"op":"retire"}
//   {"op":"exit"}
// Events: {"event":"ready","pid":N}, {"event":"started","id":N}, {"event":"result","id":N,...}.
namespace FamilyLibrarian.CatalogConcurrencyHost;

internal static class CatalogHost
{
    private static async Task<int> Main()
    {
        const string ConnectionVariable = "FL_CATALOG_TEST_CONNECTION";
        const string ApplicationNameVariable = "FL_CATALOG_TEST_APPLICATION_NAME";

        var baseConnection = Environment.GetEnvironmentVariable(ConnectionVariable);
        var applicationName = Environment.GetEnvironmentVariable(ApplicationNameVariable);
        if (string.IsNullOrEmpty(baseConnection) || string.IsNullOrEmpty(applicationName))
        {
            await Console.Error.WriteLineAsync($"{ConnectionVariable} and {ApplicationNameVariable} are required.");
            return 2;
        }

        var connection = new NpgsqlConnectionStringBuilder(baseConnection) { ApplicationName = applicationName }.ConnectionString;
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:FamilyLibrarian"] = connection,
                ["MetadataProviders:Demo:Enabled"] = "true",
                ["MetadataProviders:OpenLibrary:Enabled"] = "true",
                ["MetadataProviders:GoogleBooks:ApiKey"] = string.Empty
            })
            .Build();

        var candidates = new ConcurrentDictionary<(string Provider, string ExternalId), BookCandidate>();
        var providerCalls = 0;
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddHttpContextAccessor();
        services.AddInfrastructure(configuration);
        // The only replacements: the provider transports (deterministic echo providers carrying registered ids, so production
        // enablement still decides whether they may be asked) and the current user (there is no HTTP request in this process).
        services.RemoveAll<IBookMetadataProvider>();
        foreach (var id in new[] { "demo", "openlibrary" })
        {
            services.AddSingleton<IBookMetadataProvider>(new EchoProvider(id, candidates, () => Interlocked.Increment(ref providerCalls)));
        }

        services.RemoveAll<ICurrentUser>();
        services.AddScoped<ICurrentUser, NoUser>();
        await using var provider = services.BuildServiceProvider();

        // Warm every path the test will race on (wiring, JIT, connection pool) before announcing readiness, so the parent measures
        // lock waits rather than start-up cost.
        await using (var warm = provider.CreateAsyncScope())
        {
            _ = await warm.ServiceProvider.GetRequiredService<AppDbContext>().Works.CountAsync();
            _ = await warm.ServiceProvider.GetRequiredService<CatalogWorkResolver>().GetWorkAsync(Guid.NewGuid(), CancellationToken.None);
            _ = warm.ServiceProvider.GetRequiredService<OrphanedWorkRetirement>();
        }

        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var outputGate = new object();
        void Emit(object value)
        {
            var text = JsonSerializer.Serialize(value, json);
            lock (outputGate)
            {
                Console.Out.WriteLine(text);
                Console.Out.Flush();
            }
        }

        Emit(new { @event = "ready", pid = Environment.ProcessId });

        var running = new List<Task>();
        string? line;
        while ((line = await Console.In.ReadLineAsync()) is not null)
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            var op = root.GetProperty("op").GetString();
            if (op == "exit")
            {
                break;
            }

            var id = root.GetProperty("id").GetInt32();
            if (op == "resolve")
            {
                var providerId = root.GetProperty("provider").GetString()!;
                var externalId = root.GetProperty("externalId").GetString()!;
                var candidate = root.GetProperty("candidate").Deserialize<BookCandidate>(json)!;
                candidates[(providerId, externalId)] = candidate;
                running.Add(Task.Run(async () =>
                {
                    Emit(new { @event = "started", id });
                    await using var scope = provider.CreateAsyncScope();
                    try
                    {
                        var result = await scope.ServiceProvider.GetRequiredService<CatalogWorkResolver>()
                            .ResolveAsync(providerId, externalId, CancellationToken.None);
                        Emit(new
                        {
                            @event = "result", id, ok = true, workId = result.Work.Id, wasCreated = result.WasCreated,
                            outcome = result.Outcome.ToString(), providerCalls = Volatile.Read(ref providerCalls)
                        });
                    }
                    catch (CatalogResolutionException exception)
                    {
                        Emit(new { @event = "result", id, ok = false, fault = exception.Fault.ToString(), providerCalls = Volatile.Read(ref providerCalls) });
                    }
                    catch (Exception exception)
                    {
                        Emit(new { @event = "result", id, ok = false, error = exception.GetType().FullName });
                    }
                }));
            }
            else if (op == "retire")
            {
                running.Add(Task.Run(async () =>
                {
                    Emit(new { @event = "started", id });
                    await using var scope = provider.CreateAsyncScope();
                    try
                    {
                        var retired = await scope.ServiceProvider.GetRequiredService<OrphanedWorkRetirement>()
                            .RetireAsync(DateTimeOffset.UtcNow, CancellationToken.None);
                        Emit(new { @event = "result", id, ok = true, retired });
                    }
                    catch (Exception exception)
                    {
                        Emit(new { @event = "result", id, ok = false, error = exception.GetType().FullName });
                    }
                }));
            }
        }

        await Task.WhenAll(running);
        return 0;
    }
}

internal sealed class EchoProvider(
    string id, ConcurrentDictionary<(string Provider, string ExternalId), BookCandidate> candidates, Func<int> countCall) : IBookMetadataProvider
{
    public string Id => id;

    public string DisplayName => $"Echo {id}";

    public Task<BookCandidateSearchPage> SearchAsync(BookSearchQuery query, CancellationToken cancellationToken) =>
        Task.FromResult(new BookCandidateSearchPage([], false));

    public Task<BookCandidate?> GetDetailsAsync(string externalId, CancellationToken cancellationToken)
    {
        countCall();
        return Task.FromResult(candidates.TryGetValue((id, externalId), out var candidate) ? candidate : null);
    }
}

internal sealed class NoUser : ICurrentUser
{
    public Guid? UserId => null;

    public string? DisplayName => null;
}
