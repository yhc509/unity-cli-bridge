#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace UnityCliBridge.Bridge.Editor.Tests
{
    public sealed class ExecuteValueSerializerTests
    {
        [Test] public void Null_Serializes_AsJsonNull()
            => Assert.AreEqual("null", ExecuteValueSerializer.Serialize(null));

        [Test] public void Bool_Serializes_AsLowercase()
            => Assert.AreEqual("true", ExecuteValueSerializer.Serialize(true));

        [Test] public void Int_Serializes_Raw()
            => Assert.AreEqual("42", ExecuteValueSerializer.Serialize(42));

        [Test] public void Float_Uses_G9_RoundTrip()
        {
            // 0.1f's round-trip form is "0.100000001"
            Assert.AreEqual("0.100000001", ExecuteValueSerializer.Serialize(0.1f));
        }

        [Test] public void Double_Uses_G17_RoundTrip()
        {
            Assert.AreEqual("0.10000000000000001", ExecuteValueSerializer.Serialize(0.1d));
        }

        [TestCase(float.NaN)]
        [TestCase(float.PositiveInfinity)]
        [TestCase(float.NegativeInfinity)]
        public void Float_NonFinite_Serializes_AsNull(float value)
        {
            Assert.AreEqual("null", ExecuteValueSerializer.Serialize(value));
        }

        [TestCase(double.NaN)]
        [TestCase(double.PositiveInfinity)]
        [TestCase(double.NegativeInfinity)]
        public void Double_NonFinite_Serializes_AsNull(double value)
        {
            Assert.AreEqual("null", ExecuteValueSerializer.Serialize(value));
        }

        [Test] public void String_Is_Escaped()
            => Assert.AreEqual("\"a\\\"b\\nc\"", ExecuteValueSerializer.Serialize("a\"b\nc"));

        [Test]
        public void UnityObject_Serializes_InstanceId_AsJsonString()
        {
            var gameObject = new GameObject("ExecuteSerializerObject");
            try
            {
                var json = ExecuteValueSerializer.Serialize(gameObject);
                var parsed = JObject.Parse(json);
                string? instanceId = (string?)parsed["instanceID"];

                StringAssert.Contains("\"instanceID\":\"", json);
                Assert.AreEqual(JTokenType.String, parsed["instanceID"]?.Type);
                Assert.IsNotNull(instanceId);
                Assert.IsTrue(decimal.TryParse(
                    instanceId!,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out _));
                Assert.AreEqual("ExecuteSerializerObject", (string?)parsed["name"]);
                Assert.AreEqual("GameObject", (string?)parsed["type"]);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(gameObject);
            }
        }

        [Test] public void Array_Serializes_AsJsonArray()
            => Assert.AreEqual("[1,2,3]", ExecuteValueSerializer.Serialize(new[] { 1, 2, 3 }));

        [Test] public void Dictionary_Serializes_AsObject()
            => Assert.AreEqual("{\"a\":1}", ExecuteValueSerializer.Serialize(new Dictionary<string, int> { ["a"] = 1 }));

        [Test] public void Dictionary_WithNonStringKeys_Serializes_AsKeyValueArray()
            => Assert.AreEqual("[{\"key\":1,\"value\":\"a\"}]", ExecuteValueSerializer.Serialize(new Dictionary<int, string> { [1] = "a" }));

        [Test] public void Vector3_Serializes_Fields()
        {
            var json = ExecuteValueSerializer.Serialize(new Vector3(1f, 2f, 3f));
            Assert.AreEqual("{\"x\":1,\"y\":2,\"z\":3}", json);
        }

        [Test] public void Vector3_NonFiniteComponent_Serializes_AsNull()
        {
            var json = ExecuteValueSerializer.Serialize(new Vector3(float.NaN, 1f, 2f));
            Assert.AreEqual("{\"x\":null,\"y\":1,\"z\":2}", json);
        }

        [Test] public void PlainObject_Uses_PublicMembers()
        {
            var json = ExecuteValueSerializer.Serialize(new { name = "go", count = 2 });
            StringAssert.Contains("\"name\":\"go\"", json);
            StringAssert.Contains("\"count\":2", json);
        }

        [Test] public void CyclicReference_Does_Not_StackOverflow()
        {
            var a = new Node();
            a.Self = a;
            Assert.DoesNotThrow(() => ExecuteValueSerializer.Serialize(a));
        }

        [Test] public void CancelledToken_StopsSerialization()
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            Assert.Throws<OperationCanceledException>(() => ExecuteValueSerializer.Serialize(new[] { 1 }, cts.Token));
        }

        [Test] public void NodeCap_StopsUnboundedSequences()
        {
            Assert.Throws<OperationCanceledException>(() => ExecuteValueSerializer.Serialize(new InfiniteSequence()));
        }

        private sealed class Node { public Node? Self; }

        private sealed class InfiniteSequence : IEnumerable<object?>
        {
            public IEnumerator<object?> GetEnumerator()
            {
                while (true)
                {
                    yield return null;
                }
            }

            IEnumerator IEnumerable.GetEnumerator()
            {
                return GetEnumerator();
            }
        }
    }

    public sealed class SerializedValueApplierVisibilityTests
    {
        [Test]
        public void Apply_RejectsHiddenTopLevelProperty()
        {
            GameObject gameObject = CreateGameObject();
            try
            {
                var values = new JObject
                {
                    ["m_GameObject"] = JValue.CreateNull(),
                };

                CommandFailureException ex = Assert.Throws<CommandFailureException>(
                    () => SerializedValueApplier.Apply(gameObject.transform, values));

                Assert.AreEqual("COMPONENT_VALUE_KEY_INVALID", ex.ErrorCode);
                StringAssert.Contains("not exposed to inspect, so it cannot be patched", ex.Message);
                StringAssert.Contains("m_GameObject", ex.Message);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(gameObject);
            }
        }

        [Test]
        public void Apply_KeepsFriendlyKeyFallbackForEditableField()
        {
            GameObject gameObject = CreateGameObject();
            try
            {
                Rigidbody rigidbody = gameObject.AddComponent<Rigidbody>();
                var values = new JObject
                {
                    ["mass"] = 3.5f,
                };

                SerializedValueApplier.Apply(rigidbody, values);

                Assert.AreEqual(3.5f, rigidbody.mass);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(gameObject);
            }
        }

        [Test]
        public void Apply_AllowsEditableNestedObjectChild()
        {
            GameObject gameObject = CreateGameObject();
            try
            {
                SerializedValueApplierNestedObjectHost host = gameObject.AddComponent<SerializedValueApplierNestedObjectHost>();
                var values = new JObject
                {
                    ["nested"] = new JObject
                    {
                        ["visible"] = 11,
                    },
                };

                SerializedValueApplier.Apply(host, values);

                Assert.AreEqual(11, host.nested.visible);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(gameObject);
            }
        }

        [Test]
        public void Apply_RejectsHiddenNestedObjectChild()
        {
            GameObject gameObject = CreateGameObject();
            try
            {
                SerializedValueApplierNestedObjectHost host = gameObject.AddComponent<SerializedValueApplierNestedObjectHost>();
                var values = new JObject
                {
                    ["nested"] = new JObject
                    {
                        ["hidden"] = 12,
                    },
                };

                CommandFailureException ex = Assert.Throws<CommandFailureException>(
                    () => SerializedValueApplier.Apply(host, values));

                Assert.AreEqual("COMPONENT_VALUE_KEY_INVALID", ex.ErrorCode);
                StringAssert.Contains("not exposed to inspect, so it cannot be patched", ex.Message);
                Assert.AreEqual(0, host.nested.hidden);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(gameObject);
            }
        }

        [Test]
        public void Apply_AllowsManagedReferenceAssignableToDeclaredFieldType()
        {
            GameObject gameObject = CreateGameObject();
            try
            {
                SerializedValueApplierManagedReferenceHost host = gameObject.AddComponent<SerializedValueApplierManagedReferenceHost>();
                var values = new JObject
                {
                    ["reference"] = new JObject
                    {
                        ["$type"] = BuildManagedReferenceTypeName(typeof(SerializedValueApplierValidManagedReference)),
                        ["number"] = 42,
                    },
                };

                SerializedValueApplier.Apply(host, values);

                Assert.IsInstanceOf<SerializedValueApplierValidManagedReference>(host.reference);
                Assert.AreEqual(42, ((SerializedValueApplierValidManagedReference)host.reference!).number);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(gameObject);
            }
        }

        [Test]
        public void Apply_AllowsNestedManagedReferenceDeclaredBaseType()
        {
            GameObject gameObject = CreateGameObject();
            try
            {
                SerializedValueApplierNestedManagedReferenceHost host = gameObject.AddComponent<SerializedValueApplierNestedManagedReferenceHost>();
                var values = new JObject
                {
                    ["reference"] = new JObject
                    {
                        ["$type"] = BuildUnityManagedReferenceTypeName(typeof(SerializedValueApplierNestedManagedReferenceTypes.ValidReference)),
                        ["number"] = 43,
                    },
                };

                SerializedValueApplier.Apply(host, values);

                Assert.IsInstanceOf<SerializedValueApplierNestedManagedReferenceTypes.ValidReference>(host.reference);
                Assert.AreEqual(43, ((SerializedValueApplierNestedManagedReferenceTypes.ValidReference)host.reference!).number);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(gameObject);
            }
        }

        [Test]
        public void Apply_RejectsHiddenManagedReferenceChild()
        {
            GameObject gameObject = CreateGameObject();
            try
            {
                SerializedValueApplierNestedManagedReferenceHost host = gameObject.AddComponent<SerializedValueApplierNestedManagedReferenceHost>();
                var values = new JObject
                {
                    ["reference"] = new JObject
                    {
                        ["$type"] = BuildManagedReferenceTypeName(typeof(SerializedValueApplierNestedManagedReferenceTypes.ValidReference)),
                        ["hidden"] = 44,
                    },
                };

                CommandFailureException ex = Assert.Throws<CommandFailureException>(
                    () => SerializedValueApplier.Apply(host, values));

                Assert.AreEqual("COMPONENT_VALUE_KEY_INVALID", ex.ErrorCode);
                StringAssert.Contains("not exposed to inspect, so it cannot be patched", ex.Message);
                Assert.IsInstanceOf<SerializedValueApplierNestedManagedReferenceTypes.ValidReference>(host.reference);
                Assert.AreEqual(0, ((SerializedValueApplierNestedManagedReferenceTypes.ValidReference)host.reference!).hidden);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(gameObject);
            }
        }

        [Test]
        public void Apply_RejectsManagedReferenceNotAssignableToDeclaredFieldType()
        {
            GameObject gameObject = CreateGameObject();
            try
            {
                SerializedValueApplierManagedReferenceHost host = gameObject.AddComponent<SerializedValueApplierManagedReferenceHost>();
                var values = new JObject
                {
                    ["reference"] = new JObject
                    {
                        ["$type"] = BuildManagedReferenceTypeName(typeof(SerializedValueApplierIncompatibleManagedReference)),
                    },
                };

                CommandFailureException ex = Assert.Throws<CommandFailureException>(
                    () => SerializedValueApplier.Apply(host, values));

                Assert.AreEqual("PREFAB_FIELD_INVALID", ex.ErrorCode);
                StringAssert.Contains("not compatible with the field's declared type", ex.Message);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(gameObject);
            }
        }

        [Test]
        public void Apply_RejectsManagedReferenceWithoutPublicParameterlessConstructor()
        {
            GameObject gameObject = CreateGameObject();
            try
            {
                SerializedValueApplierManagedReferenceHost host = gameObject.AddComponent<SerializedValueApplierManagedReferenceHost>();
                var values = new JObject
                {
                    ["reference"] = new JObject
                    {
                        ["$type"] = BuildManagedReferenceTypeName(typeof(SerializedValueApplierNoDefaultConstructorManagedReference)),
                    },
                };

                CommandFailureException ex = Assert.Throws<CommandFailureException>(
                    () => SerializedValueApplier.Apply(host, values));

                Assert.AreEqual("PREFAB_FIELD_INVALID", ex.ErrorCode);
                StringAssert.Contains("needs a public parameterless constructor", ex.Message);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(gameObject);
            }
        }

        private static GameObject CreateGameObject()
        {
            return new GameObject("SerializedValueApplierTests");
        }

        private static string BuildManagedReferenceTypeName(Type type)
        {
            return type.FullName + ", " + type.Assembly.GetName().Name;
        }

        private static string BuildUnityManagedReferenceTypeName(Type type)
        {
            string fullName = type.FullName ?? throw new InvalidOperationException("Type full name is unavailable: " + type.Name);
            return type.Assembly.GetName().Name + " " + fullName.Replace('+', '/');
        }
    }

    public sealed class SerializedValueApplierNestedObjectHost : MonoBehaviour
    {
        public SerializedValueApplierNestedObject nested = new SerializedValueApplierNestedObject();
    }

    [Serializable]
    public sealed class SerializedValueApplierNestedObject
    {
        public int visible;

        [HideInInspector] public int hidden;
    }

    public sealed class SerializedValueApplierManagedReferenceHost : MonoBehaviour
    {
        [SerializeReference] public SerializedValueApplierManagedReferenceBase? reference;
    }

    public sealed class SerializedValueApplierNestedManagedReferenceHost : MonoBehaviour
    {
        [SerializeReference] public SerializedValueApplierNestedManagedReferenceTypes.BaseReference? reference;
    }

    public static class SerializedValueApplierNestedManagedReferenceTypes
    {
        [Serializable]
        public abstract class BaseReference
        {
        }

        [Serializable]
        public sealed class ValidReference : BaseReference
        {
            public int number;

            [HideInInspector] public int hidden;
        }
    }

    [Serializable]
    public abstract class SerializedValueApplierManagedReferenceBase
    {
    }

    [Serializable]
    public sealed class SerializedValueApplierValidManagedReference : SerializedValueApplierManagedReferenceBase
    {
        public int number;
    }

    [Serializable]
    public sealed class SerializedValueApplierNoDefaultConstructorManagedReference : SerializedValueApplierManagedReferenceBase
    {
        public SerializedValueApplierNoDefaultConstructorManagedReference(int number)
        {
            this.number = number;
        }

        public int number;
    }

    [Serializable]
    public sealed class SerializedValueApplierIncompatibleManagedReference
    {
    }
}
