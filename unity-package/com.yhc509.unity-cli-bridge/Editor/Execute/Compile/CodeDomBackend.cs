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
        public const string BackendId = CompileBackendIds.CodeDom;

        private const string MscorlibFileName = "mscorlib.dll";

        public CodeDomBackend()
#if UNITY_5_3_OR_NEWER
            : this(null)
#endif
        {
        }

#if UNITY_5_3_OR_NEWER
        private readonly string? _responseFileDirectory;

        /// <param name="responseFileDirectory">Where the response file goes; the temp directory when null.</param>
        public CodeDomBackend(string? responseFileDirectory)
        {
            _responseFileDirectory = responseFileDirectory;
        }
#endif

        public string Id
        {
            get { return BackendId; }
        }

        public CompileResult TryCompile(CompileRequest request)
        {
            string compilerOptions = BuildCompilerOptions(request);
#if UNITY_5_3_OR_NEWER
            return CompileWithResponseFile(request, compilerOptions);
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
        /// Effective mcs options: <c>/langversion</c>, <c>/define</c>, and <c>-nostdlib</c> when a
        /// reference is already <c>mscorlib.dll</c>. Written into the response file (see
        /// <see cref="BuildMcsInvocation"/>) and reported on the compile result. The command line
        /// itself is only the <c>@rsp</c> argument.
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

        /// <summary>
        /// Command line plus response-file arguments actually handed to mcs. References and
        /// <see cref="BuildCompilerOptions"/> (including a single <c>-nostdlib</c> when an
        /// mscorlib is referenced) stay in the response file so the command line stays the
        /// <c>@rsp</c> argument (#220).
        /// </summary>
        internal static void BuildMcsInvocation(
            CompileRequest request,
            string responseFilePath,
            out string commandLine,
            out IReadOnlyList<string> responseFileArguments)
        {
            commandLine = ResponseFileWriter.ToCommandLineArgument(responseFilePath);
            string[] optionArguments = BuildCompilerOptions(request).Split(
                new[] { ' ' },
                StringSplitOptions.RemoveEmptyEntries);
            responseFileArguments = ResponseFileWriter.BuildCompilerArguments(
                request.ReferencePaths,
                optionArguments);
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
        private CompileResult CompileWithResponseFile(CompileRequest request, string compilerOptions)
        {
            // References and options go through a response file (#220). CodeDOM would otherwise
            // put every reference on the mcs command line as /r:"path", which passes Windows'
            // 32,767-character limit at roughly 260 loaded assemblies. ReferencedAssemblies stays
            // empty: CodeDOM adds no default references either way (it only appends /noconfig).
            // -nostdlib, when BuildCompilerOptions added it, is one response-file argument and is
            // not repeated on the command line. mcs splices @file arguments in before "--".
            string responseFilePath = ResponseFileWriter.CreateTempPath(
                _responseFileDirectory ?? System.IO.Path.GetTempPath());
            try
            {
                BuildMcsInvocation(
                    request,
                    responseFilePath,
                    out string commandLine,
                    out IReadOnlyList<string> responseFileArguments);
                ResponseFileWriter.Write(responseFilePath, responseFileArguments);
                return Compile(request.Source, commandLine, compilerOptions);
            }
            finally
            {
                ResponseFileWriter.TryDelete(responseFilePath);
            }
        }

        /// <param name="commandLineOptions">What CodeDOM puts on the mcs command line (the <c>@rsp</c> argument).</param>
        /// <param name="compilerOptions">The effective options, reported in results and failure messages.</param>
        private static CompileResult Compile(string source, string commandLineOptions, string compilerOptions)
        {
            using var provider = new CSharpCodeProvider();
            var parameters = new CompilerParameters
            {
                GenerateInMemory = true,
                GenerateExecutable = false,
                IncludeDebugInformation = false,
                CompilerOptions = commandLineOptions,
                TempFiles = new TempFileCollection(System.IO.Path.GetTempPath(), keepFiles: false),
            };

            CompilerResults result = provider.CompileAssemblyFromSource(parameters, source);
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
