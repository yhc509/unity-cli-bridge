using System;
using System.IO;

namespace UnityCli.Protocol
{
    public static class AssetPathUtility
    {
        public static string Normalize(string? path, bool allowPackages = false)
        {
            string normalizedPath = NormalizeSlashes(path);
            EnsureRelativeAssetPath(normalizedPath);
            EnsureSafeSegments(normalizedPath);

            if (IsAssetsPath(normalizedPath) || (allowPackages && IsPackagesPath(normalizedPath)))
            {
                return normalizedPath;
            }

            throw new InvalidOperationException(
                allowPackages
                    ? "Asset paths must use the `Assets/...` or `Packages/...` form."
                    : "Asset paths must use the `Assets/...` form.");
        }

        public static bool IsAssetsPath(string? path)
        {
            return HasRootPrefix(NormalizeSlashes(path), "Assets");
        }

        public static bool IsPackagesPath(string? path)
        {
            return HasRootPrefix(NormalizeSlashes(path), "Packages");
        }

        public static bool IsPhysicalPathWithinRoot(string physicalPath, string rootPath)
        {
            StringComparison comparison = Path.DirectorySeparatorChar == '\\'
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;

            if (string.Equals(physicalPath, rootPath, comparison))
            {
                return true;
            }

            string rootWithSeparator = EnsureTrailingDirectorySeparator(rootPath);
            return physicalPath.StartsWith(rootWithSeparator, comparison);
        }

        private static string NormalizeSlashes(string? path)
        {
            string normalizedPath = path == null ? string.Empty : path.Replace('\\', '/').Trim();
            return normalizedPath.TrimEnd('/');
        }

        private static void EnsureRelativeAssetPath(string path)
        {
            if (path.StartsWith("/", StringComparison.Ordinal) || IsWindowsDriveQualifiedPath(path))
            {
                throw new InvalidOperationException("An asset path cannot be absolute: " + path);
            }
        }

        private static bool IsWindowsDriveQualifiedPath(string path)
        {
            return path.Length >= 2
                && path[1] == ':'
                && ((path[0] >= 'A' && path[0] <= 'Z') || (path[0] >= 'a' && path[0] <= 'z'));
        }

        private static void EnsureSafeSegments(string path)
        {
            string[] segments = path.Split('/');
            for (int index = 0; index < segments.Length; index++)
            {
                string segment = segments[index];
                if (segment.Length == 0 && index > 0)
                {
                    throw new InvalidOperationException("An asset path cannot contain an empty segment: " + path);
                }

                if (string.Equals(segment, ".", StringComparison.Ordinal)
                    || string.Equals(segment, "..", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("An asset path cannot contain a `.` or `..` segment: " + path);
                }
            }
        }

        private static bool HasRootPrefix(string path, string root)
        {
            return string.Equals(path, root, StringComparison.Ordinal)
                || path.StartsWith(root + "/", StringComparison.Ordinal);
        }

        private static string EnsureTrailingDirectorySeparator(string path)
        {
            if (path.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                || path.EndsWith(Path.AltDirectorySeparatorChar.ToString(), StringComparison.Ordinal))
            {
                return path;
            }

            return path + Path.DirectorySeparatorChar;
        }
    }
}
