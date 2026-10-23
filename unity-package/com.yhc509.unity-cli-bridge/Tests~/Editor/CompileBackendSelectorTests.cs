#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using NUnit.Framework;
using UnityCli.Protocol;
using UnityEditor;

namespace UnityCliBridge.Bridge.Editor.Tests
{
    /// <summary>The backend chain with fake backends, the default wiring, env scrubbing, and the mcs version under Mono.</summary>
    public sealed class CompileBackendSelectorTests
    {
        private const string Options = "/langversion:9 /define:UNITY_ASSERTIONS";

        private static CompileRequest Request()
        {
            return new CompileRequest("public static class S {}", "9.0", Array.Empty<string>(), Array.Empty<string>());
        }

        [Test]
        public void InfrastructureFailure_FallsThroughToCodeDomWithTheReason()
        {
            var host = new FakeBackend(CompileBackendIds.RoslynHost, _ => CompileResult.InfrastructureFailure(CompileBackendIds.RoslynHost, Options, null, null, "timeout: no reply from the host within 10000 ms"));
            var codeDom = new FakeBackend(CompileBackendIds.CodeDom, _ => CompileResult.Success(CompileBackendIds.CodeDom, typeof(CompileBackendSelectorTests).Assembly, Options));
            var selector = new CompileBackendSelector(codeDom, new ICompileBackend[] { host }, null);

            CompileOutcome outcome = selector.Compile(Request());

            Assert.That(outcome.Compiler, Is.EqualTo("codedom"));
            Assert.That(outcome.FallbackReason, Is.EqualTo("timeout: no reply from the host within 10000 ms"));
            Assert.That(host.Calls, Is.EqualTo(1));
            Assert.That(codeDom.Calls, Is.EqualTo(1));
        }

        [Test]
        public void UserError_StopsTheChain()
        {
            var host = new FakeBackend(CompileBackendIds.RoslynHost, _ => CompileResult.CompileError(CompileBackendIds.RoslynHost, Options, new[] { "Line 1: CS0029: x" }, null));
            var codeDom = new FakeBackend(CompileBackendIds.CodeDom, _ => throw new InvalidOperationException("must not run"));
            var selector = new CompileBackendSelector(codeDom, new ICompileBackend[] { host }, null);

            CompileOutcome outcome = selector.Compile(Request());

            Assert.That(outcome.Compiler, Is.EqualTo("roslyn-host"));
            Assert.That(outcome.FallbackReason, Is.Null);
            Assert.That(codeDom.Calls, Is.EqualTo(0));
            Assert.That(outcome.FormatFailureMessage(), Does.EndWith("Line 1: CS0029: x"));
        }

        [Test]
        public void CodeDomErrorAfterFallback_SaysTheHostWasUnavailable()
        {
            var host = new FakeBackend(CompileBackendIds.RoslynHost, _ => CompileResult.InfrastructureFailure(CompileBackendIds.RoslynHost, Options, null, null, "toolchain-missing: no NetCoreRuntime"));
            var codeDom = new FakeBackend(CompileBackendIds.CodeDom, _ => CompileResult.CompileError(CompileBackendIds.CodeDom, "/langversion:latest", new[] { "Line 1: CS1525: Unexpected symbol `with'" }, null));

            CompileOutcome outcome = new CompileBackendSelector(codeDom, new ICompileBackend[] { host }, null).Compile(Request());

            Assert.That(outcome.FormatFailureMessage(), Does.EndWith("\ncompiled with mcs fallback (C# 7.x); Roslyn host unavailable: toolchain-missing: no NetCoreRuntime"));
        }

        [Test]
        public void PreferredBackendException_FallsBack()
        {
            var host = new FakeBackend(CompileBackendIds.RoslynHost, _ => throw new System.IO.IOException("broken pipe"));
            var codeDom = new FakeBackend(CompileBackendIds.CodeDom, _ => CompileResult.Success(CompileBackendIds.CodeDom, typeof(CompileBackendSelectorTests).Assembly, Options));

            CompileOutcome outcome = new CompileBackendSelector(codeDom, new ICompileBackend[] { host }, null).Compile(Request());

            Assert.That(outcome.Compiler, Is.EqualTo("codedom"));
            Assert.That(outcome.FallbackReason, Is.EqualTo("backend-exception: IOException: broken pipe"));
        }

        [Test]
        public void DefaultEditor_ChainFollowsTheDefault()
        {
            Assume.That(Environment.GetEnvironmentVariable(ExecuteCompilerSettings.CompilerVariable), Is.Null.Or.Empty, "the editor was started with UCB_EXECUTE_COMPILER set");
            BundledToolchainReport toolchain = BundledToolchainLocator.Locate(EditorApplication.applicationContentsPath);
            Assume.That(toolchain.Complete, "this editor has no complete bundled toolchain");

            CompileBackendSelector selector = RoslynHostEditorLifecycle.CreateSelector();

            Assert.That(RoslynHostEditorLifecycle.Settings.UsesRoslynHost, Is.EqualTo(ExecuteCompilerSettings.AutoUsesRoslynHost));
            if (ExecuteCompilerSettings.AutoUsesRoslynHost)
            {
                Assert.That(RoslynHostEditorLifecycle.Supervisor, Is.Not.Null);
                Assert.That(CscSharedEditorLifecycle.Backend, Is.Not.Null);
                Assert.That(selector.Chain.Count, Is.EqualTo(3));
                Assert.That(selector.Chain[0].Id, Is.EqualTo("roslyn-host"));
                Assert.That(selector.Chain[1].Id, Is.EqualTo("csc-shared"));
                Assert.That(selector.Chain[2].Id, Is.EqualTo("codedom"));
            }
            else
            {
                Assert.That(RoslynHostEditorLifecycle.Supervisor, Is.Null);
                Assert.That(CscSharedEditorLifecycle.Backend, Is.Null);
                Assert.That(selector.Chain.Count, Is.EqualTo(1));
                Assert.That(selector.Select().Id, Is.EqualTo("codedom"));
            }
        }

        [Test]
        public void DefaultEditor_ExecuteReportsTheDefaultBackendInTimings()
        {
            Assume.That(Environment.GetEnvironmentVariable(ExecuteCompilerSettings.CompilerVariable), Is.Null.Or.Empty, "the editor was started with UCB_EXECUTE_COMPILER set");
            BundledToolchainReport toolchain = BundledToolchainLocator.Locate(EditorApplication.applicationContentsPath);
            Assume.That(toolchain.Complete, "this editor has no complete bundled toolchain");
            var handler = new ExecuteCodeHandler();
            string args = ProtocolJson.Serialize(new ExecuteCodeArgs { code = "__pucResult = 6 * 7; // timings " + Guid.NewGuid().ToString("N"), force = true });

            ExecuteCodePayload payload = ProtocolJson.Deserialize<ExecuteCodePayload>(handler.Handle(ProtocolConstants.CommandExecuteCode, args));

            Assert.That(payload.result, Is.EqualTo("42"));
            Assert.That(payload.timings, Is.Not.Null);
            if (ExecuteCompilerSettings.AutoUsesRoslynHost)
            {
                // The first call of a fresh project falls back while the host image is built in the
                // background (host-building -> csc-shared); every other call is the host.
                Assert.That(payload.timings!.compiler, Is.EqualTo("roslyn-host").Or.EqualTo("csc-shared"));
            }
            else
            {
                Assert.That(payload.timings!.compiler, Is.EqualTo("codedom"));
                Assert.That(payload.timings.compilerFallbackReason, Is.Empty);
            }
        }

        [Test]
        public void BundledDotnetStartInfo_ScrubsTheSystemDotnetVariablesUnderMono()
        {
            string[] names = { "DOTNET_ROOT", "DOTNET_ROLL_FORWARD", "DOTNET_STARTUP_HOOKS", "COMPlus_TieredCompilation", "DOTNET_gcServer", "MSBuildSDKsPath", "DOTNET_MULTILEVEL_LOOKUP" };
            var saved = new Dictionary<string, string?>();
            foreach (string name in names)
            {
                saved[name] = Environment.GetEnvironmentVariable(name);
            }

            try
            {
                Environment.SetEnvironmentVariable("DOTNET_ROOT", "/opt/homebrew/opt/dotnet/libexec");
                Environment.SetEnvironmentVariable("DOTNET_ROLL_FORWARD", "LatestMajor");
                Environment.SetEnvironmentVariable("DOTNET_STARTUP_HOOKS", "/tmp/hook.dll");
                Environment.SetEnvironmentVariable("COMPlus_TieredCompilation", "0");
                Environment.SetEnvironmentVariable("DOTNET_gcServer", "1");
                Environment.SetEnvironmentVariable("MSBuildSDKsPath", "/tmp/sdks");
                Environment.SetEnvironmentVariable("DOTNET_MULTILEVEL_LOOKUP", "1");
                BundledToolchainReport toolchain = BundledToolchainLocator.Locate(EditorApplication.applicationContentsPath);
                Assume.That(toolchain.Runtime != null && toolchain.Runtime.Found, "no bundled runtime in this editor");

                ProcessStartInfo startInfo = BundledDotnetProcess.CreateStartInfo(toolchain.Runtime!.Path!, new[] { "--info" }, null);

                foreach (string removed in new[] { "DOTNET_ROOT", "DOTNET_ROLL_FORWARD", "DOTNET_STARTUP_HOOKS", "COMPlus_TieredCompilation", "DOTNET_gcServer", "MSBuildSDKsPath" })
                {
                    Assert.That(startInfo.Environment.ContainsKey(removed), Is.False, removed);
                }

                Assert.That(startInfo.Environment["DOTNET_MULTILEVEL_LOOKUP"], Is.EqualTo("0"));
                Assert.That(startInfo.Environment["DOTNET_TieredPGO"], Is.EqualTo("0"));
                Assert.That(startInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"], Is.EqualTo("1"));
                Assert.That(startInfo.UseShellExecute, Is.False);
                Assert.That(System.IO.Path.IsPathRooted(startInfo.FileName), Is.True);
            }
            finally
            {
                foreach (KeyValuePair<string, string?> pair in saved)
                {
                    Environment.SetEnvironmentVariable(pair.Key, pair.Value);
                }
            }
        }

        [Test]
        public void McsVersion_UnderMono_IsTheAssemblyVersionDoctorReports()
        {
            BundledToolchainReport toolchain = BundledToolchainLocator.Locate(EditorApplication.applicationContentsPath);
            Assume.That(toolchain.Mcs != null && toolchain.Mcs.Found, "no bundled mcs in this editor");
            string path = toolchain.Mcs!.Path!;

            // Mono's FileVersionInfo reads mcs.exe's Win32 resource (4.6.57.0); the CLI under .NET reads
            // the assembly (6.13.0.0). Both sides now read the assembly metadata.
            Assert.That(toolchain.Mcs.Version, Is.EqualTo(AssemblyName.GetAssemblyName(path).Version!.ToString()));
            Assert.That(BundledToolchainLocator.ReadMcsVersion(path, _ => "4.6.57.0"), Is.EqualTo(toolchain.Mcs.Version));
        }

        private sealed class FakeBackend : ICompileBackend
        {
            private readonly Func<CompileRequest, CompileResult> _compile;

            public FakeBackend(string id, Func<CompileRequest, CompileResult> compile)
            {
                Id = id;
                _compile = compile;
            }

            public string Id { get; }

            public int Calls { get; private set; }

            public CompileResult TryCompile(CompileRequest request)
            {
                Calls++;
                return _compile(request);
            }
        }
    }
}
