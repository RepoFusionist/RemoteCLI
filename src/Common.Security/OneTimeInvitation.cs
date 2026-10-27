using System.Security.Cryptography;
using System.Text;
using Common.Protocol;

namespace Common.Security;

public sealed record OneTimeInvitation(
    Guid SessionId,
    string RemoteIdentity,
    DateTimeOffset ExpiresAt,
    RemotePermission RequestedPermissions,
    string OneTimeSecret);

public static class OneTimeInvitationFactory
{
    public static OneTimeInvitation Create(
        string remoteIdentity,
        RemotePermission requestedPermissions,
        TimeSpan? lifetime = null,
        DateTimeOffset? now = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(remoteIdentity);
        ValidatePermissions(requestedPermissions);

        var validFor = lifetime ?? TimeSpan.FromMinutes(10);
        if (validFor <= TimeSpan.Zero || validFor > TimeSpan.FromHours(1))
        {
            throw new ArgumentOutOfRangeException(nameof(lifetime), "Invitation lifetime must be between 1 second and 1 hour.");
        }

        var secret = RandomNumberGenerator.GetBytes(32);
        try
        {
            return new OneTimeInvitation(
                Guid.NewGuid(),
                remoteIdentity.Trim(),
                (now ?? DateTimeOffset.UtcNow).Add(validFor),
                requestedPermissions,
                Convert.ToBase64String(secret).TrimEnd('=').Replace('+', '-').Replace('/', '_'));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    public static RemotePermission RestrictPermissions(
        RemotePermission requested,
        RemotePermission approved)
    {
        ValidatePermissions(requested);
        ValidatePermissions(approved);
        return requested & approved;
    }

    public static byte[] CreateApprovalProof(
        string oneTimeSecret,
        Guid sessionId,
        string localIdentity,
        RemotePermission approvedPermissions,
        ReadOnlySpan<byte> challenge)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(oneTimeSecret);
        ArgumentException.ThrowIfNullOrWhiteSpace(localIdentity);
        if (sessionId == Guid.Empty || challenge.Length is < 16 or > 64)
        {
            throw new ArgumentException("Invalid session or authentication challenge.");
        }

        ValidatePermissions(approvedPermissions);
        var key = DecodeSecret(oneTimeSecret);
        var message = Encoding.UTF8.GetBytes(
            $"RemoteCli/v1/pair|{sessionId:D}|{localIdentity.Trim()}|{(int)approvedPermissions}|{Convert.ToBase64String(challenge)}");
        try
        {
            return HMACSHA256.HashData(key, message);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(message);
        }
    }

    public static bool VerifyApprovalProof(
        string oneTimeSecret,
        Guid sessionId,
        string localIdentity,
        RemotePermission approvedPermissions,
        ReadOnlySpan<byte> challenge,
        ReadOnlySpan<byte> suppliedProof)
    {
        if (suppliedProof.Length != 32)
        {
            return false;
        }

        var expected = CreateApprovalProof(oneTimeSecret, sessionId, localIdentity, approvedPermissions, challenge);
        try
        {
            return CryptographicOperations.FixedTimeEquals(expected, suppliedProof);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(expected);
        }
    }

    public static bool VerifySecret(string oneTimeSecret, ReadOnlySpan<byte> expectedDigest)
    {
        byte[] secret;
        try
        {
            secret = DecodeSecret(oneTimeSecret);
        }
        catch (FormatException)
        {
            return false;
        }

        try
        {
            var digest = SHA256.HashData(secret);
            try
            {
                return CryptographicOperations.FixedTimeEquals(digest, expectedDigest);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(digest);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    public static byte[] HashSecret(string oneTimeSecret)
    {
        var secret = DecodeSecret(oneTimeSecret);
        try
        {
            return SHA256.HashData(secret);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    private static byte[] DecodeSecret(string value)
    {
        var base64 = value.Replace('-', '+').Replace('_', '/');
        base64 = base64.PadRight(base64.Length + ((4 - base64.Length % 4) % 4), '=');
        var decoded = Convert.FromBase64String(base64);
        if (decoded.Length != 32)
        {
            CryptographicOperations.ZeroMemory(decoded);
            throw new FormatException("Invitation secrets must contain 256 bits.");
        }

        return decoded;
    }

    private static void ValidatePermissions(RemotePermission permissions)
    {
        const RemotePermission all = RemotePermission.SessionRead | RemotePermission.CommandExecute | RemotePermission.CommandCancel;
        if ((permissions & ~all) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(permissions), "Unknown remote permission bit.");
        }
    }
}

public sealed class InvitationAttemptGate
{
    private const int MaximumFailedAttempts = 5;
    private readonly byte[] _secretDigest;
    private readonly DateTimeOffset _expiresAt;
    private int _failedAttempts;
    private int _consumed;

    public InvitationAttemptGate(string oneTimeSecret, DateTimeOffset expiresAt)
    {
        _secretDigest = OneTimeInvitationFactory.HashSecret(oneTimeSecret);
        _expiresAt = expiresAt;
    }

    public bool IsConsumed => Volatile.Read(ref _consumed) != 0;
    public int FailedAttempts => Volatile.Read(ref _failedAttempts);

    public bool TryConsume(string suppliedSecret, DateTimeOffset now)
    {
        if (now >= _expiresAt || IsConsumed || FailedAttempts >= MaximumFailedAttempts)
        {
            return false;
        }

        if (!OneTimeInvitationFactory.VerifySecret(suppliedSecret, _secretDigest))
        {
            Interlocked.Increment(ref _failedAttempts);
            return false;
        }

        return Interlocked.CompareExchange(ref _consumed, 1, 0) == 0;
    }
}
