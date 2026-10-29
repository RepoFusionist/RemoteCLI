using System.Text;
using Common.Security;
using Microsoft.Extensions.Configuration;
using RemoteCli.Vps;

namespace RemoteCli.UnitTests;

public sealed class RelayAdminStoreTests
{
    [Fact]
    public void FirstRunRequiresServerGeneratedBootstrapKeyAndPersistsSafeDefaults()
    {
        var directory = Path.Combine(Path.GetTempPath(), "remotecli-admin-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Relay:Admin:DataPath"] = directory
                })
                .Build();
            var store = new RelayAdminStore(configuration);
            var bootstrap = File.ReadAllText(Path.Combine(directory, "bootstrap.key"), Encoding.UTF8);

            Assert.False(store.IsInitialized);
            Assert.True(RelayServerIdentityClient.IsSha256(store.ServerFingerprintSha256));
            Assert.False(store.TryInitialize("wrong", "root", "correct horse battery", out var wrongError));
            Assert.Equal("invalid_bootstrap_key", wrongError);
            Assert.True(store.TryInitialize(bootstrap, "root", "correct horse battery", out var error), error);
            Assert.True(store.IsInitialized);
            Assert.False(File.Exists(Path.Combine(directory, "bootstrap.key")));
            Assert.False(store.AllowUnofficialClients);
            Assert.True(store.TryLogin("root", "correct horse battery", out var token, out error), error);
            Assert.True(store.IsAuthenticated(token));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void AdminConfigCanDisableCredentialModesAndClampPolicyInputs()
    {
        var directory = Path.Combine(Path.GetTempPath(), "remotecli-admin-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Relay:Admin:DataPath"] = directory })
                .Build();
            var store = new RelayAdminStore(configuration);
            var bootstrap = File.ReadAllText(Path.Combine(directory, "bootstrap.key"), Encoding.UTF8);
            Assert.True(store.TryInitialize(bootstrap, "root", "correct horse battery", out var error), error);
            Assert.True(store.TryUpdateConfig(new AdminConfigUpdate(false, false, true, 5, 100), out error), error);
            Assert.False(store.AllowFixedPasswords);
            Assert.True(store.AllowRandomPasswords);
            Assert.Equal(5, store.MaxAuthorizationMinutes);
            Assert.Equal(100, store.RateLimitPerMinute);
            Assert.False(store.TryUpdateConfig(new AdminConfigUpdate(false, true, true, 11, 100), out error));
            Assert.Equal("invalid_limits", error);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
