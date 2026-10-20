#nullable enable
using Newtonsoft.Json.Linq;
using UnityCli.Protocol;

namespace UnityCliBridge.Bridge.Editor
{
    internal static class InspectorDefaultPruningUtility
    {
        internal static void PruneDefaultInspectableValues(JObject values)
        {
            JToken? token = values.First;
            while (token != null)
            {
                JToken? next = token.Next;
                var property = (JProperty)token;
                if (property.Value.Type == JTokenType.Boolean)
                {
                    if (SceneInspectValueRules.ShouldPruneBoolean(property.Name, property.Value.Value<bool>()))
                    {
                        property.Remove();
                    }
                }
                else if (ShouldOmitInspectableValue(property.Value))
                {
                    property.Remove();
                }

                token = next;
            }
        }

        private static bool ShouldOmitInspectableValue(JToken token)
        {
            switch (token.Type)
            {
                case JTokenType.Null:
                case JTokenType.Undefined:
                    return true;
                case JTokenType.Integer:
                    return token.Value<long>() == 0L;
                case JTokenType.Float:
                    // Strict equality intentional — only exact 0.0 is omitted, not near-zero values.
                    return token.Value<double>() == 0d;
                case JTokenType.Boolean:
                    return !token.Value<bool>();
                case JTokenType.String:
                    return string.IsNullOrEmpty(token.Value<string>());
                case JTokenType.Object:
                {
                    var obj = (JObject)token;
                    PruneDefaultInspectableValues(obj);
                    return !obj.HasValues;
                }
                case JTokenType.Array:
                {
                    var array = (JArray)token;
                    foreach (JToken item in array)
                    {
                        if (item is JObject itemObject)
                        {
                            PruneDefaultInspectableValues(itemObject);
                        }
                        else if (item is JArray itemArray)
                        {
                            PruneDefaultInspectableArray(itemArray);
                        }
                    }

                    return array.Count == 0;
                }
                default:
                    return false;
            }
        }

        private static void PruneDefaultInspectableArray(JArray values)
        {
            foreach (JToken value in values)
            {
                if (value is JObject obj)
                {
                    PruneDefaultInspectableValues(obj);
                }
                else if (value is JArray array)
                {
                    PruneDefaultInspectableArray(array);
                }
            }
        }
    }
}
