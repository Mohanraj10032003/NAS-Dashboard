using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ShipmentTracker.Api.Models;

namespace ShipmentTracker.Api.Services;

/// <summary>
/// Keeps every report that was read from the NAS as a small compressed file on THIS server (no database needed).
/// A report of a past day never changes, so it is read from the NAS once and afterwards comes from this disk in milliseconds,
/// also after the API was restarted, and even while the NAS cannot be reached (e.g. you are not in the office).
/// A cached file is used only while its saved date + size on the NAS are unchanged.
/// </summary>
public sealed class DiskCache(string folder, ILogger logger)
{
    private sealed record Entry(long ModifiedTicks, long Length, ParsedFile Parsed);
    private sealed record CatalogEntry(string Folder, DateTime BuiltAtUtc, List<CatalogFile> Files);

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public string Folder { get; } = folder;

    public static string KeyOf(string fullPath) =>
        Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(fullPath.ToLowerInvariant())))[..24].ToLowerInvariant();

    private string FileFor(string fullPath) => Path.Combine(Folder, KeyOf(fullPath) + ".rep.gz");
    private string CatalogFile_ => Path.Combine(Folder, "catalog.gz");

    public ParsedFile? TryLoad(string fullPath, long modifiedTicks, long length)
    {
        var path = FileFor(fullPath);
        try
        {
            if (!File.Exists(path)) return null;
            using var fs = File.OpenRead(path);
            using var gz = new GZipStream(fs, CompressionMode.Decompress);
            var e = JsonSerializer.Deserialize<Entry>(gz, Json);
            return e is not null && e.ModifiedTicks == modifiedTicks && e.Length == length ? e.Parsed : null;
        }
        catch (Exception ex)
        {
            logger.LogDebug("Disk cache entry ignored ({File}): {Message}", path, ex.Message);
            return null;
        }
    }

    public void Save(string fullPath, long modifiedTicks, long length, ParsedFile parsed)
        => WriteAtomic(FileFor(fullPath), new Entry(modifiedTicks, length, parsed));

    public void SaveCatalog(string nasFolder, DateTime builtAtUtc, IReadOnlyList<CatalogFile> files)
        => WriteAtomic(CatalogFile_, new CatalogEntry(nasFolder, builtAtUtc, files.ToList()));

    /// <summary>The saved folder list, only when it was made for the same NAS folder (the folder can be changed on the Settings page).</summary>
    public (DateTime BuiltAtUtc, List<CatalogFile> Files)? TryLoadCatalog(string nasFolder)
    {
        try
        {
            if (!File.Exists(CatalogFile_)) return null;
            using var fs = File.OpenRead(CatalogFile_);
            using var gz = new GZipStream(fs, CompressionMode.Decompress);
            var e = JsonSerializer.Deserialize<CatalogEntry>(gz, Json);
            return e is null || !string.Equals(e.Folder, nasFolder, StringComparison.OrdinalIgnoreCase) ? null : (e.BuiltAtUtc, e.Files);
        }
        catch { return null; }
    }

    /// <summary>Deletes cached reports whose file no longer exists on the NAS.</summary>
    public void Prune(IEnumerable<string> alivePaths)
    {
        try
        {
            if (!Directory.Exists(Folder)) return;
            var alive = alivePaths.Select(KeyOf).ToHashSet();
            foreach (var f in Directory.EnumerateFiles(Folder, "*.rep.gz"))
                if (!alive.Contains(Path.GetFileName(f)[..^".rep.gz".Length])) File.Delete(f);
        }
        catch (Exception ex) { logger.LogDebug("Disk cache prune failed: {Message}", ex.Message); }
    }

    private void WriteAtomic<T>(string path, T value)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            var tmp = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
            using (var fs = File.Create(tmp))
            using (var gz = new GZipStream(fs, CompressionLevel.Fastest))
                JsonSerializer.Serialize(gz, value, Json);
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            logger.LogWarning("Could not write the disk cache ({Path}): {Message}", path, ex.Message);
        }
    }
}
