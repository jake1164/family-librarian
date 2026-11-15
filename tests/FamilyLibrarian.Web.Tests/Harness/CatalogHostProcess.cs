using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using FamilyLibrarian.Application.Catalog;
using Npgsql;

namespace FamilyLibrarian.Web.Tests.Harness;

/// <summary>
/// A second OS process running the production catalog code (see the CatalogConcurrencyHost project) against a disposable test
/// database. Started as <c>dotnet &lt;helper.dll&gt;</c> from the test output folder, so it needs no machine-specific path; the
/// connection string travels in the child's environment, never on its command line or in a log.
/// </summary>
internal sealed class CatalogHostProcess : IAsyncDisposable
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    private readonly Process process;
    private readonly ConcurrentDictionary<int, Pending> pending = new();
    private readonly StringBuilder errors = new();
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int nextId;

    private CatalogHostProcess(Process process, string applicationName)
    {
        this.process = process;
        ApplicationName = applicationName;
    }

    /// <summary>The PostgreSQL application name every connection of this process carries, so a test can find its backends.</summary>
    public string ApplicationName { get; }

    public int ProcessId => process.Id;

    public string StandardError
    {
        get
        {
            lock (errors) return errors.ToString();
        }
    }

    internal sealed record Command(int Id, Task Started, Task<JsonElement> Result);

    private sealed record Pending(TaskCompletionSource Started, TaskCompletionSource<JsonElement> Result);

    internal static async Task<CatalogHostProcess> StartAsync(string connectionString, string nonce, TimeSpan readyTimeout)
    {
        var applicationName = $"fl-catalog-host-{nonce}";
        var dll = Path.Combine(AppContext.BaseDirectory, "FamilyLibrarian.CatalogConcurrencyHost.dll");
        if (!File.Exists(dll))
        {
            throw new FileNotFoundException("The catalog concurrency helper was not built next to the tests.", dll);
        }

        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = AppContext.BaseDirectory
        };
        start.ArgumentList.Add(dll);
        start.Environment["FL_CATALOG_TEST_CONNECTION"] = connectionString;
        start.Environment["FL_CATALOG_TEST_APPLICATION_NAME"] = applicationName;

        var process = Process.Start(start) ?? throw new InvalidOperationException("The catalog helper process did not start.");
        var host = new CatalogHostProcess(process, applicationName);
        _ = host.ReadOutputAsync();
        _ = host.ReadErrorsAsync();
        try
        {
            await host.ready.Task.WaitAsync(readyTimeout);
        }
        catch
        {
            await host.DisposeAsync();
            throw;
        }

        return host;
    }

    internal Command Resolve(string providerId, string externalId, BookCandidate candidate) =>
        Send(id => new Dictionary<string, object?>
        {
            ["id"] = id, ["op"] = "resolve", ["provider"] = providerId, ["externalId"] = externalId, ["candidate"] = candidate
        });

    internal Command Retire() => Send(id => new Dictionary<string, object?> { ["id"] = id, ["op"] = "retire" });

    private Command Send(Func<int, Dictionary<string, object?>> build)
    {
        var id = Interlocked.Increment(ref nextId);
        var entry = new Pending(
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously));
        pending[id] = entry;
        process.StandardInput.WriteLine(JsonSerializer.Serialize(build(id), WebJson));
        process.StandardInput.Flush();
        return new Command(id, entry.Started.Task, entry.Result.Task);
    }

    private async Task ReadOutputAsync()
    {
        try
        {
            string? line;
            while ((line = await process.StandardOutput.ReadLineAsync()) is not null)
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement.Clone();
                switch (root.GetProperty("event").GetString())
                {
                    case "ready":
                        ready.TrySetResult();
                        break;
                    case "started":
                        pending[root.GetProperty("id").GetInt32()].Started.TrySetResult();
                        break;
                    case "result":
                        pending[root.GetProperty("id").GetInt32()].Result.TrySetResult(root);
                        break;
                }
            }
        }
        finally
        {
            var ended = new InvalidOperationException($"The catalog helper process ended. {StandardError}");
            ready.TrySetException(ended);
            foreach (var entry in pending.Values)
            {
                entry.Started.TrySetException(ended);
                entry.Result.TrySetException(ended);
            }
        }
    }

    private async Task ReadErrorsAsync()
    {
        string? line;
        while ((line = await process.StandardError.ReadLineAsync()) is not null)
        {
            lock (errors) errors.AppendLine(line);
        }
    }

    public async ValueTask DisposeAsync()
    {
        // Only this helper's own process is ever stopped: ask it to exit, then end it, with deadlines.
        try
        {
            if (!process.HasExited)
            {
                await process.StandardInput.WriteLineAsync("""{"op":"exit"}""");
                await process.StandardInput.FlushAsync();
                using var graceful = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await process.WaitForExitAsync(graceful.Token);
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException or InvalidOperationException)
        {
            // Fall through to the forced stop below.
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            process.Dispose();
        }
    }
}

/// <summary>
/// The orchestrating test, not a child, holds the catalog advisory lock on its own connection and observes who waits for it, so
/// no participant ever holds the lock while waiting on another participant and a barrier deadlock is impossible.
/// </summary>
internal sealed class CatalogLockProbe(string connectionString) : IAsyncDisposable
{
    private const int Namespace = 831705;
    private const int Key = 0;

    private NpgsqlConnection? holder;

    /// <summary>Takes the catalog lock at session level on a dedicated connection (released by <see cref="ReleaseAsync"/> or disposal).</summary>
    public async Task HoldAsync(CancellationToken cancellationToken)
    {
        holder = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(connectionString)
        {
            ApplicationName = $"fl-catalog-probe-{Guid.NewGuid():N}", Pooling = false
        }.ConnectionString);
        await holder.OpenAsync(cancellationToken);
        await using var command = holder.CreateCommand();
        command.CommandText = $"SELECT pg_advisory_lock({Namespace}, {Key})";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task ReleaseAsync(CancellationToken cancellationToken)
    {
        if (holder is null)
        {
            return;
        }

        await using (var command = holder.CreateCommand())
        {
            command.CommandText = $"SELECT pg_advisory_unlock({Namespace}, {Key})";
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await holder.DisposeAsync();
        holder = null;
    }

    /// <summary>
    /// Polls <c>pg_locks</c> until a backend with this application name is waiting (not granted) for the catalog lock. A bounded
    /// database observation, not a sleep, is what establishes that the participant has reached the lock.
    /// </summary>
    public async Task WaitUntilWaitingAsync(string applicationName, TimeSpan timeout)
    {
        using var deadline = new CancellationTokenSource(timeout);
        await using var observer = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString);
        await observer.OpenAsync(deadline.Token);
        while (true)
        {
            if (await CountWaitingAsync(observer, applicationName, deadline.Token) > 0)
            {
                return;
            }

            try
            {
                await Task.Delay(40, deadline.Token);
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException($"No backend named '{applicationName}' began waiting for the catalog lock within {timeout}.");
            }
        }
    }

    /// <summary>How many backends of this application currently hold (granted) the catalog lock.</summary>
    public async Task<long> CountHoldingAsync(string applicationName, CancellationToken cancellationToken) =>
        await CountAsync(applicationName, granted: true, cancellationToken);

    private async Task<long> CountAsync(string applicationName, bool granted, CancellationToken cancellationToken)
    {
        await using var observer = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString);
        await observer.OpenAsync(cancellationToken);
        return await CountAsync(observer, applicationName, granted, cancellationToken);
    }

    private static Task<long> CountWaitingAsync(NpgsqlConnection observer, string applicationName, CancellationToken cancellationToken) =>
        CountAsync(observer, applicationName, granted: false, cancellationToken);

    private static async Task<long> CountAsync(NpgsqlConnection observer, string applicationName, bool granted, CancellationToken cancellationToken)
    {
        await using var command = observer.CreateCommand();
        command.CommandText =
            """
            SELECT count(*) FROM pg_locks l JOIN pg_stat_activity a ON a.pid = l.pid
            WHERE l.locktype = 'advisory' AND l.classid::bigint = @namespace AND l.objid::bigint = @key AND l.objsubid = 2
              AND l.granted = @granted AND a.application_name = @application AND a.datname = current_database()
            """;
        command.Parameters.AddWithValue("namespace", (long)Namespace);
        command.Parameters.AddWithValue("key", (long)Key);
        command.Parameters.AddWithValue("granted", granted);
        command.Parameters.AddWithValue("application", applicationName);
        return (long)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    public async ValueTask DisposeAsync()
    {
        if (holder is not null)
        {
            await holder.DisposeAsync();
            holder = null;
        }
    }
}
