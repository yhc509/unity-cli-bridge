#nullable enable
using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Microsoft.CSharp;
using UnityCli.Protocol;

namespace UnityCliBridge.Bridge.Editor
{
    /// <summary>
    /// Compiles through Unity's CodeDOM host, which is mcs. CodeDOM compilation costs ~0.5 s per
    /// call; <c>ExecuteCodeHandler</c> caches the result so a repeated snippet skips it.
    /// </summary>
    internal sealed class CodeDomBackend : ICompileBackend
    {
        public const string BackendId = CompileBackendIds.CodeDom;

        public string Id
        {
            get { return BackendId; }
        }

        public CompileResult TryCompile(CompileRequest request)
        {
            using var provider = new CSharpCodeProvider();
            // Unity's CodeDOM host is mcs, which rejects /langversion:9.0. ToCompilerOption
            // maps that project default (and anything above 7.2) to /langversion:latest.
            // /define always includes UNITY_ASSERTIONS so Debug.Assert and Debug.LogAssertion
            // are kept, plus the Editor's active scripting define symbols.
            string compilerOptions = CSharpLanguageVersion.ToCompilerOption(request.LanguageVersion)
                + " "
                + ExecuteCompilerDefines.BuildDefineOption(request.ScriptingDefines);
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
    }
}
