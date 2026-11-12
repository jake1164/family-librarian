using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using FamilyLibrarian.Application.ModelTesting;

namespace FamilyLibrarian.Application.ModelTuning;

/// <summary>
/// V1: domain plus NUL, UInt32BE message count, then UInt32BE UTF-8 byte length and bytes for each role/content,
/// JSON-mode byte, cap presence byte and optional positive UInt32BE cap. No text normalization or BOM.
/// Snapshot and production reuse digests have different purposes. This helper never retains or logs input text.
/// </summary>
public static class RenderedMessagesIdentity
{
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static string Compute(IReadOnlyList<ModelChatMessage> messages, bool jsonObjectOutput, int? maxOutputTokens)
    {
        ArgumentNullException.ThrowIfNull(messages);
        if (maxOutputTokens is <= 0) throw new ArgumentOutOfRangeException(nameof(maxOutputTokens));
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Utf8.GetBytes("family-librarian/rendered-messages/1\0"));
        Number((uint)messages.Count);
        foreach (var message in messages)
        {
            ArgumentNullException.ThrowIfNull(message);
            if (message.Images is { Count: > 0 }) throw new ArgumentException("V1 supports text messages only.", nameof(messages));
            Text(message.Role);
            Text(message.Content);
        }
        hash.AppendData([jsonObjectOutput ? (byte)1 : (byte)0]);
        hash.AppendData([maxOutputTokens.HasValue ? (byte)1 : (byte)0]);
        if (maxOutputTokens.HasValue) Number((uint)maxOutputTokens.Value);
        return "rendered-messages/1:" + Convert.ToHexStringLower(hash.GetHashAndReset());

        void Number(uint value)
        {
            Span<byte> bytes = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
            hash.AppendData(bytes);
        }
        void Text(string value)
        {
            ArgumentNullException.ThrowIfNull(value);
            Number((uint)Utf8.GetByteCount(value));
            hash.AppendData(Utf8.GetBytes(value));
        }
    }
}
