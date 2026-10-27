using System.Security.Cryptography;
using System.Text;
using Common.Protocol;

namespace Common.Security;

public static class ConnectionAuthorizationCredential
{
    private const int FixedPasswordIterations = 310_000;

    public static string NormalizeUserName(string userName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userName);
        var normalized = userName.Trim().Normalize(NormalizationForm.FormKC).ToLowerInvariant();
        if (normalized.Length is < 3 or > 64 ||
            normalized.Any(character => !(char.IsLetterOrDigit(character) || character is '.' or '_' or '-')))
        {
            throw new ArgumentException("用户名必须为 3–64 个字母、数字、点、下划线或连字符。", nameof(userName));
        }

        return normalized;
    }

    public static string DeriveFixedSecret(string userName, string password)
    {
        var normalized = NormalizeUserName(userName);
        if (string.IsNullOrWhiteSpace(password) || password.Length is < 12 or > 256 ||
            password.Any(character => character == '\0'))
        {
            throw new ArgumentException("固定密码必须为 12–256 个字符。", nameof(password));
        }

        var passwordBytes = Encoding.UTF8.GetBytes(password);
        var salt = SHA256.HashData(Encoding.UTF8.GetBytes("LongShell/v2/fixed-password|" + normalized));
        try
        {
            var key = Rfc2898DeriveBytes.Pbkdf2(
                passwordBytes,
                salt,
                FixedPasswordIterations,
                HashAlgorithmName.SHA256,
                32);
            try
            {
                return EncodeSecret(key);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
            CryptographicOperations.ZeroMemory(salt);
        }
    }

    public static string CreateRandomSecret()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        try
        {
            return EncodeSecret(bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public static string CreateOwnerToken() => CreateRandomSecret();

    public static string ResolveConnectionSecret(string userName, string password, ConnectionCredentialMode mode)
    {
        if (mode == ConnectionCredentialMode.FixedPassword)
        {
            try
            {
                // Fixed passwords are exact text, including leading and trailing spaces.
                return DeriveFixedSecret(userName, password);
            }
            catch (ArgumentException)
            {
                throw new ConnectionCredentialException(
                    "invalid_fixed_password",
                    "被控端使用固定密码，请输入设置时的完整密码（12–256 个字符，首尾空格也属于密码）。");
            }
        }

        if (mode != ConnectionCredentialMode.RotatingRandomPassword)
        {
            throw new InvalidDataException("Relay 返回了不支持的密码类型，请更新两端客户端和 Relay。");
        }

        var secret = password?.Trim() ?? string.Empty;
        try
        {
            if (secret.Length is not (43 or 44) || secret.Any(char.IsWhiteSpace))
            {
                throw new FormatException();
            }

            var digest = OneTimeInvitationFactory.HashSecret(secret);
            CryptographicOperations.ZeroMemory(digest);
            return secret;
        }
        catch (FormatException)
        {
            throw new ConnectionCredentialException(
                "invalid_random_password",
                "被控端当前使用随机密码，请复制其显示的完整 43 位随机密码。若要使用自设密码，请先在被控端选择“用户名 + 固定密码”并重新开启连接授权。");
        }
    }

    public static bool IsSupportedRotation(TimeSpan rotation) =>
        rotation == TimeSpan.Zero ||
        rotation == TimeSpan.FromMinutes(5) ||
        rotation == TimeSpan.FromMinutes(10) ||
        rotation == TimeSpan.FromMinutes(30) ||
        rotation == TimeSpan.FromHours(1) ||
        rotation == TimeSpan.FromHours(6) ||
        rotation == TimeSpan.FromHours(12) ||
        rotation == TimeSpan.FromDays(1) ||
        rotation == TimeSpan.FromDays(3) ||
        rotation == TimeSpan.FromDays(7) ||
        rotation == TimeSpan.FromDays(30);

    private static string EncodeSecret(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

public sealed class ConnectionCredentialException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
