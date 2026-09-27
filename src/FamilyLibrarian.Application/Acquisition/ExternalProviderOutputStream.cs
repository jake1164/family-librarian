using System.Security.Cryptography;
using System.Text.Json;
using FamilyLibrarian.Application.Providers;

namespace FamilyLibrarian.Application.Acquisition;

/// <summary>Enforces actual-byte limits and independently checks supported provider hashes while staging.</summary>
public sealed class ExternalProviderOutputStream : Stream
{
    private readonly Stream _inner;
    private readonly IReadOnlyDictionary<string, string> _expectedChecksums;
    private readonly Dictionary<string, IncrementalHash> _hashes;
    private readonly ExternalProviderOutputTransferBudget _budget;
    private readonly long _maxFileBytes;
    private readonly TimeSpan _inactivityTimeout;
    private long _bytesRead;
    private bool _reachedEnd;
    private bool _disposed;

    public ExternalProviderOutputStream(
        Stream inner,
        ExternalProviderOutput output,
        ExternalProviderOutputTransferBudget budget,
        ExternalProviderOutputPolicy policy)
    {
        _inner = inner;
        _budget = budget;
        _maxFileBytes = policy.MaxFileBytes;
        _inactivityTimeout = TimeSpan.FromSeconds(policy.ReadInactivityTimeoutSeconds);
        _expectedChecksums = ParseExpectedChecksums(output.ChecksumsJson);
        _hashes = _expectedChecksums.Keys.ToDictionary(
            algorithm => algorithm,
            algorithm => IncrementalHash.CreateHash(algorithm switch
            {
                "sha256" => HashAlgorithmName.SHA256,
                "md5" => HashAlgorithmName.MD5,
                _ => throw new InvalidExternalProviderOutputException("Unsupported provider checksum algorithm.")
            }),
            StringComparer.OrdinalIgnoreCase);
    }

    public long BytesRead => _bytesRead;
    public override bool CanRead => !_disposed && _inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => _bytesRead; set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = _inner.Read(buffer, offset, count);
        ProcessRead(buffer.AsSpan(offset, read), read);
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_inactivityTimeout);
        int read;
        try
        {
            read = await _inner.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("The provider output stopped sending bytes for too long.");
        }

        ProcessRead(buffer.Span[..read], read);
        return read;
    }

    public void ValidateComplete(ExternalProviderOutput output)
    {
        if (!_reachedEnd)
            throw new InvalidExternalProviderOutputException("The provider output stream ended before its contents were verified.");
        if (output.SizeBytes is { } expectedSize && expectedSize != _bytesRead)
            throw new InvalidExternalProviderOutputException("The provider output size did not match the bytes received.");

        foreach (var (algorithm, expected) in _expectedChecksums)
        {
            var actual = Convert.ToHexStringLower(_hashes[algorithm].GetHashAndReset());
            if (!CryptographicOperations.FixedTimeEquals(
                    System.Text.Encoding.ASCII.GetBytes(actual),
                    System.Text.Encoding.ASCII.GetBytes(expected.ToLowerInvariant())))
                throw new InvalidExternalProviderOutputException("The provider output checksum did not match the bytes received.");
        }
    }

    private void ProcessRead(ReadOnlySpan<byte> buffer, int read)
    {
        if (read == 0)
        {
            _reachedEnd = true;
            return;
        }
        _bytesRead = checked(_bytesRead + read);
        if (_bytesRead > _maxFileBytes)
            throw new InvalidExternalProviderOutputException("The provider output exceeds the configured per-file limit.");
        _budget.Add(read);
        foreach (var hash in _hashes.Values)
            hash.AppendData(buffer);
    }

    private static Dictionary<string, string> ParseExpectedChecksums(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidExternalProviderOutputException("The provider checksum listing is malformed.");

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var checksum in document.RootElement.EnumerateArray())
        {
            if (!checksum.TryGetProperty("algorithm", out var algorithmElement) ||
                !checksum.TryGetProperty("value", out var valueElement) ||
                algorithmElement.ValueKind != JsonValueKind.String || valueElement.ValueKind != JsonValueKind.String)
                continue;

            var algorithm = algorithmElement.GetString()?.Trim().ToLowerInvariant();
            var value = valueElement.GetString()?.Trim();
            if (algorithm is "sha256" or "md5")
            {
                var requiredLength = algorithm == "sha256" ? 64 : 32;
                if (value is null || value.Length != requiredLength || !value.All(Uri.IsHexDigit))
                    throw new InvalidExternalProviderOutputException("The provider supplied a malformed checksum.");
                result[algorithm] = value;
            }
        }
        return result;
    }

    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            _inner.Dispose();
            foreach (var hash in _hashes.Values)
                hash.Dispose();
        }
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            await _inner.DisposeAsync().ConfigureAwait(false);
            foreach (var hash in _hashes.Values)
                hash.Dispose();
        }
        await base.DisposeAsync().ConfigureAwait(false);
    }
}

public sealed class ExternalProviderOutputTransferBudget(long maxBytes)
{
    private long _bytesRead;

    public long BytesRead => Interlocked.Read(ref _bytesRead);

    public void Add(int bytes)
    {
        var total = Interlocked.Add(ref _bytesRead, bytes);
        if (total > maxBytes)
            throw new InvalidExternalProviderOutputException("The provider output set exceeds the configured aggregate limit.");
    }
}
