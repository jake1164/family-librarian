using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;

namespace FamilyLibrarian.Web.Tests.Harness;

/// <summary>
/// Rewrites a settings archive the way an earlier version would have written it. It follows the archive's own header,
/// so it fails if the format changes rather than quietly testing something else.
/// </summary>
internal static class LegacyArchive
{
    public static byte[] WithoutSetupGuidance(byte[] archive, string passphrase)
    {
        using var outer = JsonDocument.Parse(archive);
        var header = outer.RootElement.GetProperty("header");
        var associatedData = Encoding.UTF8.GetBytes(header.GetRawText());
        var nonce = Convert.FromBase64String(header.GetProperty("nonce").GetString()!);
        var key = new byte[32];
        var generator = new Argon2BytesGenerator();
        generator.Init(new Argon2Parameters.Builder(Argon2Parameters.Argon2id)
            .WithVersion(Argon2Parameters.Version13)
            .WithSalt(Convert.FromBase64String(header.GetProperty("salt").GetString()!))
            .WithMemoryAsKB(header.GetProperty("memoryKiB").GetInt32())
            .WithIterations(header.GetProperty("iterations").GetInt32())
            .WithParallelism(header.GetProperty("parallelism").GetInt32())
            .Build());
        generator.GenerateBytes(Encoding.UTF8.GetBytes(passphrase), key);

        var ciphertext = Convert.FromBase64String(outer.RootElement.GetProperty("ciphertext").GetString()!);
        var plaintext = new byte[ciphertext.Length];
        using var cipher = new AesGcm(key, 16);
        cipher.Decrypt(nonce, ciphertext,
            Convert.FromBase64String(outer.RootElement.GetProperty("tag").GetString()!), plaintext, associatedData);

        var payload = JsonNode.Parse(plaintext)!;
        Assert.IsTrue(payload["document"]!.AsObject().Remove("setupGuidance"), "The section to remove was not there.");
        var rewritten = Encoding.UTF8.GetBytes(payload.ToJsonString());
        var sealedText = new byte[rewritten.Length];
        var tag = new byte[16];
        // The same nonce is reused only because this archive replaces the original and never leaves the test.
        cipher.Encrypt(nonce, rewritten, sealedText, tag, associatedData);

        return Encoding.UTF8.GetBytes(
            $$"""{"header":{{header.GetRawText()}},"ciphertext":"{{Convert.ToBase64String(sealedText)}}","tag":"{{Convert.ToBase64String(tag)}}"}""");
    }
}
