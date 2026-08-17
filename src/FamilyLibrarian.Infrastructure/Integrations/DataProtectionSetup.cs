using System.Security.Cryptography.X509Certificates;
using FamilyLibrarian.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FamilyLibrarian.Infrastructure.Integrations;

public static class DataProtectionSetup
{
    /// <summary>
    /// Configures the key ring that protects auth cookies and stored provider
    /// credentials.
    /// </summary>
    /// <remarks>
    /// Keys persist to PostgreSQL rather than the container filesystem, because
    /// the default file provider writes to the container's own
    /// <c>~/.aspnet/DataProtection-Keys</c> and every
    /// <c>docker compose up --force-recreate</c> threw them away.
    /// <para>
    /// <c>SetApplicationName</c> pins the key discriminator, which otherwise
    /// derives from the content root path — <c>/app</c> in the container but an
    /// OS-specific absolute path under the debugger, so a cookie issued by one
    /// would fail to decrypt in the other.
    /// </para>
    /// <para>
    /// Persisting keys is not the same as protecting them. Without a key
    /// encryptor the key ring sits in the database in plaintext, so anyone with a
    /// database backup can unprotect every stored provider credential. Supplying
    /// a certificate closes that gap;
    /// <c>docs/03-provider-api-contracts.md</c> requires it for a real
    /// deployment.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddFamilyLibrarianDataProtection(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var builder = services.AddDataProtection()
            .PersistKeysToDbContext<AppDbContext>()
            .SetApplicationName("FamilyLibrarian");

        var certificate = LoadKeyEncryptionCertificate(configuration);
        if (certificate is not null)
        {
            builder.ProtectKeysWithCertificate(certificate);
        }

        return services;
    }

    /// <summary>
    /// Loads the key-encryption certificate named by
    /// <c>DataProtection:KeyEncryptionCertificate:*</c>, or returns <c>null</c>
    /// when the deployment has not configured one.
    /// </summary>
    private static X509Certificate2? LoadKeyEncryptionCertificate(IConfiguration configuration)
    {
        var section = configuration.GetSection("DataProtection:KeyEncryptionCertificate");
        var path = section["Path"];

        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        if (!File.Exists(path))
        {
            // Fail loudly. Silently falling back to an unencrypted key ring would
            // make a deployment believe its credentials are protected when they
            // are not.
            throw new InvalidOperationException(
                $"DataProtection:KeyEncryptionCertificate:Path points at '{path}', which does not exist.");
        }

        var password = section["Password"];

        return string.IsNullOrEmpty(password)
            ? X509CertificateLoader.LoadPkcs12FromFile(path, password: null)
            : X509CertificateLoader.LoadPkcs12FromFile(path, password);
    }

    /// <summary>
    /// Logs a warning, once at startup, if the key ring protecting every
    /// stored provider credential is itself unencrypted — see F4 in the
    /// architecture review.
    /// </summary>
    /// <remarks>
    /// Deliberately a warning, not the throw <see cref="LoadKeyEncryptionCertificate"/>
    /// uses for a <em>misconfigured</em> path: a fresh install legitimately has
    /// no certificate yet, and failing closed here would block the documented
    /// zero-config Compose flow.
    /// </remarks>
    public static void WarnIfKeyRingIsUnprotected(this IServiceProvider services)
    {
        var configuration = services.GetRequiredService<IConfiguration>();
        var path = configuration["DataProtection:KeyEncryptionCertificate:Path"];

        if (string.IsNullOrWhiteSpace(path))
        {
            var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("FamilyLibrarian.DataProtection");
            DataProtectionLog.KeyRingUnprotected(logger);
        }
    }
}

internal static partial class DataProtectionLog
{
    [LoggerMessage(
        EventId = 3001,
        Level = LogLevel.Warning,
        Message = "DataProtection:KeyEncryptionCertificate:Path is not set. The Data Protection key ring is " +
            "stored unencrypted in PostgreSQL, so a database backup contains everything needed to decrypt every " +
            "provider credential, SFTP key, and OIDC client secret it also contains. See " +
            "docs/06-deployment-and-recovery.md.")]
    internal static partial void KeyRingUnprotected(ILogger logger);
}
