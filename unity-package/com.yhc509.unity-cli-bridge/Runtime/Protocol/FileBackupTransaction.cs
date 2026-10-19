#nullable enable
using System;
using System.Collections.Generic;
using System.IO;

namespace UnityCli.Protocol
{
    public sealed class FileBackupTransactionException : Exception
    {
        public FileBackupTransactionException(string errorCode, string message, string? details = null, Exception? innerException = null)
            : base(message, innerException)
        {
            ErrorCode = errorCode;
            Details = details;
        }

        public string ErrorCode { get; }
        public string? Details { get; }
    }

    public sealed class FileBackupTransactionOptions
    {
        public Func<string>? BackupIdFactory { get; set; }
        public string? BackupRoot { get; set; }
        public Action<string>? WarningSink { get; set; }
        public Action? Refresh { get; set; }
    }

    public static class AtomicFileUtility
    {
        public static void WriteAllText(string destinationPath, string contents)
        {
            if (string.IsNullOrWhiteSpace(destinationPath))
            {
                throw new ArgumentException("destination path is empty.", nameof(destinationPath));
            }

            string fullPath = Path.GetFullPath(destinationPath);
            string? directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string tempPath = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(tempPath, contents);
                ReplaceTempFile(tempPath, fullPath);
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
        }

        public static int CleanupTempFiles(string directoryPath, string searchPattern = "*.tmp")
        {
            if (string.IsNullOrWhiteSpace(directoryPath) || !Directory.Exists(directoryPath))
            {
                return 0;
            }

            int deleted = 0;
            foreach (string tempPath in Directory.GetFiles(directoryPath, searchPattern, SearchOption.TopDirectoryOnly))
            {
                File.Delete(tempPath);
                deleted++;
            }

            return deleted;
        }

        internal static void ReplaceTempFile(string tempPath, string destinationPath)
        {
            if (File.Exists(destinationPath))
            {
                File.Replace(tempPath, destinationPath, null);
            }
            else
            {
                File.Move(tempPath, destinationPath);
            }
        }
    }

    public static class FileBackupTransaction
    {
        private const string BackupDirectoryName = "com.yhc509.unity-cli-bridge";

        private enum BackupMode
        {
            Copy,
            Move,
        }

        public static T RunWithBackup<T>(string path, string commandName, Func<T> action, FileBackupTransactionOptions? options = null)
        {
            return Run(path, commandName, action, BackupMode.Copy, options);
        }

        public static T RunWithMovedBackup<T>(string path, string commandName, Func<T> action, FileBackupTransactionOptions? options = null)
        {
            return Run(path, commandName, action, BackupMode.Move, options);
        }

        public static string BuildBackupPath(string path, string backupId)
        {
            return BuildBackupPath(path, backupId, backupRoot: null);
        }

        public static string BuildBackupPath(string path, string backupId, string? backupRoot)
        {
            return Path.Combine(BuildBackupDirectory(path, backupRoot), BuildBackupFileName(path, backupId));
        }

        public static string BuildBackupDirectory(string path, string? backupRoot = null)
        {
            if (!string.IsNullOrWhiteSpace(backupRoot))
            {
                return Path.GetFullPath(backupRoot);
            }

            string fullPath = Path.GetFullPath(path);
            string? projectRoot = TryResolveProjectRoot(fullPath);
            if (!string.IsNullOrWhiteSpace(projectRoot))
            {
                return BuildBackupDirectoryForProjectRoot(projectRoot);
            }

            string fallbackRoot = Path.GetDirectoryName(fullPath) ?? Directory.GetCurrentDirectory();
            return BuildBackupDirectoryForProjectRoot(fallbackRoot);
        }

        public static string BuildBackupDirectoryForProjectRoot(string projectRoot)
        {
            if (string.IsNullOrWhiteSpace(projectRoot))
            {
                throw new ArgumentException("Project root is empty.", nameof(projectRoot));
            }

            return Path.Combine(Path.GetFullPath(projectRoot), "Library", BackupDirectoryName, "backups");
        }

        private static T Run<T>(string path, string commandName, Func<T> action, BackupMode mode, FileBackupTransactionOptions? options)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("Backup target path is empty.", nameof(path));
            }

            if (action == null)
            {
                throw new ArgumentNullException(nameof(action));
            }

            options ??= new FileBackupTransactionOptions();
            string backupId = CreateBackupId(options);
            BackupEntry[] entries = BuildEntries(path, backupId, options.BackupRoot);
            var backedUpEntries = new List<BackupEntry>(entries.Length);

            try
            {
                for (int index = 0; index < entries.Length; index++)
                {
                    BackupEntry entry = entries[index];
                    if (!entry.WasPresent)
                    {
                        continue;
                    }

                    CreateBackup(entry, mode, options);
                    backedUpEntries.Add(entry);
                }

                if (mode == BackupMode.Move && backedUpEntries.Count > 0)
                {
                    options.Refresh?.Invoke();
                }
            }
            catch (Exception exception)
            {
                if (mode == BackupMode.Move && backedUpEntries.Count > 0)
                {
                    try
                    {
                        RestoreEntries(backedUpEntries, mode);
                    }
                    catch (Exception restoreException)
                    {
                        options.Refresh?.Invoke();
                        throw CreateRestoreFailedException(path, commandName, backedUpEntries, exception, restoreException);
                    }
                }
                else
                {
                    CleanupBackups(backedUpEntries, options);
                }

                options.Refresh?.Invoke();
                throw CreateBackupFailedException(path, commandName, entries, exception);
            }

            try
            {
                T result = action();
                CleanupBackups(backedUpEntries, options);
                if (mode == BackupMode.Move && backedUpEntries.Count > 0)
                {
                    options.Refresh?.Invoke();
                }

                return result;
            }
            catch (Exception actionException)
            {
                try
                {
                    RestoreEntries(entries, mode);
                    CleanupBackups(backedUpEntries, options);
                    options.Refresh?.Invoke();
                }
                catch (Exception restoreException)
                {
                    options.Refresh?.Invoke();
                    throw CreateRestoreFailedException(path, commandName, backedUpEntries, actionException, restoreException);
                }

                throw;
            }
        }

        private static string CreateBackupId(FileBackupTransactionOptions options)
        {
            string? requestedId = options.BackupIdFactory == null ? null : options.BackupIdFactory();
            if (!string.IsNullOrWhiteSpace(requestedId))
            {
                return requestedId.Trim();
            }

            return Guid.NewGuid().ToString("N").Substring(0, 8);
        }

        private static BackupEntry[] BuildEntries(string path, string backupId, string? backupRoot)
        {
            return new[]
            {
                BuildEntry(path, backupId, backupRoot),
                BuildEntry(path + ".meta", backupId, backupRoot),
            };
        }

        private static BackupEntry BuildEntry(string path, string backupId, string? backupRoot)
        {
            bool isDirectory = Directory.Exists(path);
            bool wasPresent = isDirectory || File.Exists(path);
            DateTime? originalLastWriteTimeUtc = null;
            if (wasPresent)
            {
                originalLastWriteTimeUtc = isDirectory
                    ? Directory.GetLastWriteTimeUtc(path)
                    : File.GetLastWriteTimeUtc(path);
            }

            return new BackupEntry(path, BuildBackupPath(path, backupId, backupRoot), wasPresent, isDirectory, originalLastWriteTimeUtc);
        }

        private static void CreateBackup(BackupEntry entry, BackupMode mode, FileBackupTransactionOptions options)
        {
            string? backupDirectory = Path.GetDirectoryName(entry.BackupPath);
            if (!string.IsNullOrWhiteSpace(backupDirectory))
            {
                Directory.CreateDirectory(backupDirectory);
            }

            if (PathExists(entry.BackupPath))
            {
                options.WarningSink?.Invoke("Overwriting a stale bridge backup: " + entry.BackupPath);
                DeletePath(entry.BackupPath);
            }

            if (mode == BackupMode.Move)
            {
                MovePath(entry.OriginalPath, entry.BackupPath, entry.IsDirectory);
            }
            else if (entry.IsDirectory)
            {
                CopyDirectory(entry.OriginalPath, entry.BackupPath);
            }
            else
            {
                File.Copy(entry.OriginalPath, entry.BackupPath, overwrite: true);
            }
        }

        private static void RestoreEntries(IReadOnlyList<BackupEntry> entries, BackupMode mode)
        {
            for (int index = 0; index < entries.Count; index++)
            {
                BackupEntry entry = entries[index];
                if (PathExists(entry.OriginalPath))
                {
                    DeletePath(entry.OriginalPath);
                }

                if (!entry.WasPresent)
                {
                    continue;
                }

                if (mode == BackupMode.Move)
                {
                    MovePath(entry.BackupPath, entry.OriginalPath, entry.IsDirectory);
                }
                else if (entry.IsDirectory)
                {
                    CopyDirectory(entry.BackupPath, entry.OriginalPath);
                }
                else
                {
                    File.Copy(entry.BackupPath, entry.OriginalPath, overwrite: true);
                }

                RestoreLastWriteTime(entry);
            }
        }

        private static void CleanupBackups(List<BackupEntry> backedUpEntries, FileBackupTransactionOptions options)
        {
            for (int index = 0; index < backedUpEntries.Count; index++)
            {
                BackupEntry entry = backedUpEntries[index];
                try
                {
                    if (PathExists(entry.BackupPath))
                    {
                        DeletePath(entry.BackupPath);
                    }
                }
                catch (Exception exception)
                {
                    options.WarningSink?.Invoke("Bridge backup cleanup failed: " + entry.BackupPath + " (" + exception.Message + ")");
                }
            }
        }

        private static FileBackupTransactionException CreateBackupFailedException(
            string path,
            string commandName,
            BackupEntry[] entries,
            Exception exception)
        {
            string message = commandName + " backup failed: " + path;
            string details = BuildDetails(entries, exception);
            return new FileBackupTransactionException(ProtocolConstants.ErrorBackupFailed, message, details, exception);
        }

        private static FileBackupTransactionException CreateRestoreFailedException(
            string path,
            string commandName,
            List<BackupEntry> backedUpEntries,
            Exception actionException,
            Exception restoreException)
        {
            string backupLocations = BuildBackupLocationMessage(backedUpEntries);
            string message = commandName
                + " failed, then restoring the backup failed. Manual recovery is required. Backup location: "
                + backupLocations
                + ". Cause: "
                + restoreException.Message;
            string details = "original: " + actionException + Environment.NewLine + "restore: " + restoreException;
            return new FileBackupTransactionException(ProtocolConstants.ErrorBackupRestoreFailed, message, details, restoreException);
        }

        private static string BuildDetails(BackupEntry[] entries, Exception exception)
        {
            return "backups: " + BuildBackupLocationMessage(entries) + Environment.NewLine + exception;
        }

        private static string BuildBackupLocationMessage(IReadOnlyList<BackupEntry> entries)
        {
            if (entries.Count == 0)
            {
                return "(no backup was created)";
            }

            var locations = new List<string>(entries.Count);
            for (int index = 0; index < entries.Count; index++)
            {
                BackupEntry entry = entries[index];
                if (entry.WasPresent)
                {
                    locations.Add(entry.BackupPath);
                }
            }

            return locations.Count == 0 ? "(no backup was created)" : string.Join(", ", locations.ToArray());
        }

        private static bool PathExists(string path)
        {
            return File.Exists(path) || Directory.Exists(path);
        }

        private static void DeletePath(string path)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
                return;
            }

            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }

        private static void CopyDirectory(string sourcePath, string destinationPath)
        {
            Directory.CreateDirectory(destinationPath);
            foreach (string directoryPath in Directory.GetDirectories(sourcePath, "*", SearchOption.AllDirectories))
            {
                string relativePath = directoryPath.Substring(sourcePath.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                Directory.CreateDirectory(Path.Combine(destinationPath, relativePath));
            }

            foreach (string filePath in Directory.GetFiles(sourcePath, "*", SearchOption.AllDirectories))
            {
                string relativePath = filePath.Substring(sourcePath.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string destinationFile = Path.Combine(destinationPath, relativePath);
                string? destinationDirectory = Path.GetDirectoryName(destinationFile);
                if (!string.IsNullOrWhiteSpace(destinationDirectory))
                {
                    Directory.CreateDirectory(destinationDirectory);
                }

                File.Copy(filePath, destinationFile, overwrite: true);
            }
        }

        private static void MovePath(string sourcePath, string destinationPath, bool isDirectory)
        {
            if (isDirectory)
            {
                Directory.Move(sourcePath, destinationPath);
            }
            else
            {
                File.Move(sourcePath, destinationPath);
            }
        }

        private static void RestoreLastWriteTime(BackupEntry entry)
        {
            if (!entry.OriginalLastWriteTimeUtc.HasValue || !PathExists(entry.OriginalPath))
            {
                return;
            }

            if (entry.IsDirectory)
            {
                Directory.SetLastWriteTimeUtc(entry.OriginalPath, entry.OriginalLastWriteTimeUtc.Value);
            }
            else
            {
                File.SetLastWriteTimeUtc(entry.OriginalPath, entry.OriginalLastWriteTimeUtc.Value);
            }
        }

        private static string BuildBackupFileName(string path, string backupId)
        {
            string nameSource = BuildBackupNameSource(path);
            string sanitized = SanitizeBackupFileName(nameSource);
            return sanitized + "__" + backupId;
        }

        private static string BuildBackupNameSource(string path)
        {
            string fullPath = Path.GetFullPath(path);
            string normalizedFullPath = NormalizeSeparators(fullPath);
            string? projectRoot = TryResolveProjectRoot(fullPath);
            if (!string.IsNullOrWhiteSpace(projectRoot))
            {
                string normalizedProjectRoot = NormalizeSeparators(projectRoot).TrimEnd('/');
                if (normalizedFullPath.Length > normalizedProjectRoot.Length
                    && normalizedFullPath.StartsWith(normalizedProjectRoot + "/", StringComparison.Ordinal))
                {
                    return normalizedFullPath.Substring(normalizedProjectRoot.Length + 1);
                }
            }

            return Path.GetFileName(fullPath);
        }

        private static string SanitizeBackupFileName(string value)
        {
            string normalized = NormalizeSeparators(value).Replace("/", "__");
            var chars = normalized.ToCharArray();
            for (int index = 0; index < chars.Length; index++)
            {
                char c = chars[index];
                if (c == '\\' || c == ':' || Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0)
                {
                    chars[index] = '_';
                }
            }

            return new string(chars);
        }

        private static string? TryResolveProjectRoot(string path)
        {
            string normalized = NormalizeSeparators(Path.GetFullPath(path)).TrimEnd('/');
            const string assetsSegment = "/Assets/";
            int assetsIndex = normalized.LastIndexOf(assetsSegment, StringComparison.Ordinal);
            if (assetsIndex >= 0)
            {
                return normalized.Substring(0, assetsIndex);
            }

            const string assetsSuffix = "/Assets";
            if (normalized.EndsWith(assetsSuffix, StringComparison.Ordinal))
            {
                return normalized.Substring(0, normalized.Length - assetsSuffix.Length);
            }

            return null;
        }

        private static string NormalizeSeparators(string path)
        {
            return path.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');
        }

        private readonly struct BackupEntry
        {
            public BackupEntry(string originalPath, string backupPath, bool wasPresent, bool isDirectory, DateTime? originalLastWriteTimeUtc)
            {
                OriginalPath = originalPath;
                BackupPath = backupPath;
                WasPresent = wasPresent;
                IsDirectory = isDirectory;
                OriginalLastWriteTimeUtc = originalLastWriteTimeUtc;
            }

            public string OriginalPath { get; }
            public string BackupPath { get; }
            public bool WasPresent { get; }
            public bool IsDirectory { get; }
            public DateTime? OriginalLastWriteTimeUtc { get; }
        }
    }
}
