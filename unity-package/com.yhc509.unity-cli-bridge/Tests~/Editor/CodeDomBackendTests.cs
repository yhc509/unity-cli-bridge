#nullable enable
using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.IO;
using Microsoft.CSharp;
using NUnit.Framework;
using UnityCli.Protocol;

namespace UnityCliBridge.Bridge.Editor.Tests
{
    public sealed class CodeDomBackendTests
    {
        [Test]
        public void Selector_DefaultsToCodeDom()
        {
            var selector = new CompileBackendSelector(new CodeDomBackend());

            Assert.That(selector.Select().Id, Is.EqualTo(CodeDomBackend.BackendId));
            Assert.That(CodeDomBackend.BackendId, Is.EqualTo("codedom"));
        }

        [Test]
        public void Build_GathersReferencesAndLanguageVersion()
        {
            CompileRequest request = CompileRequestBuilder.Build("// probe");

            Assert.That(request.Source, Is.EqualTo("// probe"));
            Assert.That(request.LanguageVersion, Is.Not.Empty);
            Assert.That(request.ReferencePaths, Is.Not.Empty);
            Assert.That(request.ReferencePaths, Is.Unique);
        }

        [Test]
        public void TryCompile_ValidSource_ReturnsLoadedAssembly()
        {
            CompileResult result = new CodeDomBackend().TryCompile(CompileRequestBuilder.Build(
                "public static class UcbCompileBackendProbe { public static int Value() { return 42; } }"));

            Assert.That(result.Succeeded, Is.True, result.FormatFailureMessage());
            Assert.That(result.IsInfrastructureFailure, Is.False);
            Assert.That(result.BackendId, Is.EqualTo("codedom"));
            Assert.That(result.Assembly!.GetType("UcbCompileBackendProbe"), Is.Not.Null);
            Assert.That(result.CompilerOptions, Does.StartWith("/langversion:"));
            Assert.That(result.CompilerOptions, Does.Contain("/define:UNITY_ASSERTIONS"));
        }

        [Test]
        public void TryCompile_SyntaxError_IsAUserErrorNotAnInfrastructureFailure()
        {
            CompileResult result = new CodeDomBackend().TryCompile(CompileRequestBuilder.Build(
                "public static class UcbCompileBackendBroken { public static void M() { int x = ; } }"));

            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.IsInfrastructureFailure, Is.False);
            Assert.That(result.Assembly, Is.Null);
            Assert.That(result.Errors, Is.Not.Empty);
            Assert.That(result.Errors[0], Does.Contain("CS"));
            Assert.That(result.FormatFailureMessage(), Does.StartWith("Code compilation failed (/langversion:"));
        }

        [Test]
        public void TryCompile_ReportsTheEffectiveOptionsNotTheResponseFile()
        {
            CompileRequest request = CompileRequestBuilder.Build(
                "public static class UcbCompileBackendRspBroken { public static void M() { int x = ; } }");
            string expectedOptions = CSharpLanguageVersion.ToCompilerOption(request.LanguageVersion)
                + " "
                + ExecuteCompilerDefines.BuildDefineOption(request.ScriptingDefines);

            CompileResult result = new CodeDomBackend().TryCompile(request);

            Assert.That(result.CompilerOptions, Is.EqualTo(expectedOptions));
            Assert.That(
                result.FormatFailureMessage(),
                Does.StartWith("Code compilation failed (" + expectedOptions + "):\nLine 1: CS"));
            Assert.That(result.FormatFailureMessage(), Does.Not.Contain(ResponseFileWriter.FileExtension));
        }

        [Test]
        public void TryCompile_DeletesTheResponseFileOnSuccessAndOnError()
        {
            string directory = CreateTempDirectory("ucb-rsp-cleanup");
            try
            {
                var backend = new CodeDomBackend(directory);
                CompileResult success = backend.TryCompile(CompileRequestBuilder.Build(
                    "public static class UcbCompileBackendRspClean { public static int Value() { return 1; } }"));
                CompileResult failure = backend.TryCompile(CompileRequestBuilder.Build(
                    "public static class UcbCompileBackendRspDirty { public static void M() { int x = ; } }"));

                Assert.That(success.Succeeded, Is.True, success.FormatFailureMessage());
                Assert.That(failure.Succeeded, Is.False);
                Assert.That(failure.Errors, Is.Not.Empty);
                Assert.That(Directory.GetFiles(directory), Is.Empty);
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        }

        [Test]
        public void TryCompile_ResolvesReferencesInPathsWithSpacesAndNonAsciiCharacters()
        {
            string root = CreateTempDirectory("ucb rsp 참조 テスト");
            try
            {
                string libraryPath = Path.Combine(root, "UcbRspProbeLib.dll");
                CompileLibrary(libraryPath, "public static class UcbRspProbeLib { public static int Value() { return 7; } }");
                AssertCompilesAgainst(libraryPath);

                if (Path.DirectorySeparatorChar == '/')
                {
                    // Not a legal Windows path; mcs reads a literal " in a response file as '"'.
                    string quotedDirectory = Path.Combine(root, "with \"quotes\" and 'apostrophes'");
                    Directory.CreateDirectory(quotedDirectory);
                    string quotedLibraryPath = Path.Combine(quotedDirectory, "UcbRspProbeLib.dll");
                    File.Copy(libraryPath, quotedLibraryPath);
                    AssertCompilesAgainst(quotedLibraryPath);
                }
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        private static void AssertCompilesAgainst(string libraryPath)
        {
            CompileRequest loaded = CompileRequestBuilder.Build(string.Empty);
            var references = new List<string>(loaded.ReferencePaths) { libraryPath };
            var request = new CompileRequest(
                "public static class UcbRspProbeUser { public static int Value() { return UcbRspProbeLib.Value(); } }",
                loaded.LanguageVersion,
                loaded.ScriptingDefines,
                references);

            CompileResult result = new CodeDomBackend().TryCompile(request);

            Assert.That(result.Succeeded, Is.True, libraryPath + "\n" + result.FormatFailureMessage());
        }

        private static void CompileLibrary(string outputPath, string source)
        {
            using var provider = new CSharpCodeProvider();
            var parameters = new CompilerParameters
            {
                GenerateInMemory = false,
                GenerateExecutable = false,
                OutputAssembly = outputPath,
            };
            CompilerResults result = provider.CompileAssemblyFromSource(parameters, source);
            Assert.That(result.Errors.HasErrors, Is.False, "Could not build the probe library.");
            Assert.That(File.Exists(outputPath), Is.True);
        }

        private static string CreateTempDirectory(string prefix)
        {
            string directory = Path.Combine(Path.GetTempPath(), prefix + " " + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            return directory;
        }

        [Test]
        public void Handle_CompileError_RaisesCompileFailedWithUserLineNumber()
        {
            var handler = new ExecuteCodeHandler();
            string argsJson = ProtocolJson.Serialize(new ExecuteCodeArgs
            {
                code = "int x = ;",
                force = true,
            });

            var ex = Assert.Throws<CommandFailureException>(
                () => handler.Handle(ProtocolConstants.CommandExecuteCode, argsJson));

            Assert.That(ex!.ErrorCode, Is.EqualTo("COMPILE_FAILED"));
            Assert.That(ex.Message, Does.StartWith("Code compilation failed (/langversion:"));
            Assert.That(ex.Message, Does.Contain("/define:UNITY_ASSERTIONS"));
            Assert.That(ex.Message, Does.Contain("Line 1: CS"));
        }
    }
}
