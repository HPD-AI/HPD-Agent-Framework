using HPD.Agent.Secrets;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace HPD_Agent.CLI.Auth;

/// <summary>
/// Bridges AuthStorage (auth.json) into the ISecretResolver chain.
/// Registered by the CLI at startup, invisible to library-only users.
///
/// Only resolves the credential string — OAuth-specific concerns (BaseUrl,
/// CustomHeaders, AccountId) are handled separately by IAuthProvider.LoadAsync().
/// </summary>
public sealed class AuthStorageSecretResolver : ISecretResolver
{
    private readonly AuthStorage _storage;
    private readonly AuthManager _authManager;

    public AuthStorageSecretResolver(AuthStorage storage, AuthManager authManager)
    {
        _storage = storage;
        _authManager = authManager;
    }

    public async ValueTask<ResolvedSecret?> ResolveAsync(string key, CancellationToken ct = default)
    {
        var scope = key.Contains(':') ? key[..key.IndexOf(':')] : key;

        var entry = await _storage.GetAsync(scope);
        if (entry is null)
            return null;

        // If there's a registered auth provider, use it for refresh
        var provider = _authManager.GetProvider(scope);
        if (provider != null)
        {
            var refreshed = await provider.RefreshIfNeededAsync(entry);
            if (refreshed != null)
            {
                await _storage.SetAsync(scope, refreshed);
                entry = refreshed;
            }
        }

        return new ResolvedSecret
        {
            Value = entry.GetCredential(),
            Source = entry switch
            {
                OAuthEntry => "oauth",
                ApiKeyEntry => $"auth-storage:{scope}",
                WellKnownEntry wk => $"env:{wk.EnvVarName}",
                _ => "auth-storage"
            },
            ExpiresAt = entry is OAuthEntry o ? DateTimeOffset.FromUnixTimeMilliseconds(o.ExpiresAtUnixMs) : null
        };
    }
}
