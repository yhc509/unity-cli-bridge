#nullable enable
using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Microsoft.CSharp;
using UnityCli.Protocol;
using UnityEngine;

namespace UnityCliBridge.Bridge.Editor
{
    internal sealed class ExecuteCodeHandler
    {
        private const string ArgsJsonPlaceholder = "/*__PUC_ARGS_JSON__*/";
        private const string UserCodePlaceholder = "/*__PUC_USER_CODE__*/";
        private const string WrapperTemplate = @"
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using UnityEngine;
using UnityEditor;

public static class PucExecuteWrapper
{
    internal static CancellationToken __pucToken;
    internal static readonly object __pucNoResult = new object();
    internal static object __pucResult = __pucNoResult;

    public static string Execute(CancellationToken token)
    {
        __pucToken = token;
        __pucResult = __pucNoResult;
        var __puc_internal_sb = new System.Text.StringBuilder();
        var __puc_internal_origOut = System.Console.Out;
        var __puc_internal_writer = new System.IO.StringWriter(__puc_internal_sb);
        System.Console.SetOut(__puc_internal_writer);
        try
        {
            string __pucArgsJson = /*__PUC_ARGS_JSON__*/;
            /*__PUC_USER_CODE__*/
        }
        finally
        {
            System.Console.SetOut(__puc_internal_origOut);
        }

        return __puc_internal_sb.ToString();
    }
}";
        public bool CanHandle(string command)
        {
            return string.Equals(command, ProtocolConstants.CommandExecuteCode, StringComparison.Ordinal);
        }

        public string Handle(string command, string argumentsJson)
        {
            ExecuteCodeArgs args = ProtocolJson.Deserialize<ExecuteCodeArgs>(argumentsJson) ?? new ExecuteCodeArgs();
            if (!args.force)
            {
                throw new CommandFailureException(ProtocolConstants.ErrorExecuteForceRequired, "Running code requires --force.");
            }

            if (string.IsNullOrWhiteSpace(args.code))
            {
                throw new CommandFailureException("INVALID_ARGS", "The code to run is empty.");
            }

            int effectiveTimeoutMs = ResolveEffectiveTimeoutMs(args.timeoutMs);

            using var cts = new System.Threading.CancellationTokenSource();
            cts.CancelAfter(effectiveTimeoutMs);

            try
            {
                string wrappedCode = BuildWrappedCode(args.code, args.argumentsJson);
                Assembly assembly = CompileCode(wrappedCode);
                Type? wrapperType = assembly.GetType("PucExecuteWrapper");
                if (wrapperType == null)
                {
                    throw new CommandFailureException("EXECUTE_FAILED", "Could not find the wrapper type.");
                }

                MethodInfo? executeMethod = wrapperType.GetMethod("Execute", BindingFlags.Public | BindingFlags.Static);
                if (executeMethod == null)
                {
                    throw new CommandFailureException("EXECUTE_FAILED", "Could not find the Execute method.");
                }

                var logEntries = new List<string>();
                void OnLogMessageReceived(string condition, string stackTrace, LogType type)
                {
                    if (!string.IsNullOrWhiteSpace(condition))
                    {
                        logEntries.Add(condition);
                    }
                }

                Application.logMessageReceived += OnLogMessageReceived;
                try
                {
                    string consoleOutput = (string)(executeMethod.Invoke(null, new object[] { cts.Token }) ?? string.Empty);
                    string output = MergeOutput(consoleOutput, logEntries);
                    object? noResult = wrapperType
                        .GetField("__pucNoResult", BindingFlags.NonPublic | BindingFlags.Static)
                        ?.GetValue(null);
                    object? rawResult = wrapperType
                        .GetField("__pucResult", BindingFlags.NonPublic | BindingFlags.Static)
                        ?.GetValue(null);
                    bool hasResult = !ReferenceEquals(rawResult, noResult);

                    return ProtocolJson.Serialize(new ExecuteCodePayload
                    {
                        output = output,
                        success = true,
                        hasResult = hasResult,
                        result = hasResult ? ExecuteValueSerializer.Serialize(rawResult, cts.Token) : null,
                    });
                }
                finally
                {
                    Application.logMessageReceived -= OnLogMessageReceived;
                }
            }
            catch (CommandFailureException)
            {
                throw;
            }
            catch (TargetInvocationException ex) when (ex.InnerException is OperationCanceledException)
            {
                throw new CommandFailureException(
                    ProtocolConstants.ErrorExecuteTimeout,
                    $"Code execution did not finish within {effectiveTimeoutMs} ms and was cancelled.");
            }
            catch (OperationCanceledException)
            {
                throw new CommandFailureException(
                    ProtocolConstants.ErrorExecuteTimeout,
                    $"Code execution did not finish within {effectiveTimeoutMs} ms and was cancelled.");
            }
            catch (TargetInvocationException ex) when (ex.InnerException != null)
            {
                throw new CommandFailureException(
                    "EXECUTE_FAILED",
                    ex.InnerException.Message,
                    ex.InnerException.ToString());
            }
            catch (Exception ex)
            {
                throw new CommandFailureException(
                    "EXECUTE_FAILED",
                    ex.Message,
                    ex.ToString());
            }
        }

        private static int ResolveEffectiveTimeoutMs(int requested)
        {
            if (requested <= 0)
            {
                return ProtocolConstants.DefaultExecuteTimeoutMs;
            }
            if (requested > ProtocolConstants.MaxExecuteTimeoutMs)
            {
                return ProtocolConstants.MaxExecuteTimeoutMs;
            }
            return requested;
        }

        private static string BuildWrappedCode(string userCode, string? argumentsJson)
        {
            string templateBody = UsingDirectiveUtility.StripUsingDirectives(WrapperTemplate, out string[] templateUsings);
            string userCodeBody = UsingDirectiveUtility.StripUsingDirectives(userCode, out string[] userUsings);
            string[] mergedUsings = MergeUsingDirectives(templateUsings, userUsings);
            string usingBlock = string.Join(Environment.NewLine, mergedUsings);
            string effectiveArgumentsJson = string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson;
            string wrappedUserCode = string.Join(
                Environment.NewLine,
                "#line 1 \"user-code\"",
                userCodeBody,
                "#line default");
            string built = templateBody
                .Replace(ArgsJsonPlaceholder, ExecuteCodeStringLiteral.ToCSharpStringLiteral(effectiveArgumentsJson))
                .Replace("            " + UserCodePlaceholder, wrappedUserCode);

            if (built.Contains(UserCodePlaceholder) || built.Contains(ArgsJsonPlaceholder))
            {
                throw new CommandFailureException(
                    "EXECUTE_FAILED",
                    "A wrapper template placeholder was not substituted. Check whether the template indentation changed.");
            }

            return usingBlock + Environment.NewLine + Environment.NewLine + built;
        }

        private static string[] MergeUsingDirectives(IEnumerable<string> templateUsings, IEnumerable<string> userUsings)
        {
            var mergedUsings = new List<string>();
            var seenUsings = new HashSet<string>(StringComparer.Ordinal);

            AppendUsingDirectives(mergedUsings, seenUsings, templateUsings);
            AppendUsingDirectives(mergedUsings, seenUsings, userUsings);

            return mergedUsings.ToArray();
        }

        private static void AppendUsingDirectives(
            ICollection<string> mergedUsings,
            ISet<string> seenUsings,
            IEnumerable<string> usings)
        {
            foreach (string usingDirective in usings)
            {
                if (seenUsings.Add(usingDirective))
                {
                    mergedUsings.Add(usingDirective);
                }
            }
        }

        private static Assembly CompileCode(string code)
        {
            using var provider = new CSharpCodeProvider();
            var parameters = new CompilerParameters
            {
                GenerateInMemory = true,
                GenerateExecutable = false,
                IncludeDebugInformation = false,
                TempFiles = new TempFileCollection(System.IO.Path.GetTempPath(), keepFiles: false),
            };

            var referencedAssemblies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    if (!string.IsNullOrWhiteSpace(assembly.Location)
                        && referencedAssemblies.Add(assembly.Location))
                    {
                        parameters.ReferencedAssemblies.Add(assembly.Location);
                    }
                }
                catch
                {
                }
            }

            CompilerResults result = provider.CompileAssemblyFromSource(parameters, code);
            if (result.Errors.HasErrors)
            {
                var errors = new StringBuilder();
                foreach (CompilerError error in result.Errors)
                {
                    if (!error.IsWarning)
                    {
                        errors.AppendLine($"Line {error.Line}: {error.ErrorText}");
                    }
                }

                throw new CommandFailureException(
                    "COMPILE_FAILED",
                    "Code compilation failed:\n" + errors.ToString().TrimEnd());
            }

            return result.CompiledAssembly;
        }

        private static string MergeOutput(string consoleOutput, IEnumerable<string> logEntries)
        {
            string[] segments = new[] { consoleOutput.Trim(), string.Join(Environment.NewLine, logEntries).Trim() }
                .Where(segment => !string.IsNullOrWhiteSpace(segment))
                .ToArray();

            return segments.Length == 0 ? string.Empty : string.Join(Environment.NewLine, segments);
        }
    }
}
