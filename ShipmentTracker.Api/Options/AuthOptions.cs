namespace ShipmentTracker.Api.Options;

/// <summary>Dashboard login ("Auth" section of appsettings).</summary>
public sealed class AuthOptions
{
    public const string SectionName = "Auth";

    /// <summary>Signing key for login tokens (32+ characters). Empty = a random key is created at every start.</summary>
    public string? JwtSecret { get; set; }

    public int TokenHours { get; set; } = 12;

    public List<UserEntry> Users { get; set; } = [];
}

public sealed class UserEntry
{
    public string Username { get; set; } = "";
    public string Name { get; set; } = "";
    public string Role { get; set; } = "Viewer";

    /// <summary>Generate with:  dotnet run -- hash-password "YourPassword"</summary>
    public string PasswordHash { get; set; } = "";
}
