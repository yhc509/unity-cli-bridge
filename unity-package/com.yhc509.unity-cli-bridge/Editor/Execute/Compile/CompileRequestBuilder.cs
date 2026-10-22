#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityCli.Protocol;
using UnityEditor;
using UnityEngine;

namespace UnityCliBridge.Bridge.Editor
{
    /// <summary>
    /// Gathers the Editor-side compile inputs: the project's C# language version, the active
    /// scripting defines, and every loaded assembly with a file location as a reference.
    /// </summary>
    internal static class CompileRequestBuilder
    {
        public static CompileRequest Build(string source)
        {
            // The cache key stays a hash of the wrapped source (see ExecuteCodeHandler).
            // Language version and scripting defines are fixed for the domain, so a cache hit
            // still skips this compile. The rsp's -define/-unsafe/-nowarn are only used by the
            // Roslyn host; CodeDOM keeps its options.
            string responseFile = ReadProjectResponseFile();
            return new CompileRequest(
                source,
                CSharpLanguageVersion.Resolve(responseFile, ReadAdditionalCompilerArguments()),
                ReadActiveScriptingDefines(),
                CollectLoadedAssemblyReferences(),
                ProjectCompilerOptions.Parse(responseFile));
        }

        internal static string[] CollectLoadedAssemblyReferences()
        {
            var references = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Assembly loadedAssembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    if (!string.IsNullOrWhiteSpace(loadedAssembly.Location)
                        && seen.Add(loadedAssembly.Location))
                    {
                        references.Add(loadedAssembly.Location);
                    }
                }
                catch
                {
                }
            }

            return references.ToArray();
        }

        private static string[] ReadActiveScriptingDefines()
        {
            try
            {
                string[] defines = EditorUserBuildSettings.activeScriptCompilationDefines;
                return defines ?? Array.Empty<string>();
            }
            catch (Exception)
            {
                return Array.Empty<string>();
            }
        }

        private static string ReadProjectResponseFile()
        {
            string responseFile = ReadTextIfExists(Path.Combine(Application.dataPath, "csc.rsp"));
            if (string.IsNullOrEmpty(responseFile))
            {
                responseFile = ReadTextIfExists(Path.Combine(Application.dataPath, "mcs.rsp"));
            }

            return responseFile;
        }

        private static string ReadTextIfExists(string path)
        {
            try
            {
                return File.Exists(path) ? File.ReadAllText(path) : string.Empty;
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        private static string[] ReadAdditionalCompilerArguments()
        {
            try
            {
                BuildTargetGroup group = EditorUserBuildSettings.selectedBuildTargetGroup;
                if (group == BuildTargetGroup.Unknown)
                {
                    return Array.Empty<string>();
                }

                // 2021.3 exposes the build-target-group overload. Later editors mark it obsolete
                // in favor of NamedBuildTarget, but the method still returns the same arguments.
#pragma warning disable CS0618
                string[] arguments = PlayerSettings.GetAdditionalCompilerArgumentsForGroup(group);
#pragma warning restore CS0618
                return arguments ?? Array.Empty<string>();
            }
            catch (Exception)
            {
                return Array.Empty<string>();
            }
        }
    }
}
