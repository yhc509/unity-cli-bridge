#nullable enable
using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace UnityCliBridge.Bridge.Editor
{
    internal static class InspectorMutationReaderUtility
    {
        internal static JObject ParseArgumentsObject(string? argumentsJson)
        {
            if (string.IsNullOrWhiteSpace(argumentsJson))
            {
                return new JObject();
            }

            return JObject.Parse(argumentsJson);
        }

        internal static T DeserializeArguments<T>(JObject arguments) where T : class, new()
        {
            if (arguments == null)
            {
                throw new ArgumentNullException(nameof(arguments));
            }

            return arguments.ToObject<T>(BridgeJsonSettings.CamelCaseIgnoreNullSerializer) ?? new T();
        }

        internal static int? ParseOptionalMaxDepth(string? argumentsJson, string errorCode)
        {
            return ParseOptionalMaxDepth(ParseArgumentsObject(argumentsJson), errorCode);
        }

        internal static int? ParseOptionalMaxDepth(JObject arguments, string errorCode)
        {
            if (arguments == null)
            {
                throw new ArgumentNullException(nameof(arguments));
            }

            JToken? token = arguments["maxDepth"];
            if (token == null || token.Type == JTokenType.Null || token.Type == JTokenType.Undefined)
            {
                return null;
            }

            if (token.Type != JTokenType.Integer)
            {
                throw new CommandFailureException(errorCode, "`--max-depth` must be an integer.");
            }

            return token.Value<int>();
        }

        internal static JToken? ReadOptionalProperty(JObject? values, string propertyName)
        {
            if (values == null)
            {
                return null;
            }

            if (!values.TryGetValue(propertyName, StringComparison.OrdinalIgnoreCase, out JToken? token))
            {
                return null;
            }

            return IsNullLike(token) ? null : token;
        }

        internal static bool? ReadOptionalBoolean(JObject? values, string propertyName)
        {
            JToken? token = ReadOptionalProperty(values, propertyName);
            return token == null ? null : token.Value<bool>();
        }

        internal static float? ReadOptionalFloat(JObject? values, string propertyName)
        {
            JToken? token = ReadOptionalProperty(values, propertyName);
            return token == null ? null : token.Value<float>();
        }

        internal static string? ReadOptionalString(JObject? values, string propertyName)
        {
            JToken? token = ReadOptionalProperty(values, propertyName);
            return token == null ? null : token.Value<string>();
        }

        internal static JObject? ReadOptionalObject(JObject? values, string propertyName, string errorCode, string errorMessage)
        {
            JToken? token = ReadOptionalProperty(values, propertyName);
            if (token == null)
            {
                return null;
            }

            if (token is JObject obj)
            {
                return obj;
            }

            throw new CommandFailureException(errorCode, errorMessage);
        }

        internal static Vector3? MergeVector3(Vector3 current, JObject? values)
        {
            return InspectorUtility.MergeVector3(
                current,
                ReadOptionalFloat(values, "x"),
                ReadOptionalFloat(values, "y"),
                ReadOptionalFloat(values, "z"));
        }

        internal static NodeMutationAnalysis AnalyzeNodeMutationValues(JObject values)
        {
            if (values == null)
            {
                throw new ArgumentNullException(nameof(values));
            }

            var warnings = new List<string>();
            int recognizedLeafCount = AnalyzeNodeMutationObject(values, string.Empty, warnings);
            return new NodeMutationAnalysis(recognizedLeafCount, warnings.ToArray());
        }

        private static int AnalyzeNodeMutationObject(JObject values, string prefix, List<string> warnings)
        {
            int recognizedLeafCount = 0;
            foreach (JProperty property in values.Properties())
            {
                string path = BuildMutationPath(prefix, property.Name);
                if (MatchesName(property.Name, "name")
                    || MatchesName(property.Name, "active")
                    || MatchesName(property.Name, "tag")
                    || MatchesName(property.Name, "layer"))
                {
                    if (!IsNullLike(property.Value))
                    {
                        recognizedLeafCount++;
                    }

                    continue;
                }

                if (MatchesName(property.Name, "transform"))
                {
                    if (property.Value is JObject transformObject)
                    {
                        recognizedLeafCount += AnalyzeTransformMutationObject(transformObject, path, warnings);
                    }
                    else if (!IsNullLike(property.Value))
                    {
                        recognizedLeafCount++;
                    }

                    continue;
                }

                warnings.Add("Unknown key: " + path);
            }

            return recognizedLeafCount;
        }

        private static int AnalyzeTransformMutationObject(JObject values, string prefix, List<string> warnings)
        {
            int recognizedLeafCount = 0;
            foreach (JProperty property in values.Properties())
            {
                string path = BuildMutationPath(prefix, property.Name);
                if (MatchesName(property.Name, "localPosition")
                    || MatchesName(property.Name, "localRotationEuler")
                    || MatchesName(property.Name, "localScale"))
                {
                    if (property.Value is JObject vectorObject)
                    {
                        recognizedLeafCount += AnalyzeVector3MutationObject(vectorObject, path, warnings);
                    }
                    else if (!IsNullLike(property.Value))
                    {
                        recognizedLeafCount++;
                    }

                    continue;
                }

                warnings.Add("Unknown key: " + path);
            }

            return recognizedLeafCount;
        }

        private static int AnalyzeVector3MutationObject(JObject values, string prefix, List<string> warnings)
        {
            int recognizedLeafCount = 0;
            foreach (JProperty property in values.Properties())
            {
                if (MatchesName(property.Name, "x")
                    || MatchesName(property.Name, "y")
                    || MatchesName(property.Name, "z"))
                {
                    if (!IsNullLike(property.Value))
                    {
                        recognizedLeafCount++;
                    }

                    continue;
                }

                warnings.Add("Unknown key: " + BuildMutationPath(prefix, property.Name));
            }

            return recognizedLeafCount;
        }

        private static string BuildMutationPath(string prefix, string name)
        {
            return string.IsNullOrEmpty(prefix)
                ? name
                : prefix + "." + name;
        }

        private static bool MatchesName(string actual, string expected)
        {
            return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsNullLike(JToken token)
        {
            return token == null
                || token.Type == JTokenType.Null
                || token.Type == JTokenType.Undefined;
        }
    }

    internal sealed class NodeMutationAnalysis
    {
        internal NodeMutationAnalysis(int recognizedLeafCount, string[] warnings)
        {
            RecognizedLeafCount = recognizedLeafCount;
            Warnings = warnings ?? Array.Empty<string>();
        }

        internal int RecognizedLeafCount { get; }

        internal bool HasRecognizedKeys => RecognizedLeafCount > 0;

        internal string[] Warnings { get; }
    }
}
