using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace RevitMCPCommandSet.Utils
{
    public static class JsonSchemaGenerator
    {
        /// <summary>
        /// Generates and transforms a JSON Schema for the specified type
        /// </summary>
        /// <typeparam name="T">Type for which to generate the schema</typeparam>
        /// <param name="mainPropertyName">Name of the main property in the transformed schema</param>
        /// <returns>The transformed JSON Schema string</returns>
        public static string GenerateTransformedSchema<T>(string mainPropertyName)
        {
            return GenerateTransformedSchema<T>(mainPropertyName, false);
        }

        /// <summary>
        /// Generates and transforms a JSON Schema for the specified type, with optional ThinkingProcess support
        /// </summary>
        /// <typeparam name="T">Type for which to generate the schema</typeparam>
        /// <param name="mainPropertyName">Name of the main property in the transformed schema</param>
        /// <param name="includeThinkingProcess">Whether to add the ThinkingProcess property</param>
        /// <returns>The transformed JSON Schema string</returns>
        public static string GenerateTransformedSchema<T>(string mainPropertyName, bool includeThinkingProcess)
        {
            if (string.IsNullOrWhiteSpace(mainPropertyName))
                throw new ArgumentException("Main property name cannot be null or empty.", nameof(mainPropertyName));

            // Create the root schema
            JObject rootSchema = new JObject
            {
                ["type"] = "object",
                ["properties"] = new JObject(),
                ["required"] = new JArray(),
                ["additionalProperties"] = false
            };

            // Add the ThinkingProcess property when requested
            if (includeThinkingProcess)
            {
                AddProperty(rootSchema, "ThinkingProcess", new JObject { ["type"] = "string" }, true);
            }

            // Generate the target property's schema
            JObject mainPropertySchema = GenerateSchema(typeof(T));
            AddProperty(rootSchema, mainPropertyName, mainPropertySchema, true);

            // Recursively add "additionalProperties": false to every object
            AddAdditionalPropertiesFalse(rootSchema);

            // Return the formatted JSON Schema
            return JsonConvert.SerializeObject(rootSchema, Formatting.Indented);
        }

        /// <summary>
        /// Recursively generates a JSON Schema for the specified type
        /// </summary>
        private static JObject GenerateSchema(Type type)
        {
            if (type == typeof(string)) return new JObject { ["type"] = "string" };
            if (type == typeof(int) || type == typeof(long) || type == typeof(short)) return new JObject { ["type"] = "integer" };
            if (type == typeof(float) || type == typeof(double) || type == typeof(decimal)) return new JObject { ["type"] = "number" };
            if (type == typeof(bool)) return new JObject { ["type"] = "boolean" };

            // Handle Dictionary types first
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Dictionary<,>))
                return HandleDictionary(type);

            // Handle array and collection types
            if (type.IsArray || (typeof(IEnumerable).IsAssignableFrom(type) && type.IsGenericType))
            {
                Type itemType = type.IsArray ? type.GetElementType() : type.GetGenericArguments()[0];
                return new JObject
                {
                    ["type"] = "array",
                    ["items"] = GenerateSchema(itemType)
                };
            }

            // Handle class types
            if (type.IsClass)
            {
                var schema = new JObject
                {
                    ["type"] = "object",
                    ["properties"] = new JObject(),
                    ["required"] = new JArray(),
                    ["additionalProperties"] = false
                };

                foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    AddProperty(schema, prop.Name, GenerateSchema(prop.PropertyType), isRequired: true);
                }
                return schema;
            }

            // Treat all other types as strings by default
            return new JObject { ["type"] = "string" };
        }

        /// <summary>
        /// Handles Dictionary&lt;string, TValue&gt;, ensuring string keys and the correct value schema
        /// </summary>
        private static JObject HandleDictionary(Type type)
        {
            Type keyType = type.GetGenericArguments()[0];
            Type valueType = type.GetGenericArguments()[1];

            if (keyType != typeof(string))
            {
                throw new NotSupportedException("JSON Schema only supports dictionaries with string keys.");
            }

            return new JObject
            {
                ["type"] = "object",
                ["additionalProperties"] = GenerateSchema(valueType)
            };
        }

        /// <summary>
        /// Adds a property to a schema
        /// </summary>
        private static void AddProperty(JObject schema, string propertyName, JToken propertySchema, bool isRequired)
        {
            ((JObject)schema["properties"]).Add(propertyName, propertySchema);

            if (isRequired)
            {
                ((JArray)schema["required"]).Add(propertyName);
            }
        }

        /// <summary>
        /// Recursively adds "additionalProperties": false to objects that contain a "required" property
        /// </summary>
        private static void AddAdditionalPropertiesFalse(JToken token)
        {
            if (token.Type == JTokenType.Object)
            {
                var obj = (JObject)token;
                if (obj["required"] != null && obj["additionalProperties"] == null)
                {
                    obj["additionalProperties"] = false;
                }

                foreach (var property in obj.Properties())
                {
                    AddAdditionalPropertiesFalse(property.Value);
                }
            }
            else if (token.Type == JTokenType.Array)
            {
                foreach (var item in (JArray)token)
                {
                    AddAdditionalPropertiesFalse(item);
                }
            }
        }
    }
}
