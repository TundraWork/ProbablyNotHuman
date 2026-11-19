using System;
using System.Linq;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace ProbablyNotHuman
{
    internal static class ActionSchema
    {
        public static void Validate(JToken value, JObject schema, string path = "arguments")
        {
            string type = (string)schema["type"];
            bool valid = type == null || (type == "object" && value is JObject) || (type == "array" && value is JArray) || (type == "string" && value.Type == JTokenType.String) || (type == "boolean" && value.Type == JTokenType.Boolean) || (type == "integer" && value.Type == JTokenType.Integer) || (type == "number" && (value.Type == JTokenType.Integer || value.Type == JTokenType.Float));
            if (!valid) Fail(path, "must have type " + type);
            if (schema["enum"] is JArray choices)
            {
                bool found = false;
                foreach (var choice in choices) if (JToken.DeepEquals(value, choice)) found = true;
                if (!found) Fail(path, "is not an allowed value (allowed: " + string.Join(", ", choices.Select(choice => choice.ToString(Newtonsoft.Json.Formatting.None))) + ")");
            }
            if (value is JObject obj)
            {
                var properties = schema["properties"] as JObject;
                if (schema["required"] is JArray required)
                    foreach (var key in required)
                        if (obj[(string)key] == null)
                        {
                            // Name what was given instead, so a misnamed argument is obvious.
                            var unknown = new List<string>();
                            foreach (var given in obj.Properties()) if (properties?[given.Name] == null) unknown.Add(given.Name);
                            var accepted = new List<string>();
                            if (properties != null) foreach (var known in properties.Properties()) accepted.Add(known.Name);
                            Fail(path + "." + (string)key, "is required" + (unknown.Count > 0 ? " (got " + string.Join(", ", unknown) + "; accepted: " + string.Join(", ", accepted) + ")" : ""));
                        }
                foreach (var property in obj.Properties())
                {
                    if (properties?[property.Name] is JObject child) Validate(property.Value, child, path + "." + property.Name);
                    else if (schema["additionalProperties"]?.Type == JTokenType.Boolean && !(bool)schema["additionalProperties"]) Fail(path + "." + property.Name, "is not an accepted argument");
                }
            }
            if (value is JArray array)
            {
                if (schema["minItems"] != null && array.Count < (int)schema["minItems"]) Fail(path, "has too few items");
                if (schema["maxItems"] != null && array.Count > (int)schema["maxItems"]) Fail(path, "has too many items");
                if (schema["items"] is JObject itemSchema) for (int i = 0; i < array.Count; i++) Validate(array[i], itemSchema, path + "[" + i + "]");
            }
            if (value.Type == JTokenType.Integer || value.Type == JTokenType.Float)
            {
                double number = (double)value;
                if (double.IsNaN(number) || double.IsInfinity(number)) Fail(path, "must be finite");
                if (schema["minimum"] != null && number < (double)schema["minimum"]) Fail(path, "is below minimum");
                if (schema["maximum"] != null && number > (double)schema["maximum"]) Fail(path, "is above maximum");
            }
        }
        private static void Fail(string path, string message) => throw new BridgeException("invalid_arguments", path + " " + message + ".");
    }
}
