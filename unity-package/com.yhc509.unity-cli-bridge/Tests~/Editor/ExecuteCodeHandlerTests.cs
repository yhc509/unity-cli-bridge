#nullable enable
using System;
using NUnit.Framework;
using UnityCli.Protocol;

namespace UnityCliBridge.Bridge.Editor.Tests
{
    public sealed class ExecuteCodeHandlerTests
    {
        [Test]
        public void Handle_WhenUserCodeThrows_RaisesCommandFailure()
        {
            var handler = new ExecuteCodeHandler();
            string argsJson = ProtocolJson.Serialize(new ExecuteCodeArgs
            {
                code = "throw new System.Exception(\"boom\");",
                force = true,
            });

            var ex = Assert.Throws<CommandFailureException>(
                () => handler.Handle(ProtocolConstants.CommandExecuteCode, argsJson));

            Assert.That(ex!.ErrorCode, Is.EqualTo("EXECUTE_FAILED"));
            Assert.That(ex.Message, Does.Contain("boom"));
        }

        [Test]
        public void Handle_WhenUserCodeSucceeds_ReturnsSuccessPayload()
        {
            var handler = new ExecuteCodeHandler();
            string argsJson = ProtocolJson.Serialize(new ExecuteCodeArgs
            {
                code = "var __x = 1 + 1;",
                force = true,
            });

            string result = handler.Handle(ProtocolConstants.CommandExecuteCode, argsJson);
            ExecuteCodePayload? payload = ProtocolJson.Deserialize<ExecuteCodePayload>(result);

            Assert.That(payload, Is.Not.Null);
            Assert.That(payload!.success, Is.True);
        }

        [Test]
        public void Handle_DefaultLiteral_CompilesAndSecondCallHitsCache()
        {
            var handler = new ExecuteCodeHandler();
            string argsJson = ProtocolJson.Serialize(new ExecuteCodeArgs
            {
                code = "int value = default; __pucResult = value;",
                force = true,
            });

            ExecuteCodePayload? first = ProtocolJson.Deserialize<ExecuteCodePayload>(
                handler.Handle(ProtocolConstants.CommandExecuteCode, argsJson));
            ExecuteCodePayload? second = ProtocolJson.Deserialize<ExecuteCodePayload>(
                handler.Handle(ProtocolConstants.CommandExecuteCode, argsJson));

            Assert.That(first, Is.Not.Null);
            Assert.That(first!.success, Is.True);
            Assert.That(first.cacheHit, Is.False);
            Assert.That(second, Is.Not.Null);
            Assert.That(second!.cacheHit, Is.True);
            Assert.That(second.compileMs, Is.EqualTo(0));
        }

        [Test]
        public void BuildWrappedCode_FileHeaderUsings_SitAboveTheWrapper()
        {
            const string source = "\uFEFFusing UnityEngine; // camera\r\nusing UnityEngine.Rendering;\r\n\r\nvar btn = GameObject.Find(\"Button\");\r\n";

            string wrapped = ExecuteCodeHandler.BuildWrappedCode(source);
            int classAt = wrapped.IndexOf("public static class PucExecuteWrapper", StringComparison.Ordinal);
            int usingAt = wrapped.IndexOf("using UnityEngine;", StringComparison.Ordinal);
            int methodAt = wrapped.IndexOf("public static string Execute", StringComparison.Ordinal);

            Assert.That(usingAt, Is.GreaterThanOrEqualTo(0));
            Assert.That(usingAt, Is.LessThan(classAt));
            Assert.That(wrapped.Substring(methodAt), Does.Not.Contain("using UnityEngine;"));
            Assert.That(wrapped, Does.Contain("var btn = GameObject.Find(\"Button\");"));
        }
    }
}
