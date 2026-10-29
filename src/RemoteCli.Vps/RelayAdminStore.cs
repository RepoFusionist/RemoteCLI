using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RemoteCli.Vps;

/// <summary>
/// Stores the small amount of mutable relay administration state outside the relay room
/// process. Passwords and bootstrap material never leave this class or appear in API responses.
/// </summary>
public sealed class RelayAdminStore
{
    public const string OfficialClientHeader = "X-RemoteCLI-Client";
    public const string OfficialClientValuePrefix = "RemoteCLI/";
    private const int MinimumPasswordLength = 12;
    private const int MaximumPasswordLength = 256;
    private const int PasswordIterations = 210_000;
    private readonly object _sync = new();
    private readonly string _dataDirectory;
    private readonly string _statePath;
    private readonly string _bootstrapPath;
    private readonly string _identityPath;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _sessions = new(StringComparer.Ordinal);
    private PersistedState? _state;

    public RelayAdminStore(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var configuredPath = configuration["Relay:Admin:DataPath"] ??
            Environment.GetEnvironmentVariable("REMOTECLI_ADMIN_DATA");
        _dataDirectory = string.IsNullOrWhiteSpace(configuredPath)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RemoteCLI", "RelayAdmin")
            : Path.GetFullPath(configuredPath);
        if (string.IsNullOrWhiteSpace(_dataDirectory))
        {
            _dataDirectory = Path.Combine(AppContext.BaseDirectory, "data", "admin");
        }

        _statePath = Path.Combine(_dataDirectory, "admin.json");
        _bootstrapPath = Path.Combine(_dataDirectory, "bootstrap.key");
        _identityPath = Path.Combine(_dataDirectory, "server.identity");
        Directory.CreateDirectory(_dataDirectory);
        LoadState();
        EnsureServerIdentity();
        if (_state is null)
        {
            EnsureBootstrapKey();
        }
    }

    public bool IsInitialized
    {
        get { lock (_sync) return _state is not null; }
    }

    public string DataDirectory => _dataDirectory;

    public string ServerFingerprintSha256
    {
        get
        {
            var identity = File.ReadAllBytes(_identityPath);
            try
            {
                return Convert.ToHexString(SHA256.HashData(identity));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(identity);
            }
        }
    }

    public bool AllowUnofficialClients
    {
        get { lock (_sync) return _state?.AllowUnofficialClients ?? true; }
    }

    public bool AllowFixedPasswords
    {
        get { lock (_sync) return _state?.AllowFixedPasswords ?? true; }
    }

    public bool AllowRandomPasswords
    {
        get { lock (_sync) return _state?.AllowRandomPasswords ?? true; }
    }

    public int MaxAuthorizationMinutes
    {
        get { lock (_sync) return _state?.MaxAuthorizationMinutes ?? 10; }
    }

    public int RateLimitPerMinute
    {
        get { lock (_sync) return _state?.RateLimitPerMinute ?? 30; }
    }

    public bool IsOfficialClient(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var value = request.Headers[OfficialClientHeader].ToString();
        return value.StartsWith(OfficialClientValuePrefix, StringComparison.Ordinal);
    }

    public bool IsCredentialModeAllowed(Common.Protocol.ConnectionCredentialMode mode) => mode switch
    {
        Common.Protocol.ConnectionCredentialMode.FixedPassword => AllowFixedPasswords,
        Common.Protocol.ConnectionCredentialMode.RotatingRandomPassword => AllowRandomPasswords,
        _ => false
    };

    public bool IsAuthorizationLifetimeAllowed(DateTimeOffset expiresAt) =>
        expiresAt <= DateTimeOffset.UtcNow.AddMinutes(MaxAuthorizationMinutes);

    public AdminSetupStatus GetSetupStatus() => new(IsInitialized, ServerFingerprintSha256);

    public AdminConfigView GetConfig()
    {
        lock (_sync)
        {
            var state = _state;
            return new AdminConfigView(
                state?.RootUser ?? string.Empty,
                state is not null,
                state?.AllowUnofficialClients ?? true,
                state?.AllowFixedPasswords ?? true,
                state?.AllowRandomPasswords ?? true,
                state?.MaxAuthorizationMinutes ?? 10,
                state?.RateLimitPerMinute ?? 30,
                ServerFingerprintSha256);
        }
    }

    public bool TryInitialize(string bootstrapKey, string rootUser, string password, out string error)
    {
        if (!TryNormalizeRoot(rootUser, out rootUser, out error) || !ValidatePassword(password, out error))
        {
            return false;
        }

        lock (_sync)
        {
            if (_state is not null)
            {
                error = "already_initialized";
                return false;
            }

            byte[] expected;
            try
            {
                expected = File.ReadAllBytes(_bootstrapPath);
            }
            catch (IOException)
            {
                error = "bootstrap_key_unavailable";
                return false;
            }

            var supplied = Encoding.UTF8.GetBytes(bootstrapKey ?? string.Empty);
            try
            {
                if (!CryptographicOperations.FixedTimeEquals(expected, supplied))
                {
                    error = "invalid_bootstrap_key";
                    return false;
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(expected);
                CryptographicOperations.ZeroMemory(supplied);
            }

            var salt = RandomNumberGenerator.GetBytes(32);
            var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, PasswordIterations, HashAlgorithmName.SHA256, 32);
            _state = new PersistedState
            {
                RootUser = rootUser,
                PasswordSalt = Convert.ToBase64String(salt),
                PasswordHash = Convert.ToBase64String(hash),
                PasswordIterations = PasswordIterations,
                AllowUnofficialClients = false,
                AllowFixedPasswords = true,
                AllowRandomPasswords = true,
                MaxAuthorizationMinutes = 10,
                RateLimitPerMinute = 30,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            CryptographicOperations.ZeroMemory(salt);
            CryptographicOperations.ZeroMemory(hash);
            SaveStateLocked();
            TryDeleteBootstrapKey();
            error = string.Empty;
            return true;
        }
    }

    public bool TryLogin(string rootUser, string password, out string token, out string error)
    {
        token = string.Empty;
        lock (_sync)
        {
            if (_state is null || !string.Equals(_state.RootUser, rootUser?.Trim(), StringComparison.Ordinal))
            {
                error = "invalid_credentials";
                return false;
            }

            byte[] salt;
            byte[] expected;
            try
            {
                salt = Convert.FromBase64String(_state.PasswordSalt);
                expected = Convert.FromBase64String(_state.PasswordHash);
            }
            catch (FormatException)
            {
                error = "invalid_credentials";
                return false;
            }

            var actual = Rfc2898DeriveBytes.Pbkdf2(password ?? string.Empty, salt, _state.PasswordIterations, HashAlgorithmName.SHA256, expected.Length);
            var valid = CryptographicOperations.FixedTimeEquals(actual, expected);
            CryptographicOperations.ZeroMemory(salt);
            CryptographicOperations.ZeroMemory(expected);
            CryptographicOperations.ZeroMemory(actual);
            if (!valid)
            {
                error = "invalid_credentials";
                return false;
            }

            token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            _sessions[token] = DateTimeOffset.UtcNow.AddHours(8);
            error = string.Empty;
            return true;
        }
    }

    public bool IsAuthenticated(string? token)
    {
        if (string.IsNullOrWhiteSpace(token) || !_sessions.TryGetValue(token, out var expiresAt))
        {
            return false;
        }

        if (expiresAt > DateTimeOffset.UtcNow)
        {
            return true;
        }

        _sessions.TryRemove(token, out _);
        return false;
    }

    public void Logout(string? token)
    {
        if (!string.IsNullOrWhiteSpace(token)) _sessions.TryRemove(token, out _);
    }

    public bool TryUpdateConfig(AdminConfigUpdate update, out string error)
    {
        ArgumentNullException.ThrowIfNull(update);
        if (update.MaxAuthorizationMinutes is < 1 or > 10 || update.RateLimitPerMinute is < 1 or > 600)
        {
            error = "invalid_limits";
            return false;
        }

        lock (_sync)
        {
            if (_state is null)
            {
                error = "not_initialized";
                return false;
            }

            _state.AllowUnofficialClients = update.AllowUnofficialClients;
            _state.AllowFixedPasswords = update.AllowFixedPasswords;
            _state.AllowRandomPasswords = update.AllowRandomPasswords;
            _state.MaxAuthorizationMinutes = update.MaxAuthorizationMinutes;
            _state.RateLimitPerMinute = update.RateLimitPerMinute;
            _state.UpdatedAt = DateTimeOffset.UtcNow;
            SaveStateLocked();
            error = string.Empty;
            return true;
        }
    }

    public bool TryChangePassword(string currentPassword, string newPassword, out string error)
    {
        if (!ValidatePassword(newPassword, out error)) return false;
        lock (_sync)
        {
            if (_state is null || !VerifyPasswordLocked(currentPassword))
            {
                error = "invalid_credentials";
                return false;
            }

            var salt = RandomNumberGenerator.GetBytes(32);
            var hash = Rfc2898DeriveBytes.Pbkdf2(newPassword, salt, PasswordIterations, HashAlgorithmName.SHA256, 32);
            _state.PasswordSalt = Convert.ToBase64String(salt);
            _state.PasswordHash = Convert.ToBase64String(hash);
            _state.PasswordIterations = PasswordIterations;
            _state.UpdatedAt = DateTimeOffset.UtcNow;
            CryptographicOperations.ZeroMemory(salt);
            CryptographicOperations.ZeroMemory(hash);
            SaveStateLocked();
            _sessions.Clear();
            error = string.Empty;
            return true;
        }
    }

    private bool VerifyPasswordLocked(string password)
    {
        if (_state is null) return false;
        try
        {
            var salt = Convert.FromBase64String(_state.PasswordSalt);
            var expected = Convert.FromBase64String(_state.PasswordHash);
            var actual = Rfc2898DeriveBytes.Pbkdf2(password ?? string.Empty, salt, _state.PasswordIterations, HashAlgorithmName.SHA256, expected.Length);
            var valid = CryptographicOperations.FixedTimeEquals(actual, expected);
            CryptographicOperations.ZeroMemory(salt);
            CryptographicOperations.ZeroMemory(expected);
            CryptographicOperations.ZeroMemory(actual);
            return valid;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private void LoadState()
    {
        if (!File.Exists(_statePath)) return;
        var json = File.ReadAllText(_statePath, Encoding.UTF8);
        var state = JsonSerializer.Deserialize<PersistedState>(json, JsonOptions);
        if (state is null || !TryNormalizeRoot(state.RootUser, out _, out _) ||
            state.PasswordIterations < 100_000 || state.PasswordIterations > 1_000_000 ||
            string.IsNullOrWhiteSpace(state.PasswordSalt) || string.IsNullOrWhiteSpace(state.PasswordHash))
        {
            throw new InvalidOperationException("The Remote CLI admin configuration is invalid or incomplete.");
        }

        _state = state;
    }

    private void EnsureServerIdentity()
    {
        if (File.Exists(_identityPath))
        {
            var existing = File.ReadAllBytes(_identityPath);
            if (existing.Length == 32) return;
            CryptographicOperations.ZeroMemory(existing);
            throw new InvalidOperationException("The Remote CLI server identity is invalid.");
        }

        WriteNewFile(_identityPath, RandomNumberGenerator.GetBytes(32));
    }

    private void EnsureBootstrapKey()
    {
        if (File.Exists(_bootstrapPath)) return;
        WriteNewFile(_bootstrapPath, Encoding.UTF8.GetBytes(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))));
    }

    private void SaveStateLocked()
    {
        var temporaryPath = _statePath + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(_state, JsonOptions), new UTF8Encoding(false));
        if (File.Exists(_statePath))
        {
            try
            {
                File.Replace(temporaryPath, _statePath, null);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                File.Copy(temporaryPath, _statePath, overwrite: true);
                File.Delete(temporaryPath);
            }
        }
        else
        {
            // Some managed Linux/Windows service volumes do not allow MoveFileEx for a
            // newly-created file even though ordinary file writes are permitted.
            File.Copy(temporaryPath, _statePath, overwrite: false);
            File.Delete(temporaryPath);
        }
    }

    private static void WriteNewFile(string path, byte[] bytes)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.SequentialScan);
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush(true);
        }
        catch (IOException) when (File.Exists(path))
        {
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private void TryDeleteBootstrapKey()
    {
        try { File.Delete(_bootstrapPath); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static bool TryNormalizeRoot(string? value, out string normalized, out string error)
    {
        normalized = (value ?? string.Empty).Trim();
        if (!string.Equals(normalized, "root", StringComparison.OrdinalIgnoreCase))
        {
            error = "root_user_required";
            return false;
        }

        normalized = "root";
        error = string.Empty;
        return true;
    }

    private static bool ValidatePassword(string? value, out string error)
    {
        if (string.IsNullOrEmpty(value) || value.Length is < MinimumPasswordLength or > MaximumPasswordLength || value.Any(char.IsControl))
        {
            error = "password_length_invalid";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private sealed class PersistedState
    {
        public string RootUser { get; set; } = "root";
        public string PasswordSalt { get; set; } = string.Empty;
        public string PasswordHash { get; set; } = string.Empty;
        public int PasswordIterations { get; set; }
        public bool AllowUnofficialClients { get; set; }
        public bool AllowFixedPasswords { get; set; }
        public bool AllowRandomPasswords { get; set; }
        public int MaxAuthorizationMinutes { get; set; }
        public int RateLimitPerMinute { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
    }
}

public sealed record AdminSetupStatus(bool Initialized, string ServerFingerprintSha256);

public sealed record AdminConfigView(
    string RootUser,
    bool Initialized,
    bool AllowUnofficialClients,
    bool AllowFixedPasswords,
    bool AllowRandomPasswords,
    int MaxAuthorizationMinutes,
    int RateLimitPerMinute,
    string ServerFingerprintSha256);

public sealed record AdminConfigUpdate(
    bool AllowUnofficialClients,
    bool AllowFixedPasswords,
    bool AllowRandomPasswords,
    int MaxAuthorizationMinutes,
    int RateLimitPerMinute);
