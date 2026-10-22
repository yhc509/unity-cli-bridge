#nullable enable
using System;
using System.Collections.Generic;
using UnityCli.Protocol;
#if UNITY_5_3_OR_NEWER
using System.CodeDom.Compiler;
using System.Reflection;
using System.Text;
using Microsoft.CSharp;
#endif

namespace UnityCliBridge.Bridge.Editor
{
    /// <summary>
    /// Compiles through Unity's CodeDOM host, which is mcs. CodeDOM compilation costs ~0.5 s per
    /// call; <c>ExecuteCodeHandler</c> caches the result so a repeated snippet skips it.
    /// </summary>
    internal sealed class CodeDomBackend : ICompileBackend
    {
        public const string BackendId = "codedom";

        private const string MscorlibFileName = "mscorlib.dll";

        public string Id
        {
            get { return BackendId; }
        }

        public CompileResult TryCompile(CompileRequest request)
        {
            string compilerOptions = BuildCompilerOptions(request);
#if UNITY_5_3_OR_NEWER
            return CompileWithMcs(request, compilerOptions);
#else
            // Linked into the dotnet tests for BuildCompilerOptions. mcs itself runs only in the Editor.
            return CompileResult.InfrastructureFailure(
                BackendId,
                compilerOptions,
                null,
                null,
                "mcs is hosted by the Unity Editor.");
#endif
        }

        /// <summary>
        /// mcs arguments assigned to <c>CompilerParameters.CompilerOptions</c>.
        /// An <c>mscorlib.dll</c> already in the reference set means Unity's unityjit/unityaot
        /// corlib is explicit, so <c>-nostdlib</c> stops mcs from also loading MonoBleedingEdge's
        /// own mscorlib (CS0433 on Windows). That reference is not removed. With no mscorlib
        /// reference, mcs keeps its default corlib.
        /// </summary>
        internal static string BuildCompilerOptions(CompileRequest request)
        {
            // Unity's CodeDOM host is mcs, which rejects /langversion:9.0. ToCompilerOption
            // maps that project default (and anything above 7.2) to /langversion:latest.
            // /define always includes UNITY_ASSERTIONS so Debug.Assert and Debug.LogAssertion
            // are kept, plus the Editor's active scripting define symbols.
            string compilerOptions = CSharpLanguageVersion.ToCompilerOption(request.LanguageVersion)
                + " "
                + ExecuteCompilerDefines.BuildDefineOption(request.ScriptingDefines);
            if (ReferencesIncludeMscorlib(request.ReferencePaths))
            {
                compilerOptions += " -nostdlib";
            }

            return compilerOptions;
        }

        private static bool ReferencesIncludeMscorlib(IReadOnlyList<string> referencePaths)
        {
            for (int index = 0; index < referencePaths.Count; index++)
            {
                if (IsMscorlibReference(referencePaths[index]))
                {
                    return true;
                }
            }

            return false;
        }

        // Both separators: Assembly.Location on Windows uses '\', and the dotnet tests run on
        // every OS. Path.GetFileName would not split a Windows path on Linux or macOS.
        private static bool IsMscorlibReference(string? referencePath)
        {
            if (string.IsNullOrEmpty(referencePath))
            {
                return false;
            }

            int nameStart = 0;
            for (int index = referencePath.Length - 1; index >= 0; index--)
            {
                char character = referencePath[index];
                if (character == '/' || character == '\\')
                {
                    nameStart = index + 1;
                    break;
                }
            }

            int length = referencePath.Length - nameStart;
            return length == MscorlibFileName.Length
                && string.Compare(
                    referencePath,
                    nameStart,
                    MscorlibFileName,
                    0,
                    length,
                    StringComparison.OrdinalIgnoreCase) == 0;
        }

#if UNITY_5_3_OR_NEWER
        private static CompileResult CompileWithMcs(CompileRequest request, string compilerOptions)
        {
            using var provider = new CSharpCodeProvider();
            var parameters = new CompilerParameters
            {
                GenerateInMemory = true,
                GenerateExecutable = false,
                IncludeDebugInformation = false,
                CompilerOptions = compilerOptions,
                TempFiles = new TempFileCollection(System.IO.Path.GetTempPath(), keepFiles: false),
            };

            foreach (string referencePath in request.ReferencePaths)
            {
                parameters.ReferencedAssemblies.Add(referencePath);
            }

            CompilerResults result = provider.CompileAssemblyFromSource(parameters, request.Source);
            if (result.Errors.HasErrors)
            {
                return CompileResult.CompileError(
                    BackendId,
                    compilerOptions,
                    ReadFailureErrors(result),
                    ReadCompilerOutput(result));
            }

            if (!TryGetCompiledAssembly(result, out Assembly? assembly, out string? failure) || assembly == null)
            {
                return CompileResult.InfrastructureFailure(
                    BackendId,
                    compilerOptions,
                    ReadFailureErrors(result),
                    ReadCompilerOutput(result),
                    failure);
            }

            return CompileResult.Success(BackendId, assembly, compilerOptions);
        }

        private static string[] ReadFailureErrors(CompilerResults result)
        {
            string[] errors = ReadCompilerErrors(result, includeWarnings: false);
            if (errors.Length == 0)
            {
                errors = ReadCompilerErrors(result, includeWarnings: true);
            }

            return errors;
        }

        private static bool TryGetCompiledAssembly(CompilerResults result, out Assembly? assembly, out string? failure)
        {
            assembly = null;
            failure = null;
            try
            {
                assembly = result.CompiledAssembly;
            }
            catch (Exception ex)
            {
                failure = ex.Message;
                return false;
            }

            if (assembly == null)
            {
                failure = "Compiler failed to produce the assembly.";
                return false;
            }

            return true;
        }

        private static string[] ReadCompilerErrors(CompilerResults result, bool includeWarnings)
        {
            var lines = new List<string>();
            foreach (CompilerError error in result.Errors)
            {
                if (error.IsWarning && !includeWarnings)
                {
                    continue;
                }

                var line = new StringBuilder();
                if (error.Line > 0)
                {
                    line.Append("Line ");
                    line.Append(error.Line);
                    line.Append(": ");
                }

                if (!string.IsNullOrEmpty(error.ErrorNumber))
                {
                    line.Append(error.ErrorNumber);
                    line.Append(": ");
                }

                if (!string.IsNullOrEmpty(error.ErrorText))
                {
                    line.Append(error.ErrorText);
                }

                if (line.Length > 0)
                {
                    lines.Add(line.ToString());
                }
            }

            return lines.ToArray();
        }

        private static string[] ReadCompilerOutput(CompilerResults result)
        {
            if (result.Output == null || result.Output.Count == 0)
            {
                return Array.Empty<string>();
            }

            var lines = new string[result.Output.Count];
            for (int index = 0; index < result.Output.Count; index++)
            {
                lines[index] = result.Output[index] ?? string.Empty;
            }

            return lines;
        }
#endif
    }
}
