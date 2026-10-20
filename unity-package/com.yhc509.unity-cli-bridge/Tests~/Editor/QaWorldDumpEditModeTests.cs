#nullable enable
using NUnit.Framework;
using UnityCli.Protocol;
using UnityEditor;

namespace UnityCliBridge.Bridge.Editor.Tests
{
    public sealed class QaWorldDumpEditModeTests
    {
        [Test]
        public void Handle_OutsidePlayMode_ReturnsSceneInspectHintWithoutCoordinates()
        {
            Assert.That(EditorApplication.isPlaying, Is.False);

            string json = new QaCommandHandler().Handle(
                ProtocolConstants.CommandQaWorldDump,
                ProtocolJson.Serialize(new QaWorldDumpArgs()));

            Assert.That(json, Does.Contain("\"editMode\":true"));
            Assert.That(json, Does.Contain("scene inspect"));
            Assert.That(json, Does.Contain(QaWorldDumpEditMode.Hint));
            Assert.That(json, Does.Not.Contain("\"centerX\""));
            Assert.That(json, Does.Not.Contain("QA_NOT_PLAYING"));
        }
    }
}
