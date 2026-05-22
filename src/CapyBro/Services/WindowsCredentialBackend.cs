using Meziantou.Framework.Win32;

namespace CapyBro.Services;

internal sealed class WindowsCredentialBackend : ICredentialBackend
{
    public string? Read(string target)
    {
        var credential = CredentialManager.ReadCredential(target);
        return credential?.Password;
    }

    public void Write(string target, string username, string secret)
    {
        CredentialManager.WriteCredential(
            applicationName: target,
            userName: username,
            secret: secret,
            persistence: CredentialPersistence.LocalMachine);
    }

    public void Delete(string target)
    {
        CredentialManager.DeleteCredential(target);
    }
}
