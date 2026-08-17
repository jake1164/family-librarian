using FamilyLibrarian.Infrastructure.Integrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FamilyLibrarian.Infrastructure.Tests.Integrations;

[TestClass]
public sealed class DataProtectionSetupTests
{
    [TestMethod]
    public void WarnsWhenNoKeyEncryptionCertificateIsConfigured()
    {
        var provider = BuildServiceProvider([]);

        provider.WarnIfKeyRingIsUnprotected();

        Assert.HasCount(1, RecordingLoggerProvider.Warnings);
        StringAssert.Contains(RecordingLoggerProvider.Warnings.Single(), "unencrypted");
    }

    [TestMethod]
    public void DoesNotWarnWhenAKeyEncryptionCertificatePathIsConfigured()
    {
        var provider = BuildServiceProvider(
            [new KeyValuePair<string, string?>("DataProtection:KeyEncryptionCertificate:Path", "/run/secrets/dataprotection.pfx")]);

        provider.WarnIfKeyRingIsUnprotected();

        Assert.HasCount(0, RecordingLoggerProvider.Warnings);
    }

    private static ServiceProvider BuildServiceProvider(IEnumerable<KeyValuePair<string, string?>> configurationValues)
    {
        RecordingLoggerProvider.Warnings.Clear();

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(configurationValues).Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging(builder => builder.AddProvider(new RecordingLoggerProvider()));

        return services.BuildServiceProvider();
    }

    /// <summary>Captures Warning-level messages so a test can assert on them without a real sink.</summary>
    private sealed class RecordingLoggerProvider : ILoggerProvider
    {
        public static List<string> Warnings { get; } = [];

        public ILogger CreateLogger(string categoryName) => new RecordingLogger();

        public void Dispose()
        {
        }

        private sealed class RecordingLogger : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (logLevel == LogLevel.Warning)
                {
                    Warnings.Add(formatter(state, exception));
                }
            }
        }
    }
}
