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
        public const string BackendId = "codedom";

        private readonly string? _responseFileDirectory;

        public CodeDomBackend()
            : this(null)
        {
        }

        /// <param name="responseFileDirectory">Where the response file goes; the temp directory when null.</param>
        public CodeDomBackend(string? responseFileDirectory)
        {
            _responseFileDirectory = responseFileDirectory;
        }

        public string Id
        {
            get { return BackendId; }
        }

        public CompileResult TryCompile(CompileRequest request)
        {
            // Unity's CodeDOM host is mcs, which rejects /langversion:9.0. ToCompilerOption
            // maps that project default (and anything above 7.2) to /langversion:latest.
            // /define always includes UNITY_ASSERTIONS so Debug.Assert and Debug.LogAssertion
            // are kept, plus the Editor's active scripting define symbols.
            string languageOption = CSharpLanguageVersion.ToCompilerOption(request.LanguageVersion);
            string defineOption = ExecuteCompilerDefines.BuildDefineOption(request.ScriptingDefines);
            string compilerOptions = languageOption + " " + defineOption;

            // References and options go through a response file (#220). CodeDOM would otherwise
            // put every reference on the mcs command line as /r:"path", which passes Windows'
            // 32,767-character limit at roughly 260 loaded assemblies. ReferencedAssemblies stays
            // empty: CodeDOM adds no default references either way (it only appends /noconfig, and
            // never /nostdlib), and mcs splices @file arguments in before "--", so mcs sees the
            // same references in the same order with the same options.
            string responseFilePath = ResponseFileWriter.CreateTempPath(
                _responseFileDirectory ?? System.IO.Path.GetTempPath());
            try
            {
                ResponseFileWriter.Write(
                    responseFilePath,
                    ResponseFileWriter.BuildCompilerArguments(request.ReferencePaths, languageOption, defineOption));
                return Compile(request.Source, ResponseFileWriter.ToCommandLineArgument(responseFilePath), compilerOptions);
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
    }
}
