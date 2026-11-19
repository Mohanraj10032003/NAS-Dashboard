using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ShipmentTracker.Api.Models;
using ShipmentTracker.Api.Options;

namespace ShipmentTracker.Api.Services.Nas;

public interface INasConnector
{
    /// <summary>Makes sure the configured folder can be read; logs in to the share with Nas:Username / Nas:Password when needed.</summary>
    Task EnsureConnectedAsync(NasOptions options, CancellationToken ct);
}

/// <summary>
/// Real-world analogy: the NAS is an office building with a security desk. This class walks up to the desk,
/// shows the ID card (username + password) once, and gets a visitor pass for the rest of the day.
/// On Windows the pass is a normal SMB connection (WNetAddConnection2 - the same thing as
/// "net use \\server\share /user:..."), so every Directory / File call afterwards just works.
/// </summary>
public sealed class NasConnector(ILogger<NasConnector> logger) : INasConnector
{
    public async Task EnsureConnectedAsync(NasOptions options, CancellationToken ct)
    {
        var path = options.Path?.Trim();
        if (string.IsNullOrEmpty(path))
            throw new NasUnavailableException("Nas:Path is not configured. Set it in appsettings.json, e.g. \\\\NAS-SERVER\\Shipments.");

        // 1. Already reachable? (local folder, mounted share, or Windows already holds a connection)
        if (await ExistsAsync(path, options, ct)) return;

        // 2. Not reachable: log in to the share, then look again.
        if (!OperatingSystem.IsWindows())
        {
            throw new NasUnavailableException(
                $"Folder '{path}' is not reachable. On Linux/macOS mount the share first " +
                "(e.g. sudo mount -t cifs //server/share /mnt/nas -o username=...,password=...) and set Nas:Path to the mount point.");
        }

        var share = GetShareRoot(path);
        if (share is null)
            throw new NasUnavailableException($"Folder '{path}' was not found. Check Nas:Path (a mapped drive letter like Z:\\ is not visible to a service - use the \\\\server\\share form).");

        if (string.IsNullOrWhiteSpace(options.Username))
            throw new NasUnavailableException($"Folder '{path}' is not reachable and no Nas:Username is configured.");

        var user = ComposeUser(options);
        logger.LogInformation("Connecting to {Share} as {User}", share, user);

        try
        {
            ConnectWindows(share, user, options.Password ?? "");
        }
        catch (NasUnavailableException) { throw; }
        catch (Exception ex)
        {
            throw new NasUnavailableException(ex.Message, ex);
        }

        if (!await ExistsAsync(path, options, ct))
            throw new NasUnavailableException($"Logged in to {share}, but the folder '{path}' does not exist or the account cannot read it.");

        logger.LogInformation("NAS connected: {Path}", path);
    }

    // ------------------------------------------------------------------ helpers

    private static async Task<bool> ExistsAsync(string path, NasOptions options, CancellationToken ct)
    {
        try
        {
            // Directory.Exists on a dead server can block for a long time, so never wait forever.
            return await Task.Run(() => Directory.Exists(path), ct)
                .WaitAsync(TimeSpan.FromSeconds(Math.Max(3, options.NetworkTimeoutSeconds)), ct);
        }
        catch (TimeoutException)
        {
            throw new NasUnavailableException($"Timed out after {options.NetworkTimeoutSeconds}s waiting for '{path}'. Is the server on and reachable from this machine?");
        }
    }

    private static string ComposeUser(NasOptions o)
    {
        var user = o.Username!.Trim();
        if (user.Contains('\\') || user.Contains('@') || string.IsNullOrWhiteSpace(o.Domain)) return user;
        return $"{o.Domain!.Trim()}\\{user}";
    }

    /// <summary>\\server\share\sub\folder  ->  \\server\share</summary>
    internal static string? GetShareRoot(string path)
    {
        var p = path.Trim().Replace('/', '\\');
        if (!p.StartsWith(@"\\")) return null;
        var parts = p.TrimStart('\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length < 2 ? null : $@"\\{parts[0]}\{parts[1]}";
    }

    // ------------------------------------------------------------------ Windows SMB login

    [SupportedOSPlatform("windows")]
    private static void ConnectWindows(string share, string user, string password)
    {
        var resource = new NetResource { Type = ResourceTypeDisk, RemoteName = share };

        var rc = WNetAddConnection2(ref resource, password, user, 0);

        // 1219: Windows already has a connection to this server under other credentials -> drop it and retry once.
        if (rc == ErrorSessionCredentialConflict)
        {
            WNetCancelConnection2(share, 0, true);
            rc = WNetAddConnection2(ref resource, password, user, 0);
        }

        if (rc != 0)
        {
            var hint = rc switch
            {
                86 or 1326 => " (wrong username or password)",
                5 => " (access denied)",
                53 or 67 => " (server or share name not found)",
                1219 => " (this machine already uses other credentials for that server; run 'net use * /delete' and retry)",
                1231 or 1203 => " (server not reachable - check network / VPN / firewall)",
                _ => ""
            };
            throw new NasUnavailableException($"Could not log in to {share} as {user}: {new Win32Exception(rc).Message}{hint} [Win32 error {rc}]");
        }
    }

    private const int ResourceTypeDisk = 1;
    private const int ErrorSessionCredentialConflict = 1219;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NetResource
    {
        public int Scope;
        public int Type;
        public int DisplayType;
        public int Usage;
        public string? LocalName;
        public string? RemoteName;
        public string? Comment;
        public string? Provider;
    }

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetAddConnection2(ref NetResource netResource, string? password, string? username, int flags);

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetCancelConnection2(string name, int flags, bool force);
}
