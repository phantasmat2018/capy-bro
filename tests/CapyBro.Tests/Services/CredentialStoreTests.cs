using System.IO;

using CapyBro.Services;

using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace CapyBro.Tests.Services;

public class CredentialStoreTests
{
    private const string Target = "TestTarget";
    private const string Username = "TestUser";

    private static CredentialStore CreateSut(FakeCredentialBackend backend) =>
        new(backend, Target, Username, NullLogger<CredentialStore>.Instance);

    [Fact]
    public async Task GetApiKey_NoCredential_ReturnsNullAsync()
    {
        var backend = new FakeCredentialBackend();
        var sut = CreateSut(backend);

        var result = await sut.GetApiKeyAsync();

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetApiKey_PreviouslyStored_ReturnsValueAsync()
    {
        var backend = new FakeCredentialBackend();
        backend.Stored[Target] = ("user", "secret-value");
        var sut = CreateSut(backend);

        var result = await sut.GetApiKeyAsync();

        result.Should().Be("secret-value");
    }

    [Fact]
    public async Task GetApiKey_BackendThrowsIOException_ReturnsNullAsync()
    {
        var backend = new FakeCredentialBackend { ThrowOnRead = new IOException("disk failure") };
        var sut = CreateSut(backend);

        var result = await sut.GetApiKeyAsync();

        result.Should().BeNull();
    }

    [Fact]
    public async Task SetApiKey_HappyPath_WritesViaBackendAsync()
    {
        var backend = new FakeCredentialBackend();
        var sut = CreateSut(backend);

        await sut.SetApiKeyAsync("new-secret");

        backend.Stored.Should().ContainKey(Target);
        backend.Stored[Target].Should().Be((Username, "new-secret"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task SetApiKey_NullOrEmpty_ThrowsAsync(string? apiKey)
    {
        var backend = new FakeCredentialBackend();
        var sut = CreateSut(backend);

        var act = async () => await sut.SetApiKeyAsync(apiKey!);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task DeleteApiKey_HappyPath_RemovesFromBackendAsync()
    {
        var backend = new FakeCredentialBackend();
        backend.Stored[Target] = (Username, "to-delete");
        var sut = CreateSut(backend);

        await sut.DeleteApiKeyAsync();

        backend.Stored.Should().NotContainKey(Target);
    }

    [Fact]
    public async Task DeleteApiKey_BackendThrows_DoesNotPropagateAsync()
    {
        var backend = new FakeCredentialBackend { ThrowOnDelete = new IOException("locked") };
        var sut = CreateSut(backend);

        var act = async () => await sut.DeleteApiKeyAsync();

        await act.Should().NotThrowAsync();
    }

    private sealed class FakeCredentialBackend : ICredentialBackend
    {
        public Dictionary<string, (string Username, string Secret)> Stored { get; } = new(StringComparer.Ordinal);

        public Exception? ThrowOnRead { get; set; }

        public Exception? ThrowOnWrite { get; set; }

        public Exception? ThrowOnDelete { get; set; }

        public string? Read(string target)
        {
            if (ThrowOnRead is not null)
            {
                throw ThrowOnRead;
            }

            return Stored.TryGetValue(target, out var entry) ? entry.Secret : null;
        }

        public void Write(string target, string username, string secret)
        {
            if (ThrowOnWrite is not null)
            {
                throw ThrowOnWrite;
            }

            Stored[target] = (username, secret);
        }

        public void Delete(string target)
        {
            if (ThrowOnDelete is not null)
            {
                throw ThrowOnDelete;
            }

            Stored.Remove(target);
        }
    }
}
