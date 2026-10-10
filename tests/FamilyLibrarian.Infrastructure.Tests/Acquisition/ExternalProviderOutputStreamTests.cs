using System.Text.Json;
using FamilyLibrarian.Application.Acquisition;
using FamilyLibrarian.Application.Providers;
using FamilyLibrarian.Domain.Acquisition;

namespace FamilyLibrarian.Infrastructure.Tests.Acquisition;

[TestClass]
public sealed class ExternalProviderOutputStreamTests
{
    private static readonly ExternalProviderOutputPolicy Policy = new()
    {
        MaxFileBytes = 8,
        MaxJobBytes = 10,
        ReadInactivityTimeoutSeconds = 1
    };

    [TestMethod]
    public async Task VerifiesDeclaredSizeAndSupportedChecksumAfterReadingAllBytes()
    {
        var bytes = "book"u8.ToArray();
        const string checksum = "821f03288846297c2cf43c34766a38f7";
        var output = Describe(bytes.Length, JsonSerializer.Serialize(new[] { new { algorithm = "md5", value = checksum } }));
        await using var stream = new ExternalProviderOutputStream(
            new MemoryStream(bytes), output, new ExternalProviderOutputTransferBudget(10), Policy);

        await stream.CopyToAsync(Stream.Null);
        stream.ValidateComplete(output);
    }

    [TestMethod]
    public async Task RejectsActualBytesAboveFileLimitEvenWhenDescriptorOmitsSize()
    {
        var output = Describe(null, null);
        await using var stream = new ExternalProviderOutputStream(
            new MemoryStream("too large!"u8.ToArray()), output, new ExternalProviderOutputTransferBudget(10), Policy);

        await Assert.ThrowsExactlyAsync<InvalidExternalProviderOutputException>(async () =>
            await stream.CopyToAsync(Stream.Null));
    }

    [TestMethod]
    public async Task RejectsChecksumMismatch()
    {
        var output = Describe(4, JsonSerializer.Serialize(new[] { new { algorithm = "sha256", value = new string('0', 64) } }));
        await using var stream = new ExternalProviderOutputStream(
            new MemoryStream("book"u8.ToArray()), output, new ExternalProviderOutputTransferBudget(10), Policy);

        await stream.CopyToAsync(Stream.Null);
        Assert.ThrowsExactly<InvalidExternalProviderOutputException>(() => stream.ValidateComplete(output));
    }

    [TestMethod]
    public async Task EnforcesAggregateBudgetAcrossSequentialOutputStreams()
    {
        var budget = new ExternalProviderOutputTransferBudget(6);
        var first = Describe(null, null);
        await using (var stream = new ExternalProviderOutputStream(new MemoryStream("four"u8.ToArray()), first, budget, Policy))
            await stream.CopyToAsync(Stream.Null);
        var second = Describe(null, null);
        await using var next = new ExternalProviderOutputStream(new MemoryStream("four"u8.ToArray()), second, budget, Policy);

        await Assert.ThrowsExactlyAsync<InvalidExternalProviderOutputException>(async () =>
            await next.CopyToAsync(Stream.Null));
    }

    private static ExternalProviderOutput Describe(long? size, string? checksums) =>
        new("output", ProviderOutputKind.File, "primary", "book.epub", "application/epub+zip",
            size, null, null, checksums, null);
}
