using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Bonfire.Core.Content;
using Bonfire.Infrastructure.Linux;

namespace Bonfire.Infrastructure.Content;

public enum JarKind
{
    FabricMod,
    BukkitPlugin,
    Unknown,
}

/// <summary>What a local JAR claims about itself. Claims only — compatibility is not verified.</summary>
public sealed record LocalJarInfo(string Sha512, long Size, JarKind Kind, string? Id, string? Name, string? Version);

public sealed class InvalidJarException(string reason) : IOException(reason);

/// <summary>
/// Copies a user-chosen JAR into private staging first, then hashes and inspects that same copy
/// (the source cannot be swapped in between). Inspection only reads descriptor entries with
/// strict limits — mod code is never loaded or run.
/// </summary>
public static class LocalJar
{
    public const int MaxEntries = 20_000;
    public const long MaxDescriptorBytes = 1024 * 1024;

    public static async Task<LocalJarInfo> StageAsync(string sourcePath, string stagingPath, CancellationToken cancellationToken)
    {
        var created = false;
        try
        {
            await using (var source = SafeFiles.OpenRegularFile(sourcePath, Path.GetFileName(sourcePath)))
            {
                if (source.Length is <= 0 or > ContentRules.MaxFileBytes)
                {
                    throw new InvalidJarException("file is empty or too large");
                }

                await using var copy = new FileStream(stagingPath, new FileStreamOptions
                {
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.Write,
                    UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
                });
                created = true;

                // Counted copy: the source may grow while being read.
                var buffer = new byte[81920];
                long total = 0;
                int n;
                while ((n = await source.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    total += n;
                    if (total > ContentRules.MaxFileBytes)
                    {
                        throw new InvalidJarException("file is too large");
                    }

                    await copy.WriteAsync(buffer.AsMemory(0, n), cancellationToken);
                }

                copy.Flush(flushToDisk: true);
            }

            return Inspect(stagingPath);
        }
        catch
        {
            if (created)
            {
                File.Delete(stagingPath); // only the copy this call made
            }

            throw;
        }
    }

    public static LocalJarInfo Inspect(string path)
    {
        string sha;
        long size;
        using (var stream = SafeFiles.OpenRegularFile(path, Path.GetFileName(path)))
        {
            size = stream.Length;
            sha = Convert.ToHexStringLower(SHA512.HashData(stream));
        }

        try
        {
            using var zip = ZipFile.OpenRead(path);
            if (zip.Entries.Count > MaxEntries)
            {
                throw new InvalidJarException("too many entries");
            }

            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in zip.Entries)
            {
                if (!names.Add(entry.FullName))
                {
                    throw new InvalidJarException("duplicate entries");
                }
            }

            if (Read(zip, "fabric.mod.json") is { } fabric)
            {
                using var doc = JsonDocument.Parse(fabric, new JsonDocumentOptions { MaxDepth = 32, AllowTrailingCommas = true });
                var root = doc.RootElement;
                return new LocalJarInfo(sha, size, JarKind.FabricMod, Text(root, "id"), Text(root, "name"), Text(root, "version"));
            }

            if ((Read(zip, "paper-plugin.yml") ?? Read(zip, "plugin.yml")) is { } yaml)
            {
                return new LocalJarInfo(sha, size, JarKind.BukkitPlugin, null, YamlValue(yaml, "name"), YamlValue(yaml, "version"));
            }

            return new LocalJarInfo(sha, size, JarKind.Unknown, null, null, null);
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException)
        {
            throw new InvalidJarException("not a valid JAR: " + ex.Message);
        }
    }

    private static string? Read(ZipArchive zip, string name)
    {
        var entry = zip.GetEntry(name);
        if (entry is null)
        {
            return null;
        }

        if (entry.Length > MaxDescriptorBytes)
        {
            throw new InvalidJarException($"{name} is too large");
        }

        using var stream = entry.Open();
        var buffer = new byte[MaxDescriptorBytes + 1];
        var read = 0;
        int n;
        while (read < buffer.Length && (n = stream.Read(buffer, read, buffer.Length - read)) > 0)
        {
            read += n;
        }

        if (read > MaxDescriptorBytes)
        {
            throw new InvalidJarException($"{name} is too large"); // declared length lied
        }

        return System.Text.Encoding.UTF8.GetString(buffer, 0, read);
    }

    private static string? Text(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? Clip(v.GetString())
            : null;

    /// <summary>Top-level "key: value" only; anything fancier is ignored.</summary>
    private static string? YamlValue(string yaml, string key)
    {
        foreach (var line in yaml.Split('\n'))
        {
            if (line.StartsWith(key + ":", StringComparison.Ordinal))
            {
                return Clip(line[(key.Length + 1)..].Trim().Trim('"', '\'', '\r'));
            }
        }

        return null;
    }

    private static string? Clip(string? text) =>
        text is null ? null : new string(text.Where(c => !char.IsControl(c)).Take(100).ToArray());
}
