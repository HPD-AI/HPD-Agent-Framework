using System.Text.Json;
using System.Text.Json.Serialization;

namespace HPD_Agent.CLI.Auth;

/// <summary>
/// Manages persistent storage of authentication credentials.
/// Stores in ~/.local/share/HPD-Agent/auth.json with secure file permissions.
/// </summary>
public class AuthStorage
{
    private static readonly string DefaultDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HPD-Agent");

    private static readonly string DefaultPath = Path.Combine(DefaultDirectory, "auth.json");

    private readonly string _filePath;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private Dictionary<string, AuthEntry>? _cache;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public AuthStorage() : this(DefaultPath) { }

    public AuthStorage(string filePath)
    {
        _filePath = filePath;
    }

    /// <summary>
    /// Gets the path to the auth storage file.
    /// </summary>
    public string FilePath => _filePath;

    /// <summary>
    /// Gets the authentication entry for a provider.
    /// </summary>
    public async Task<AuthEntry?> GetAsync(string providerId)
    {
        var all = await LoadAsync();
        return all.TryGetValue(providerId.ToLowerInvariant(), out var entry) ? entry : null;
    }

    /// <summary>
    /// Sets the authentication entry for a provider.
    /// </summary>
    public async Task SetAsync(string providerId, AuthEntry entry)
    {
        await _lock.WaitAsync();
        try
        {
            var all = await LoadInternalAsync();
            all[providerId.ToLowerInvariant()] = entry;
            await SaveInternalAsync(all);
            _cache = all;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Removes the authentication entry for a provider.
    /// </summary>
    public async Task<bool> RemoveAsync(string providerId)
    {
        await _lock.WaitAsync();
        try
        {
            var all = await LoadInternalAsync();
            var removed = all.Remove(providerId.ToLowerInvariant());
            if (removed)
            {
                await SaveInternalAsync(all);
                _cache = all;
            }
            return removed;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Gets all authentication entries.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, AuthEntry>> GetAllAsync()
    {
        return await LoadAsync();
    }

    /// <summary>
    /// Clears all authentication entries.
    /// </summary>
    public async Task ClearAsync()
    {
        await _lock.WaitAsync();
        try
        {
            if (File.Exists(_filePath))
            {
                File.Delete(_filePath);
            }
            _cache = new Dictionary<string, AuthEntry>();
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Checks if a provider has stored credentials.
    /// </summary>
    public async Task<bool> HasCredentialsAsync(string providerId)
    {
        var entry = await GetAsync(providerId);
        return entry != null;
    }

    private async Task<Dictionary<string, AuthEntry>> LoadAsync()
    {
        await _lock.WaitAsync();
        try
        {
            return await LoadInternalAsync();
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<Dictionary<string, AuthEntry>> LoadInternalAsync()
    {
        if (_cache != null)
        {
            return _cache;
        }

        if (!File.Exists(_filePath))
        {
            _cache = new Dictionary<string, AuthEntry>();
            return _cache;
        }

        try
        {
            var json = await File.ReadAllTextAsync(_filePath);
            _cache = JsonSerializer.Deserialize<Dictionary<string, AuthEntry>>(json, JsonOptions)
                     ?? new Dictionary<string, AuthEntry>();
            return _cache;
        }
        catch (JsonException)
        {
            // Corrupted file, start fresh
            _cache = new Dictionary<string, AuthEntry>();
            return _cache;
        }
    }

    private async Task SaveInternalAsync(Dictionary<string, AuthEntry> data)
    {
        // Ensure directory exists
        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(data, JsonOptions);
        await File.WriteAllTextAsync(_filePath, json);

        // Set file permissions to owner-only (0600) on Unix systems
        SetSecureFilePermissions(_filePath);
    }

    private static void SetSecureFilePermissions(string filePath)
    {
        if (!OperatingSystem.IsWindows())
        {
            try
            {
                // chmod 600 - owner read/write only
                File.SetUnixFileMode(filePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            catch
            {
                // Ignore permission errors on systems that don't support it
            }
        }
    }
}
