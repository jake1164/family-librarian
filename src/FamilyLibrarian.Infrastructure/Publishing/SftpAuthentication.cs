using FamilyLibrarian.Domain.Publishing;
using Renci.SshNet;

namespace FamilyLibrarian.Infrastructure.Publishing;

internal static class SftpAuthentication
{
    public static AuthenticationMethod Create(
        CwaSftpAuthenticationMode mode,
        string username,
        string credential,
        string? privateKeyPassphrase) =>
        mode switch
        {
            CwaSftpAuthenticationMode.Password => new PasswordAuthenticationMethod(username, credential),
            CwaSftpAuthenticationMode.PrivateKey => CreatePrivateKey(username, credential, privateKeyPassphrase),
            _ => throw new InvalidOperationException("The configured SFTP authentication mode is not supported.")
        };

    private static PrivateKeyAuthenticationMethod CreatePrivateKey(string username, string privateKey, string? passphrase)
    {
        var keyStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(privateKey));
        var keyFile = string.IsNullOrEmpty(passphrase)
            ? new PrivateKeyFile(keyStream)
            : new PrivateKeyFile(keyStream, passphrase);
        return new PrivateKeyAuthenticationMethod(username, keyFile);
    }
}
