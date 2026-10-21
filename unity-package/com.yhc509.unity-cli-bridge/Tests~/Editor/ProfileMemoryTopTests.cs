#nullable enable
using System;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityCli.Protocol;
using UnityEngine;

namespace UnityCliBridge.Bridge.Editor.Tests
{
    public sealed class ProfileMemoryTopTests
    {
        [Test]
        public void Handle_RanksInjectedObjectsAndSkipsHidden()
        {
            var visible = new Texture2D(8, 4, TextureFormat.RGBA32, false);
            visible.name = "UcbTopVisible";
            var hidden = new Texture2D(8, 4, TextureFormat.RGBA32, false);
            hidden.name = "UcbTopHidden";
            hidden.hideFlags = HideFlags.HideAndDontSave;
            var mesh = new Mesh();
            mesh.name = "UcbTopMesh";
            mesh.vertices = new[] { Vector3.zero, Vector3.one, Vector3.up };
            mesh.SetTriangles(new[] { 0, 1, 2 }, 0);

            var handler = new ProfileCommandHandler
            {
                MemoryTopObjectSource = typeName =>
                {
                    if (typeName == "Texture2D")
                    {
                        return new UnityEngine.Object[] { visible, hidden, null! };
                    }

                    if (typeName == "Mesh")
                    {
                        return new UnityEngine.Object[] { mesh };
                    }

                    return Array.Empty<UnityEngine.Object>();
                },
            };

            try
            {
                string json = handler.Handle(
                    ProtocolConstants.CommandProfileMemoryTop,
                    ProtocolJson.Serialize(new ProfileMemoryTopArgs { limit = 5, scope = "loaded" }));
                JObject payload = JObject.Parse(json);
                Assert.That(payload.Value<string>("note"), Does.Contain("Editor-measured sizes"));
                Assert.That(payload.Value<string>("scope"), Is.EqualTo("loaded"));
                Assert.That(payload["type"], Is.Null);
                Assert.That(payload.Value<bool>("truncated"), Is.False);

                JArray top = (JArray)payload["top"]!;
                JObject? texture = FindByName(top, "UcbTopVisible");
                JObject? meshEntry = FindByName(top, "UcbTopMesh");
                Assert.That(texture, Is.Not.Null);
                Assert.That(FindByName(top, "UcbTopHidden"), Is.Null);
                Assert.That(texture!["detail"]!.Value<int>("width"), Is.EqualTo(8));
                Assert.That(texture["detail"]!.Value<int>("height"), Is.EqualTo(4));
                Assert.That(texture["detail"]!.Value<string>("format"), Is.EqualTo("RGBA32"));
                Assert.That(texture["detail"]!.Value<int>("mipCount"), Is.EqualTo(1));
                Assert.That(texture["detail"]!["vertexCount"], Is.Null);
                Assert.That(meshEntry, Is.Not.Null);
                Assert.That(meshEntry!["detail"]!.Value<int>("vertexCount"), Is.EqualTo(3));
                Assert.That(meshEntry["detail"]!.Value<int>("subMeshCount"), Is.EqualTo(1));
                Assert.That(meshEntry["path"], Is.Null);

                JArray totals = (JArray)payload["totals"]!;
                Assert.That(CountFor(totals, "Texture2D"), Is.EqualTo(1));
                Assert.That(CountFor(totals, "Mesh"), Is.EqualTo(1));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(visible);
                UnityEngine.Object.DestroyImmediate(hidden);
                UnityEngine.Object.DestroyImmediate(mesh);
            }
        }

        [Test]
        public void Handle_AssetsScope_DropsNonPersistentObjects()
        {
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            texture.name = "UcbTopSceneOnly";
            var handler = new ProfileCommandHandler
            {
                MemoryTopObjectSource = _ => new UnityEngine.Object[] { texture },
            };

            try
            {
                string json = handler.Handle(
                    ProtocolConstants.CommandProfileMemoryTop,
                    ProtocolJson.Serialize(new ProfileMemoryTopArgs { type = "Texture2D", scope = "assets", limit = 5 }));
                JObject payload = JObject.Parse(json);
                Assert.That(payload.Value<string>("type"), Is.EqualTo("Texture2D"));
                Assert.That(payload.Value<string>("scope"), Is.EqualTo("assets"));
                Assert.That(((JArray)payload["top"]!).Count, Is.EqualTo(0));
                Assert.That(((JArray)payload["totals"]!).Count, Is.EqualTo(0));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(texture);
            }
        }

        [Test]
        public void Handle_ScanCap_SetsTruncatedAndWarning()
        {
            var first = new Texture2D(2, 2, TextureFormat.RGBA32, false) { name = "UcbTopCapA" };
            var second = new Texture2D(2, 2, TextureFormat.RGBA32, false) { name = "UcbTopCapB" };
            var handler = new ProfileCommandHandler
            {
                MemoryTopScanCap = 1,
                MemoryTopObjectSource = _ => new UnityEngine.Object[] { first, second },
            };

            try
            {
                string json = handler.Handle(
                    ProtocolConstants.CommandProfileMemoryTop,
                    ProtocolJson.Serialize(new ProfileMemoryTopArgs { type = "Texture2D", limit = 20 }));
                JObject payload = JObject.Parse(json);
                Assert.That(payload.Value<bool>("truncated"), Is.True);
                Assert.That(payload["warnings"]!.ToString(), Does.Contain("Scan stopped after 1"));
                Assert.That(((JArray)payload["top"]!).Count, Is.EqualTo(1));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(first);
                UnityEngine.Object.DestroyImmediate(second);
            }
        }

        [Test]
        public void Handle_RealMeshQuery_ReturnsTheEditorNote()
        {
            var handler = new ProfileCommandHandler();
            string json = handler.Handle(
                ProtocolConstants.CommandProfileMemoryTop,
                ProtocolJson.Serialize(new ProfileMemoryTopArgs { type = "Mesh", limit = 1, scope = "loaded" }));
            JObject payload = JObject.Parse(json);
            Assert.That(payload.Value<string>("note"), Does.Contain("differ from player builds"));
            Assert.That(payload.Value<string>("type"), Is.EqualTo("Mesh"));
            Assert.That(payload["totals"], Is.Not.Null);
            Assert.That(payload["top"], Is.Not.Null);
        }

        private static JObject? FindByName(JArray entries, string name)
        {
            foreach (JToken entry in entries)
            {
                if (entry.Value<string>("name") == name)
                {
                    return (JObject)entry;
                }
            }

            return null;
        }

        private static int CountFor(JArray totals, string type)
        {
            foreach (JToken total in totals)
            {
                if (total.Value<string>("type") == type)
                {
                    return total.Value<int>("count");
                }
            }

            return -1;
        }
    }
}
