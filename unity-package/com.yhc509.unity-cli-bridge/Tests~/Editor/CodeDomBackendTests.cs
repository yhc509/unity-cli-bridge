#nullable enable
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
