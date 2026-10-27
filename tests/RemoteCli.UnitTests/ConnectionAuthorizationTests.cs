using Common.Protocol;
using Common.Security;
using RemoteCli.Vps;

namespace RemoteCli.UnitTests;

public sealed class ConnectionAuthorizationTests
{
    [Fact]
    public void FixedPasswordDerivationIsNormalizedDeterministicAndUserBound()
    {
        var first = ConnectionAuthorizationCredential.DeriveFixedSecret("  Test.User  ", "correct horse battery staple");
        var repeated = ConnectionAuthorizationCredential.DeriveFixedSecret("test.user", "correct horse battery staple");
        var otherUser = ConnectionAuthorizationCredential.DeriveFixedSecret("other.user", "correct horse battery staple");

        Assert.Equal("test.user", ConnectionAuthorizationCredential.NormalizeUserName("  Test.User  "));
        Assert.Equal(first, repeated);
        Assert.NotEqual(first, otherUser);
        Assert.Equal(43, first.Length);
        Assert.Throws<ArgumentException>(() =>
            ConnectionAuthorizationCredential.DeriveFixedSecret("test.user", "too-short"));
    }

    [Fact]
    public void RandomCredentialSupportsEveryAdvertisedRefreshInterval()
    {
        var allowed = new[]
        {
            TimeSpan.Zero,
            TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(30),
            TimeSpan.FromHours(1), TimeSpan.FromHours(6), TimeSpan.FromHours(12),
            TimeSpan.FromDays(1), TimeSpan.FromDays(3), TimeSpan.FromDays(7), TimeSpan.FromDays(30)
        };

        Assert.All(allowed, interval => Assert.True(ConnectionAuthorizationCredential.IsSupportedRotation(interval)));
        Assert.False(ConnectionAuthorizationCredential.IsSupportedRotation(TimeSpan.FromMinutes(15)));
        Assert.Equal(43, ConnectionAuthorizationCredential.CreateRandomSecret().Length);
    }

    [Fact]
    public void FixedConnectionPasswordPreservesExactTextAndDoesNotRequireBase64()
    {
        const string password = "  固定密码 !@#$%^&*()  ";
        var secret = ConnectionAuthorizationCredential.ResolveConnectionSecret(
            "test.user", password, ConnectionCredentialMode.FixedPassword);

        Assert.Equal(ConnectionAuthorizationCredential.DeriveFixedSecret("test.user", password), secret);
        Assert.NotEqual(ConnectionAuthorizationCredential.DeriveFixedSecret("test.user", password.Trim()), secret);
        Assert.Equal(32, OneTimeInvitationFactory.HashSecret(secret).Length);
    }

    [Theory]
    [InlineData("")]
    [InlineData("self-chosen-password!")]
    [InlineData("AAAAAAAAAAAAAAAAAAAA!AAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA==")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAA AAAAAAAAAAAAAAAAAAAAA")]
    public void InvalidRandomConnectionPasswordReturnsActionableErrorWithoutEchoingInput(string password)
    {
        var exception = Assert.Throws<ConnectionCredentialException>(() =>
            ConnectionAuthorizationCredential.ResolveConnectionSecret(
                "test.user", password, ConnectionCredentialMode.RotatingRandomPassword));

        Assert.Equal("invalid_random_password", exception.Code);
        Assert.Contains("43", exception.Message);
        Assert.Contains("固定密码", exception.Message);
        Assert.DoesNotContain("Base-64", exception.Message);
        Assert.Null(exception.InnerException);
        if (password.Length > 0)
        {
            Assert.DoesNotContain(password, exception.Message);
        }
    }

    [Fact]
    public void RandomConnectionPasswordAcceptsCopiedWhitespaceAndBothBase64Alphabets()
    {
        var bytes = Enumerable.Repeat((byte)255, 32).ToArray();
        var standard = Convert.ToBase64String(bytes);
        var urlSafe = standard.TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var expectedDigest = System.Security.Cryptography.SHA256.HashData(bytes);

        foreach (var value in new[] { standard, urlSafe, " \r\n" + urlSafe + "\t " })
        {
            var secret = ConnectionAuthorizationCredential.ResolveConnectionSecret(
                "test.user", value, ConnectionCredentialMode.RotatingRandomPassword);
            Assert.Equal(expectedDigest, OneTimeInvitationFactory.HashSecret(secret));
        }
    }

    [Fact]
    public void InvalidFixedConnectionPasswordIsClassifiedSeparately()
    {
        var exception = Assert.Throws<ConnectionCredentialException>(() =>
            ConnectionAuthorizationCredential.ResolveConnectionSecret(
                "test.user", "too-short", ConnectionCredentialMode.FixedPassword));

        Assert.Equal("invalid_fixed_password", exception.Code);
        Assert.DoesNotContain("too-short", exception.Message);
    }

    [Fact]
    public void UnknownCredentialModeCannotFallBackToRandomPassword()
    {
        Assert.Throws<InvalidDataException>(() =>
            ConnectionAuthorizationCredential.ResolveConnectionSecret(
                "test.user", ConnectionAuthorizationCredential.CreateRandomSecret(), (ConnectionCredentialMode)99));
    }

    [Fact]
    public async Task RelayAuthorizationRequiresOwnerForReplacementAndRevocation()
    {
        await using var relay = new RelayRoomService();
        var ownerToken = ConnectionAuthorizationCredential.CreateOwnerToken();
        var first = CreateRegistration("test.user", ownerToken);
        Assert.True(relay.TryRegisterAuthorization(first, out var registerError), registerError);
        Assert.True(relay.TryGetAuthorization("TEST.USER", out var registered));
        Assert.Equal(first.Invitation.SessionId, registered!.SessionId);

        var wrongOwner = CreateRegistration("test.user", ConnectionAuthorizationCredential.CreateOwnerToken());
        Assert.False(relay.TryRegisterAuthorization(wrongOwner, out var ownershipError));
        Assert.Equal("username_in_use", ownershipError);

        var replacement = CreateRegistration("test.user", ownerToken);
        Assert.True(relay.TryRegisterAuthorization(replacement, out var replacementError), replacementError);
        Assert.False(relay.TryGetInvitation(first.Invitation.SessionId, out _));
        Assert.True(relay.TryGetAuthorization("test.user", out var current));
        Assert.Equal(replacement.Invitation.SessionId, current!.SessionId);

        Assert.False(relay.TryRevokeAuthorization(
            "test.user",
            new ConnectionAuthorizationRevocation(ConnectionAuthorizationCredential.CreateOwnerToken()),
            out var revokeOwnershipError));
        Assert.Equal("authorization_not_found", revokeOwnershipError);

        Assert.True(relay.TryRevokeAuthorization(
            "test.user",
            new ConnectionAuthorizationRevocation(ownerToken),
            out var revokeError), revokeError);
        Assert.False(relay.TryGetAuthorization("test.user", out _));
        Assert.False(relay.TryGetInvitation(replacement.Invitation.SessionId, out _));
    }

    private static ConnectionAuthorizationRegistration CreateRegistration(string userName, string ownerToken)
    {
        var now = DateTimeOffset.UtcNow;
        var invitation = new InvitationRequest(
            ProtocolConstants.CurrentVersion,
            Guid.NewGuid(),
            "remote-host",
            now.AddMinutes(5),
            RemotePermission.SessionRead | RemotePermission.CommandExecute,
            ConnectionAuthorizationCredential.NormalizeUserName(userName),
            ConnectionCredentialMode.RotatingRandomPassword,
            now.AddHours(1));
        return new ConnectionAuthorizationRegistration(userName, ownerToken, invitation);
    }
}
