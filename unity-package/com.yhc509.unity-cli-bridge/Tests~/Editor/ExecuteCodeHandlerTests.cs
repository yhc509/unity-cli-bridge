#nullable enable
using System;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityCli.Protocol;
using UnityEngine;
using UnityEngine.TestTools;

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
        public void Handle_LogAssertion_EmitsAssert()
        {
            LogAssert.Expect(LogType.Assert, new Regex("ucb-216"));
            var handler = new ExecuteCodeHandler();
            string argsJson = ProtocolJson.Serialize(new ExecuteCodeArgs
            {
                code = "UnityEngine.Debug.LogAssertion(\"ucb-216\");",
                force = true,
            });

            string result = handler.Handle(ProtocolConstants.CommandExecuteCode, argsJson);
            ExecuteCodePayload? payload = ProtocolJson.Deserialize<ExecuteCodePayload>(result);
            Assert.That(payload, Is.Not.Null);
            Assert.That(payload!.success, Is.True);
        }

        [Test]
        public void Handle_TopOfFileUsing_Compiles()
        {
            var handler = new ExecuteCodeHandler();
            string argsJson = ProtocolJson.Serialize(new ExecuteCodeArgs
            {
                code = "using System.Text;\nvar builder = new StringBuilder();\nbuilder.Append(\"ok\");\n__pucResult = builder.ToString();",
                force = true,
            });

            ExecuteCodePayload? payload = ProtocolJson.Deserialize<ExecuteCodePayload>(
                handler.Handle(ProtocolConstants.CommandExecuteCode, argsJson));

            Assert.That(payload, Is.Not.Null);
            Assert.That(payload!.success, Is.True);
            Assert.That(payload.result, Does.Contain("ok"));
        }

        [Test]
        public void Handle_ReportsPhaseTimings_OnMissAndHit()
        {
            var handler = new ExecuteCodeHandler();
            string argsJson = ProtocolJson.Serialize(new ExecuteCodeArgs
            {
                code = "__pucResult = \"" + Guid.NewGuid().ToString("N") + "\";",
                force = true,
            });
            var firstTimings = new ResponseTimings();
            var secondTimings = new ResponseTimings();

            ExecuteCodePayload? first = ProtocolJson.Deserialize<ExecuteCodePayload>(
                handler.Handle(ProtocolConstants.CommandExecuteCode, argsJson, firstTimings));
            ExecuteCodePayload? second = ProtocolJson.Deserialize<ExecuteCodePayload>(
                handler.Handle(ProtocolConstants.CommandExecuteCode, argsJson, secondTimings));

            Assert.That(first, Is.Not.Null);
            Assert.That(first!.success, Is.True);
            Assert.That(first.cacheHit, Is.False);
            Assert.That(firstTimings.wrapMs, Is.Not.Null);
            Assert.That(firstTimings.compileMs, Is.Not.Null);
            Assert.That(firstTimings.loadMs, Is.Not.Null);
            Assert.That(firstTimings.invokeMs, Is.Not.Null);
            Assert.That(firstTimings.serializeMs, Is.Not.Null);
            Assert.That(firstTimings.compiler, Is.EqualTo("codedom"));
            Assert.That(firstTimings.cacheHit, Is.False);
            Assert.That(firstTimings.wrapMs!.Value, Is.GreaterThanOrEqualTo(0));
            Assert.That(firstTimings.compileMs!.Value, Is.GreaterThanOrEqualTo(0));
            Assert.That(firstTimings.loadMs!.Value, Is.GreaterThanOrEqualTo(0));

            Assert.That(second, Is.Not.Null);
            Assert.That(second!.cacheHit, Is.True);
            Assert.That(second.compileMs, Is.EqualTo(0));
            Assert.That(secondTimings.compiler, Is.EqualTo("codedom"));
            Assert.That(secondTimings.cacheHit, Is.True);
            Assert.That(secondTimings.compileMs, Is.EqualTo(0));
            Assert.That(secondTimings.loadMs, Is.EqualTo(0));
            Assert.That(secondTimings.invokeMs, Is.Not.Null);
            Assert.That(secondTimings.serializeMs, Is.Not.Null);
        }

        [Test]
        public void Handle_WhenCompileFails_RecordsWrapAndCompileOnly()
        {
            var handler = new ExecuteCodeHandler();
            var timings = new ResponseTimings();
            string argsJson = ProtocolJson.Serialize(new ExecuteCodeArgs
            {
                code = "this is not valid $$$;",
                force = true,
            });

            CommandFailureException? ex = Assert.Throws<CommandFailureException>(
                () => handler.Handle(ProtocolConstants.CommandExecuteCode, argsJson, timings));

            Assert.That(ex!.ErrorCode, Is.EqualTo("COMPILE_FAILED"));
            Assert.That(timings.compiler, Is.EqualTo("codedom"));
            Assert.That(timings.wrapMs, Is.Not.Null);
            Assert.That(timings.compileMs, Is.Not.Null);
            Assert.That(timings.cacheHit, Is.False);
            Assert.That(timings.loadMs, Is.Null);
            Assert.That(timings.invokeMs, Is.Null);
            Assert.That(timings.serializeMs, Is.Null);
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
