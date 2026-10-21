#nullable enable
using System;
using System.Collections.Generic;

namespace UnityCliBridge.Bridge.Editor
{
    /// <summary>
    /// Backend-neutral compile inputs. <see cref="LanguageVersion"/> is the project's resolved
    /// C# version (see <c>CSharpLanguageVersion.Resolve</c>), not a compiler flag; each backend
    /// maps it and <see cref="ScriptingDefines"/> to its own options.
    /// </summary>
    internal sealed class CompileRequest
    {
        public CompileRequest(
            string source,
            string languageVersion,
            IReadOnlyList<string> scriptingDefines,
            IReadOnlyList<string> referencePaths)
        {
            Source = source ?? throw new ArgumentNullException(nameof(source));
            LanguageVersion = languageVersion ?? string.Empty;
            ScriptingDefines = scriptingDefines ?? Array.Empty<string>();
            ReferencePaths = referencePaths ?? Array.Empty<string>();
        }

        public string Source { get; }

        public string LanguageVersion { get; }

        public IReadOnlyList<string> ScriptingDefines { get; }

        /// <summary>Assembly file paths, deduplicated, in AppDomain load order.</summary>
        public IReadOnlyList<string> ReferencePaths { get; }
    }
}
