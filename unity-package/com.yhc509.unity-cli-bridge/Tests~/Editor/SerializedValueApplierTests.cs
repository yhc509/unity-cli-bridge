#nullable enable
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace UnityCliBridge.Bridge.Editor.Tests
{
    public sealed class SerializedValueApplierTests
    {
        private GameObject? _gameObject;
        private SerializedValueProbe _probe = null!;

        [SetUp]
        public void SetUp()
        {
            _gameObject = new GameObject("SerializedValueApplierProbe");
            _probe = _gameObject.AddComponent<SerializedValueProbe>();
        }

        [TearDown]
        public void TearDown()
        {
            if (_gameObject != null)
            {
                Object.DestroyImmediate(_gameObject);
                _gameObject = null;
            }
        }

        private void Apply(string valuesJson)
        {
            SerializedValueApplier.Apply(_probe, JObject.Parse(valuesJson));
        }

        [Test]
        public void Apply_WhenVector3IsAnArray_UsesComponentOrder()
        {
            Apply("{\"vector3Value\":[1,2,3]}");

            Assert.That(_probe.vector3Value, Is.EqualTo(new Vector3(1f, 2f, 3f)));
        }

        [Test]
        public void Apply_WhenVector3IsAStringEncodedArray_ParsesItAnyway()
        {
            Apply("{\"vector3Value\":\"[1,2,3]\"}");

            Assert.That(_probe.vector3Value, Is.EqualTo(new Vector3(1f, 2f, 3f)));
        }

        [Test]
        public void Apply_WhenVector3IsAStringEncodedObject_ParsesItAnyway()
        {
            Apply("{\"vector3Value\":\"{\\\"x\\\":1,\\\"y\\\":2,\\\"z\\\":3}\"}");

            Assert.That(_probe.vector3Value, Is.EqualTo(new Vector3(1f, 2f, 3f)));
        }

        [Test]
        public void Apply_WhenVector3IsAnObject_StillWorks()
        {
            Apply("{\"vector3Value\":{\"x\":1,\"y\":2,\"z\":3}}");

            Assert.That(_probe.vector3Value, Is.EqualTo(new Vector3(1f, 2f, 3f)));
        }

        [Test]
        public void Apply_WhenTupleShorthandIsUsed_CoversTheVectorFamily()
        {
            Apply("{\"vector2Value\":[1,2],\"vector4Value\":[1,2,3,4],\"vector3IntValue\":[1,2,3]," +
                  "\"quaternionValue\":[0,0,0,1],\"rectValue\":[1,2,3,4]}");

            Assert.That(_probe.vector2Value, Is.EqualTo(new Vector2(1f, 2f)));
            Assert.That(_probe.vector4Value, Is.EqualTo(new Vector4(1f, 2f, 3f, 4f)));
            Assert.That(_probe.vector3IntValue, Is.EqualTo(new Vector3Int(1, 2, 3)));
            Assert.That(_probe.quaternionValue, Is.EqualTo(new Quaternion(0f, 0f, 0f, 1f)));
            Assert.That(_probe.rectValue, Is.EqualTo(new Rect(1f, 2f, 3f, 4f)));
        }

        [Test]
        public void Apply_WhenColorArrayOmitsAlpha_DefaultsToOpaque()
        {
            Apply("{\"colorValue\":[1,0,0]}");

            Assert.That(_probe.colorValue, Is.EqualTo(new Color(1f, 0f, 0f, 1f)));
        }

        [Test]
        public void Apply_WhenNestedVectorsAreArrays_AppliesThroughTheParentObject()
        {
            Apply("{\"boundsValue\":{\"center\":[1,2,3],\"size\":[4,5,6]}}");

            Assert.That(_probe.boundsValue.center, Is.EqualTo(new Vector3(1f, 2f, 3f)));
            Assert.That(_probe.boundsValue.size, Is.EqualTo(new Vector3(4f, 5f, 6f)));
        }

        [Test]
        public void Apply_WhenTupleArityIsWrong_Fails()
        {
            CommandFailureException failure = Assert.Throws<CommandFailureException>(
                () => Apply("{\"vector3Value\":[1,2]}"))!;

            Assert.That(failure.Message, Does.Contain("vector3Value"));
        }

        [Test]
        public void Apply_WhenStringLooksLikeJsonButTargetIsAString_LeavesItAlone()
        {
            Apply("{\"stringValue\":\"[not,really,json]\"}");

            Assert.That(_probe.stringValue, Is.EqualTo("[not,really,json]"));
        }

        [Test]
        public void Apply_WhenStringIsValidJsonButTargetIsAString_LeavesItAlone()
        {
            Apply("{\"stringValue\":\"{\\\"x\\\":1}\"}");

            Assert.That(_probe.stringValue, Is.EqualTo("{\"x\":1}"));
        }

        [Test]
        public void Apply_WhenStringEncodedJsonIsMalformed_KeepsTheOriginalValidationError()
        {
            CommandFailureException failure = Assert.Throws<CommandFailureException>(
                () => Apply("{\"vector3Value\":\"[1,2,\"}"))!;

            Assert.That(failure.ErrorCode, Is.EqualTo("PREFAB_FIELD_INVALID"));
            Assert.That(failure.Message, Does.Contain("object value is required"));
        }

        [Test]
        public void Apply_WhenArrayFieldIsAStringEncodedArray_ParsesItAnyway()
        {
            Apply("{\"intArrayValue\":\"[1,2,3]\"}");

            Assert.That(_probe.intArrayValue, Is.EqualTo(new[] { 1, 2, 3 }));
        }

        [Test]
        public void Apply_WhenPrimitivesAndEnumsAreUsed_BehaviorIsUnchanged()
        {
            Apply("{\"floatValue\":1.5,\"intValue\":7,\"enumValue\":\"Running\"}");

            Assert.That(_probe.floatValue, Is.EqualTo(1.5f));
            Assert.That(_probe.intValue, Is.EqualTo(7));
            Assert.That(_probe.enumValue, Is.EqualTo(SerializedValueProbe.ProbeMode.Running));
        }
    }
}
