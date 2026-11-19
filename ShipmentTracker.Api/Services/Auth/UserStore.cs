using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using ShipmentTracker.Api.Options;

namespace ShipmentTracker.Api.Services.Auth;

/// <summary>Checks a username/password against Auth:Users and slows down guessing (5 wrong tries = 5 minute pause).</summary>
public sealed class UserStore(IOptionsMonitor<AuthOptions> options)
{
    private const int MaxFailures = 5;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(5);

    // a real-looking hash so a wrong username takes as long to reject as a wrong password
    private static readonly string DummyHash = PasswordHasher.Hash(Guid.NewGuid().ToString());

    private readonly ConcurrentDictionary<string, (int Count, DateTime Since)> _failures = new();

    public bool IsLocked(string username)
    {
        var key = username.ToLowerInvariant();
        if (!_failures.TryGetValue(key, out var f)) return false;
        if (DateTime.UtcNow - f.Since > Window) { _failures.TryRemove(key, out _); return false; }
        return f.Count >= MaxFailures;
    }

    public UserEntry? Validate(string username, string password)
    {
        var user = options.CurrentValue.Users.FirstOrDefault(u => u.Username.Equals(username, StringComparison.OrdinalIgnoreCase));
        var ok = PasswordHasher.Verify(password, user?.PasswordHash ?? DummyHash) && user is not null;

        var key = username.ToLowerInvariant();
        if (ok) { _failures.TryRemove(key, out _); return user; }

        _failures.AddOrUpdate(key, _ => (1, DateTime.UtcNow), (_, old) =>
            DateTime.UtcNow - old.Since > Window ? (1, DateTime.UtcNow) : (old.Count + 1, old.Since));
        return null;
    }

    public UserEntry? Find(string username) =>
        options.CurrentValue.Users.FirstOrDefault(u => u.Username.Equals(username, StringComparison.OrdinalIgnoreCase));
}
