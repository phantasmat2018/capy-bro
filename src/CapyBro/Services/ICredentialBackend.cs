namespace CapyBro.Services;

internal interface ICredentialBackend
{
    string? Read(string target);

    void Write(string target, string username, string secret);

    void Delete(string target);
}
