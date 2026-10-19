#nullable enable
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
#if !UNITY_5_3_OR_NEWER
using System.Text.Json;
#endif

namespace UnityCli.Protocol
{
    public static class InstanceRegistryFile
    {
        private const int MaxRetryCount = 10;
        private const int RetryDelayMs = 30;
        private const int StaleLockSeconds = 30;

        public static InstanceRegistry Load(string filePath)
        {
            string fullPath = EnsureDirectory(filePath);
            Exception? lastException = null;

            for (int attempt = 0; attempt < MaxRetryCount; attempt++)
            {
                try
                {
                    return LoadUnlocked(fullPath);
                }
                catch (IOException exception)
                {
                    lastException = exception;
                    Thread.Sleep((attempt + 1) * RetryDelayMs);
                }
                catch (Exception exception) when (IsJsonParseException(exception))
                {
                    lastException = exception;
                    Thread.Sleep((attempt + 1) * RetryDelayMs);
                }
            }

            if (lastException != null)
            {
                throw lastException;
            }

            return new InstanceRegistry();
        }

        public static void Save(string filePath, InstanceRegistry registry)
        {
            if (registry == null)
            {
                throw new ArgumentNullException("registry");
            }

            string fullPath = EnsureDirectory(filePath);
            WithExclusiveLock(fullPath, () => WriteAtomically(fullPath, NormalizeRegistry(registry)));
        }

        public static void Update(string filePath, Func<InstanceRegistry, InstanceRegistry> update)
        {
            if (update == null)
            {
                throw new ArgumentNullException("update");
            }

            string fullPath = EnsureDirectory(filePath);
            WithExclusiveLock(fullPath, () =>
            {
                InstanceRegistry current = LoadUnlocked(fullPath);
                InstanceRegistry next = NormalizeRegistry(update(current));
                WriteAtomically(fullPath, next);
            });
        }

        public static string GetTokenSidecarPath(string registryFilePath, string projectHash)
        {
            if (string.IsNullOrWhiteSpace(projectHash))
            {
                return string.Empty;
            }

            if (!IsValidSidecarProjectHash(projectHash))
            {
                return string.Empty;
            }

            string registryDirectory = Path.GetDirectoryName(Path.GetFullPath(registryFilePath)) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(registryDirectory))
            {
                return string.Empty;
            }

            string tokenDirectory = Path.GetFullPath(Path.Combine(registryDirectory, "tokens"));
            string fullPath = Path.GetFullPath(Path.Combine(tokenDirectory, projectHash + ".token"));
            StringComparison comparison = Path.DirectorySeparatorChar == '\\'
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            string tokenDirectoryWithSeparator = tokenDirectory.EndsWith(Path.DirectorySeparatorChar.ToString(), comparison)
                ? tokenDirectory
                : tokenDirectory + Path.DirectorySeparatorChar;

            if (!fullPath.StartsWith(tokenDirectoryWithSeparator, comparison))
            {
                return string.Empty;
            }

            return fullPath;
        }

        private static bool IsValidSidecarProjectHash(string projectHash)
        {
            if (string.IsNullOrEmpty(projectHash) || projectHash.Length < 12)
            {
                return false;
            }

            for (int index = 0; index < 12; index++)
            {
                if (!IsHexDigit(projectHash[index]))
                {
                    return false;
                }
            }

            if (projectHash.Length == 12)
            {
                return true;
            }

            if (projectHash[12] != '-' || projectHash.Length == 13)
            {
                return false;
            }

            for (int index = 13; index < projectHash.Length; index++)
            {
                char character = projectHash[index];
                if (character < '0' || character > '9')
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsHexDigit(char character)
        {
            return (character >= '0' && character <= '9')
                || (character >= 'a' && character <= 'f')
                || (character >= 'A' && character <= 'F');
        }

        public static void WriteTokenSidecar(string registryFilePath, string projectHash, string token)
        {
            string fullPath = GetTokenSidecarPath(registryFilePath, projectHash);
            if (string.IsNullOrWhiteSpace(fullPath))
            {
                return;
            }

            string? directory = Path.GetDirectoryName(fullPath);
            if (string.IsNullOrWhiteSpace(directory))
            {
                return;
            }

            Directory.CreateDirectory(directory);
            TryApplyOwnerOnlyDirectoryMode(directory);

            string tempPath = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";

            try
            {
                using (var stream = CreateOwnerOnlyTempFileForAtomicWrite(tempPath))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                {
                    writer.Write(token ?? string.Empty);
                }

                if (File.Exists(fullPath))
                {
                    File.Replace(tempPath, fullPath, null);
                }
                else
                {
                    File.Move(tempPath, fullPath);
                }

                TryApplyOwnerOnlyFileMode(fullPath);
            }
            finally
            {
                TryDeleteFile(tempPath);
            }
        }

        /// <summary>
        /// Writes the sidecar only when its content differs from <paramref name="token"/>.
        /// A crashed/killed editor leaves a stale sidecar behind (normal exit deletes it);
        /// the next session owns the listener, so its live token must win or every CLI
        /// call fails UNAUTHORIZED until manual cleanup.
        /// </summary>
        public static void EnsureTokenSidecar(string registryFilePath, string projectHash, string token)
        {
            string existing = ReadTokenSidecar(registryFilePath, projectHash);
            if (string.Equals(existing, token, StringComparison.Ordinal))
            {
                return;
            }

            WriteTokenSidecar(registryFilePath, projectHash, token);
        }

        public static string ReadTokenSidecar(string registryFilePath, string projectHash)
        {
            string fullPath = GetTokenSidecarPath(registryFilePath, projectHash);
            if (string.IsNullOrWhiteSpace(fullPath))
            {
                return string.Empty;
            }

            try
            {
                using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                return reader.ReadToEnd().Trim();
            }
            catch
            {
                return string.Empty;
            }
        }

        public static void DeleteTokenSidecar(string registryFilePath, string projectHash)
        {
            string fullPath = GetTokenSidecarPath(registryFilePath, projectHash);
            if (string.IsNullOrWhiteSpace(fullPath))
            {
                return;
            }

            TryDeleteFile(fullPath);
        }

        private static void WithExclusiveLock(string fullPath, Action action)
        {
            string lockPath = fullPath + ".lock";
            IOException? lastException = null;

            for (int attempt = 0; attempt < MaxRetryCount; attempt++)
            {
                FileStream? lockStream;
                bool acquired = TryAcquireLock(lockPath, out lockStream);

                if (acquired && lockStream != null)
                {
                    try
                    {
                        action();
                        return;
                    }
                    catch (IOException exception)
                    {
                        lastException = exception;
                    }
                    finally
                    {
                        ReleaseLock(lockStream, lockPath);
                    }
                }
                else
                {
                    lastException = new IOException("Failed to acquire registry lock: " + lockPath);
                    TryReclaimStaleLock(lockPath);
                }

                Thread.Sleep((attempt + 1) * RetryDelayMs);
            }

            if (lastException != null)
            {
                throw lastException;
            }
        }

        private static bool TryAcquireLock(string lockPath, out FileStream? lockStream)
        {
            FileStream? stream = null;
            bool acquired = false;

            try
            {
                stream = new FileStream(lockPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
                acquired = true;
                // Unity Mono runtime lacks Environment.ProcessId (.NET 5+ only); Process.GetCurrentProcess().Id stays portable.
                int processId = Process.GetCurrentProcess().Id;
                string content = processId.ToString(CultureInfo.InvariantCulture)
                    + Environment.NewLine
                    + DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)
                    + Environment.NewLine;
                byte[] bytes = Encoding.UTF8.GetBytes(content);
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush();
                lockStream = stream;
                return true;
            }
            catch (IOException)
            {
                if (acquired && stream != null)
                {
                    ReleaseLock(stream, lockPath);
                }

                lockStream = null;
                return false;
            }
        }

        private static bool TryReclaimStaleLock(string lockPath)
        {
            FileStream stream;

            try
            {
                // FileShare.Delete lets us rename the file aside on Windows while still
                // holding the stream; live acquire callers request FileShare.None, which
                // is incompatible with our share so they remain blocked.
                stream = new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.Delete);
            }
            catch (FileNotFoundException)
            {
                return false;
            }
            catch (DirectoryNotFoundException)
            {
                return false;
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }

            string graveyardPath = lockPath + "." + Guid.NewGuid().ToString("N") + ".stale";

            using (stream)
            {
                if (!IsOpenedLockStale(lockPath, stream))
                {
                    return false;
                }

                try
                {
                    File.Move(lockPath, graveyardPath);
                }
                catch (IOException)
                {
                    return false;
                }
                catch (UnauthorizedAccessException)
                {
                    return false;
                }
            }

            TryDeleteFile(graveyardPath);
            return true;
        }

        private static bool IsOpenedLockStale(string lockPath, FileStream stream)
        {
            DateTime lastWriteTimeUtc;

            try
            {
                lastWriteTimeUtc = File.GetLastWriteTimeUtc(lockPath);
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }

            if (DateTime.UtcNow - lastWriteTimeUtc < TimeSpan.FromSeconds(StaleLockSeconds))
            {
                return false;
            }

            try
            {
                stream.Position = 0;
                using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 1024, leaveOpen: true);
                string? ownerPidLine = reader.ReadLine();
                if (ownerPidLine == null || !int.TryParse(ownerPidLine, NumberStyles.Integer, CultureInfo.InvariantCulture, out int ownerPid))
                {
                    return true;
                }

                string? lockTimestampLine = reader.ReadLine();
                if (lockTimestampLine == null
                    || !DateTimeOffset.TryParseExact(lockTimestampLine, "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset lockTimestamp))
                {
                    return true;
                }

                return IsProcessStale(ownerPid, lockTimestamp);
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        private static bool IsProcessStale(int ownerPid, DateTimeOffset lockTimestamp)
        {
            try
            {
                using Process process = Process.GetProcessById(ownerPid);
                if (process.HasExited)
                {
                    return true;
                }

                try
                {
                    DateTime processStartUtc = process.StartTime.ToUniversalTime();
                    return processStartUtc > lockTimestamp.UtcDateTime.AddSeconds(1);
                }
                catch (InvalidOperationException)
                {
                    return false;
                }
                catch (Win32Exception)
                {
                    return false;
                }
                catch (NotSupportedException)
                {
                    return false;
                }
            }
            catch (ArgumentException)
            {
                return true;
            }
        }

        private static void ReleaseLock(FileStream lockStream, string lockPath)
        {
            try
            {
                lockStream.Dispose();
            }
            finally
            {
                TryDeleteFile(lockPath);
            }
        }

        private static void WriteAtomically(string fullPath, InstanceRegistry registry)
        {
            string tempPath = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";

            try
            {
                using (var stream = CreateOwnerOnlyTempFileForAtomicWrite(tempPath))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                {
                    writer.Write(ProtocolJson.Serialize(registry));
                }

                if (File.Exists(fullPath))
                {
                    File.Replace(tempPath, fullPath, null);
                }
                else
                {
                    File.Move(tempPath, fullPath);
                }

                TryApplyOwnerOnlyFileMode(fullPath);
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
        }

        internal static FileStream CreateOwnerOnlyTempFileForAtomicWrite(string tempPath)
        {
            var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None);
            TryApplyOwnerOnlyFileMode(tempPath);
            return stream;
        }

        private static InstanceRegistry LoadUnlocked(string fullPath)
        {
            if (!File.Exists(fullPath))
            {
                return new InstanceRegistry();
            }

            using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            string json = reader.ReadToEnd();
            if (string.IsNullOrWhiteSpace(json))
            {
                return new InstanceRegistry();
            }

            return NormalizeRegistry(ProtocolJson.Deserialize<InstanceRegistry>(json));
        }

        private static InstanceRegistry NormalizeRegistry(InstanceRegistry? registry)
        {
            if (registry == null)
            {
                registry = new InstanceRegistry();
            }

            if (registry.instances == null)
            {
                registry.instances = Array.Empty<InstanceRecord>();
            }

            if (string.IsNullOrEmpty(registry.activeProjectRoot)
                && !string.IsNullOrWhiteSpace(registry.activeProjectHash))
            {
                string legacyHash = registry.activeProjectHash;
                var match = Array.Find(
                    registry.instances,
                    item => string.Equals(item.projectHash, legacyHash, StringComparison.OrdinalIgnoreCase));
                if (match != null && !string.IsNullOrWhiteSpace(match.projectRoot))
                {
                    registry.activeProjectRoot = match.projectRoot;
                }
            }

            registry.activeProjectHash = null;

            return registry;
        }

        private static void TryApplyOwnerOnlyFileMode(string fullPath)
        {
#if UNITY_5_3_OR_NEWER
            if (Path.DirectorySeparatorChar == '\\' || string.IsNullOrWhiteSpace(fullPath))
            {
                return;
            }

            TryApplyUnixModeWithChmod(fullPath, "600");
#else
            try
            {
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(fullPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }
            }
            catch
            {
            }
#endif
        }

        private static void TryApplyOwnerOnlyDirectoryMode(string fullPath)
        {
#if UNITY_5_3_OR_NEWER
            if (Path.DirectorySeparatorChar == '\\' || string.IsNullOrWhiteSpace(fullPath))
            {
                return;
            }

            TryApplyUnixModeWithChmod(fullPath, "700");
#else
            try
            {
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(fullPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                }
            }
            catch
            {
            }
#endif
        }

#if UNITY_5_3_OR_NEWER
        private static void TryApplyUnixModeWithChmod(string fullPath, string mode)
        {
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = "chmod",
                    Arguments = mode + " " + QuoteProcessArgument(fullPath),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };

                Process? process = null;
                try
                {
                    process = Process.Start(startInfo);
                    if (process == null || !process.WaitForExit(1000))
                    {
                        UnityEngine.Debug.LogWarning("Unity CLI bridge registry permission fix failed: chmod timed out.");
                    }
                    else if (process.ExitCode != 0)
                    {
                        UnityEngine.Debug.LogWarning(string.Format(
                            "Unity CLI bridge registry permission fix failed: chmod exited with {0}.",
                            process.ExitCode));
                    }
                }
                finally
                {
                    if (process != null)
                    {
                        process.Dispose();
                    }
                }
            }
            catch (Exception exception)
            {
                UnityEngine.Debug.LogWarning(string.Format(
                    "Unity CLI bridge registry permission fix failed: {0}",
                    exception.Message));
            }
        }
#endif

#if UNITY_5_3_OR_NEWER
        private static string QuoteProcessArgument(string value)
        {
            return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }
#endif

        private static string EnsureDirectory(string filePath)
        {
            string fullPath = Path.GetFullPath(filePath);
            string? directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            return fullPath;
        }

        private static bool IsJsonParseException(Exception exception)
        {
#if UNITY_5_3_OR_NEWER
            return exception is ArgumentException;
#else
            return exception is JsonException;
#endif
        }

        private static void TryDeleteFile(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
            }
        }
    }
}
