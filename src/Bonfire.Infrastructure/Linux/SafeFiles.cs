using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Bonfire.Infrastructure.Linux;

public sealed record TreeEntry(string RelativePath, string FullPath, bool IsDirectory);

/// <summary>
/// File operations on trees written by an untrusted party (the server inside its container):
/// symbolic links are never followed, only regular files are read, deletion never leaves the tree.
/// Callers make sure the container is stopped, so nothing changes the tree concurrently.
/// </summary>
public static class SafeFiles
{
    /// <summary>Lists a tree, parents before children. Links, sockets, FIFOs and devices throw.</summary>
    public static List<TreeEntry> Walk(string root)
    {
        var rootInfo = new DirectoryInfo(root);
        if (!rootInfo.Exists || rootInfo.LinkTarget is not null)
        {
            throw new UnsupportedEntryException(".", "root is missing or a link");
        }

        var result = new List<TreeEntry>();
        var pending = new Stack<(DirectoryInfo Dir, string Relative)>();
        pending.Push((rootInfo, ""));
        while (pending.Count > 0)
        {
            var (dir, relative) = pending.Pop();
            foreach (var entry in dir.EnumerateFileSystemInfos().OrderBy(e => e.Name, StringComparer.Ordinal))
            {
                var rel = relative.Length == 0 ? entry.Name : $"{relative}/{entry.Name}";
                if (entry.LinkTarget is not null)
                {
                    throw new UnsupportedEntryException(rel, "symbolic link");
                }

                if (entry is DirectoryInfo sub)
                {
                    result.Add(new TreeEntry(rel, sub.FullName, true));
                    pending.Push((sub, rel));
                }
                else
                {
                    result.Add(new TreeEntry(rel, entry.FullName, false));
                }
            }
        }

        return result;
    }

    /// <summary>Opens a regular file for reading; links (O_NOFOLLOW) and special files are refused.</summary>
    public static FileStream OpenRegularFile(string path, string relativeForErrors)
    {
        var fd = LibC.Open(path, LibC.O_RDONLY | LibC.O_NOFOLLOW | LibC.O_CLOEXEC | LibC.O_NONBLOCK, 0);
        if (fd < 0)
        {
            throw new UnsupportedEntryException(relativeForErrors, Marshal.GetPInvokeErrorMessage(Marshal.GetLastPInvokeError()));
        }

        var handle = new SafeFileHandle(fd, ownsHandle: true);
        uint type;
        try
        {
            type = LibC.FileType(fd); // the type of what was actually opened, not of a path
        }
        catch
        {
            handle.Dispose();
            throw;
        }

        if (type != LibC.S_IFREG)
        {
            handle.Dispose();
            throw new UnsupportedEntryException(relativeForErrors, "not a regular file");
        }

        return new FileStream(handle, FileAccess.Read, bufferSize: 0);
    }

    /// <summary>
    /// Deletes a directory tree without following links: a link is removed itself, never its target.
    /// <paramref name="mustBeUnder"/> guards against deleting anything outside the expected parent.
    /// </summary>
    public static void DeleteTree(string path, string mustBeUnder)
    {
        var full = Path.GetFullPath(path);
        var parent = Path.GetFullPath(mustBeUnder).TrimEnd('/') + "/";
        if (!full.StartsWith(parent, StringComparison.Ordinal) || full.Length <= parent.Length)
        {
            throw new InvalidOperationException($"Refusing to delete {full}: not inside {parent}");
        }

        var info = new DirectoryInfo(full);
        if (info.LinkTarget is not null)
        {
            File.Delete(full); // the link itself
            return;
        }

        if (!info.Exists)
        {
            return;
        }

        DeleteContents(info);
        info.Delete();
    }

    private static void DeleteContents(DirectoryInfo dir)
    {
        foreach (var entry in dir.EnumerateFileSystemInfos())
        {
            if (entry is DirectoryInfo sub && entry.LinkTarget is null)
            {
                DeleteContents(sub);
                sub.Delete();
            }
            else
            {
                File.Delete(entry.FullName); // files, links and special files: the entry itself
            }
        }
    }

    /// <summary>
    /// Atomic rename that never replaces an existing entry (renameat2 RENAME_NOREPLACE). Renaming
    /// moves a link as a link, never its target. Both paths must be on the same filesystem.
    /// </summary>
    public static void MoveNoReplace(string from, string to)
    {
        if (LibC.RenameAt2(LibC.AT_FDCWD, from, LibC.AT_FDCWD, to, LibC.RENAME_NOREPLACE) != 0)
        {
            var errno = Marshal.GetLastPInvokeError();
            throw errno == LibC.EEXIST
                ? new IOException($"{Path.GetFileName(to)} already exists")
                : new IOException($"Cannot move {Path.GetFileName(from)}: {Marshal.GetPInvokeErrorMessage(errno)}");
        }
    }

    /// <summary>True if anything (file, directory, link — even a dangling one) exists at the path.</summary>
    public static bool EntryExists(string path)
    {
        var info = new FileInfo(path);
        return info.Exists || info.LinkTarget is not null || Directory.Exists(path);
    }

    /// <summary>
    /// Creates the directory if needed (owner-only, its entry synced to disk) and requires a real
    /// directory, not a link. The parent must already exist.
    /// </summary>
    public static void EnsureRealDirectory(string path)
    {
        var info = new DirectoryInfo(path);
        if (info.LinkTarget is not null || File.Exists(path))
        {
            throw new UnsupportedEntryException(info.Name, "expected a directory");
        }

        if (!info.Exists)
        {
            Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            FsyncDirectory(Path.GetDirectoryName(Path.GetFullPath(path).TrimEnd('/'))!);
            info.Refresh();
            if (info.LinkTarget is not null)
            {
                throw new UnsupportedEntryException(info.Name, "expected a directory");
            }
        }
    }

    /// <summary>Makes a directory's entries (renames, new files) durable.</summary>
    public static void FsyncDirectory(string directory)
    {
        var fd = LibC.Open(directory, LibC.O_RDONLY | LibC.O_DIRECTORY | LibC.O_CLOEXEC, 0);
        if (fd < 0)
        {
            throw new IOException($"Cannot open {directory}: {Marshal.GetPInvokeErrorMessage(Marshal.GetLastPInvokeError())}");
        }

        using var handle = new SafeFileHandle(fd, ownsHandle: true);
        if (LibC.Fsync(fd) != 0)
        {
            throw new IOException($"fsync of {directory} failed: {Marshal.GetPInvokeErrorMessage(Marshal.GetLastPInvokeError())}");
        }
    }
}

public sealed class UnsupportedEntryException(string relativePath, string reason)
    : IOException($"{relativePath}: {reason}")
{
    public string RelativePath { get; } = relativePath;
}
