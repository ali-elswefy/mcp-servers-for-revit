using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Models.Links;
using RevitMCPCommandSet.Utils.ExternalEvents;

namespace RevitMCPCommandSet.Utils.Links
{
    /// <summary>
    /// Parses and validates the parameters of the linked-model commands. Free of Revit types.
    /// Anything malformed is a validation error, never a silent default.
    /// </summary>
    public static class LinkedRequestParser
    {
        public const string ListCommand = "list_linked_models";
        public const string QueryCommand = "query_linked_elements";
        public const string DetailsCommand = "get_linked_element_details";

        public static LinkedModelsRequest ParseList(JObject parameters, string requestId)
        {
            return new LinkedModelsRequest
            {
                RequestId = requestId,
                IncludeCounts = Bool(parameters, "includeCounts", ListCommand, requestId) ?? false
            };
        }

        public static LinkedElementQueryRequest ParseQuery(JObject parameters, string requestId)
        {
            const string cmd = QueryCommand;

            var request = new LinkedElementQueryRequest
            {
                RequestId = requestId,
                LinkInstanceId = Long(parameters, "linkInstanceId", cmd, requestId),
                LinkName = String(parameters, "linkName", cmd, requestId),
                Categories = StringList(parameters, "categories", cmd, requestId, allowEmpty: false),
                FamilyName = String(parameters, "familyName", cmd, requestId),
                TypeName = String(parameters, "typeName", cmd, requestId),
                NameContains = String(parameters, "nameContains", cmd, requestId),
                LevelName = String(parameters, "levelName", cmd, requestId),
                NearHostElementId = Long(parameters, "nearHostElementId", cmd, requestId),
                ParameterNames = StringList(parameters, "parameterNames", cmd, requestId, allowEmpty: true) ?? new List<string>(),
                CountOnly = Bool(parameters, "countOnly", cmd, requestId) ?? false
            };

            if (request.LinkInstanceId.HasValue && request.LinkName != null)
                throw CommandErrors.Validation("Pass either 'linkInstanceId' or 'linkName', not both.", cmd, requestId);

            request.BoundingBox = Box(parameters, cmd, requestId);
            if (request.BoundingBox != null && request.NearHostElementId.HasValue)
                throw CommandErrors.Validation("Pass either a bounding box or 'nearHostElementId', not both.", cmd, requestId);

            double? nearDistance = Double(parameters, "nearDistanceMm", cmd, requestId);
            if (nearDistance.HasValue && !request.NearHostElementId.HasValue)
                throw CommandErrors.Validation("'nearDistanceMm' needs 'nearHostElementId'.", cmd, requestId);
            if (nearDistance.HasValue && nearDistance.Value < 0)
                throw CommandErrors.Validation("'nearDistanceMm' must not be negative.", cmd, requestId);
            request.NearDistanceMm = nearDistance ?? 0;

            request.ParameterFilters = ParameterFilters(parameters, cmd, requestId);

            long? limit = Long(parameters, "limit", cmd, requestId);
            if (limit.HasValue && (limit.Value < 1 || limit.Value > LinkedElementQueryRequest.MaxLimit))
                throw CommandErrors.Validation($"'limit' must be between 1 and {LinkedElementQueryRequest.MaxLimit}.", cmd, requestId);
            request.Limit = (int)(limit ?? LinkedElementQueryRequest.DefaultLimit);

            // Listing every element of a link is never what a caller wants; counting everything per category is.
            if (!request.CountOnly && !request.HasCriteria)
            {
                throw CommandErrors.Validation(
                    "Specify at least one filter (categories, familyName, typeName, nameContains, levelName, a bounding box, nearHostElementId or parameterFilters), " +
                    "or set countOnly to get per-category counts.",
                    cmd, requestId);
            }

            return request;
        }

        public static LinkedElementDetailsRequest ParseDetails(JObject parameters, string requestId)
        {
            const string cmd = DetailsCommand;

            var request = new LinkedElementDetailsRequest
            {
                RequestId = requestId,
                LinkInstanceId = Long(parameters, "linkInstanceId", cmd, requestId),
                LinkName = String(parameters, "linkName", cmd, requestId),
                ElementId = Long(parameters, "elementId", cmd, requestId),
                UniqueId = String(parameters, "uniqueId", cmd, requestId),
                IncludeTypeParameters = Bool(parameters, "includeTypeParameters", cmd, requestId) ?? true,
                IncludeRelationships = Bool(parameters, "includeRelationships", cmd, requestId) ?? false
            };

            if (request.LinkInstanceId.HasValue && request.LinkName != null)
                throw CommandErrors.Validation("Pass either 'linkInstanceId' or 'linkName', not both.", cmd, requestId);

            if (request.ElementId.HasValue == (request.UniqueId != null))
                throw CommandErrors.Validation("Pass exactly one of 'elementId' and 'uniqueId'.", cmd, requestId);

            return request;
        }

        // ---------------------------------------------------------------- field readers

        private static bool IsAbsent(JToken token) => token == null || token.Type == JTokenType.Null;

        private static string String(JObject parameters, string name, string cmd, string requestId)
        {
            JToken token = parameters?[name];
            if (IsAbsent(token))
                return null;

            if (token.Type != JTokenType.String || string.IsNullOrWhiteSpace(token.Value<string>()))
                throw CommandErrors.Validation($"'{name}' must be a non-empty string.", cmd, requestId);

            return token.Value<string>().Trim();
        }

        private static long? Long(JObject parameters, string name, string cmd, string requestId)
        {
            JToken token = parameters?[name];
            if (IsAbsent(token))
                return null;

            bool isWholeNumber = token.Type == JTokenType.Integer ||
                                 (token.Type == JTokenType.Float && Math.Abs(token.Value<double>() % 1) < double.Epsilon);
            if (!isWholeNumber)
                throw CommandErrors.Validation($"'{name}' must be a whole number.", cmd, requestId);

            return token.Value<long>();
        }

        private static double? Double(JObject parameters, string name, string cmd, string requestId)
        {
            JToken token = parameters?[name];
            if (IsAbsent(token))
                return null;

            if (token.Type != JTokenType.Integer && token.Type != JTokenType.Float)
                throw CommandErrors.Validation($"'{name}' must be a number.", cmd, requestId);

            return token.Value<double>();
        }

        private static bool? Bool(JObject parameters, string name, string cmd, string requestId)
        {
            JToken token = parameters?[name];
            if (IsAbsent(token))
                return null;

            if (token.Type != JTokenType.Boolean)
                throw CommandErrors.Validation($"'{name}' must be a boolean.", cmd, requestId);

            return token.Value<bool>();
        }

        private static List<string> StringList(JObject parameters, string name, string cmd, string requestId, bool allowEmpty)
        {
            JToken token = parameters?[name];
            if (IsAbsent(token))
                return null;

            var array = token as JArray;
            if (array == null || array.Any(item => item.Type != JTokenType.String || string.IsNullOrWhiteSpace(item.Value<string>())))
                throw CommandErrors.Validation($"'{name}' must be an array of non-empty strings.", cmd, requestId);

            if (array.Count == 0 && !allowEmpty)
            {
                throw CommandErrors.Validation(
                    $"'{name}' must not be empty. Omit it for no restriction, or list at least one value.", cmd, requestId);
            }

            return array.Select(item => item.Value<string>().Trim()).ToList();
        }

        private static MmBox Box(JObject parameters, string cmd, string requestId)
        {
            MmPoint min = Point(parameters, "boundingBoxMin", cmd, requestId);
            MmPoint max = Point(parameters, "boundingBoxMax", cmd, requestId);
            if (min == null && max == null)
                return null;

            if (min == null || max == null)
                throw CommandErrors.Validation("'boundingBoxMin' and 'boundingBoxMax' must be given together.", cmd, requestId);

            if (min.X > max.X || min.Y > max.Y || min.Z > max.Z)
                throw CommandErrors.Validation("'boundingBoxMin' must be less than or equal to 'boundingBoxMax' on every axis.", cmd, requestId);

            return new MmBox { Min = min, Max = max };
        }

        private static MmPoint Point(JObject parameters, string name, string cmd, string requestId)
        {
            JToken token = parameters?[name];
            if (IsAbsent(token))
                return null;

            var obj = token as JObject;
            double? x = obj == null ? null : Coordinate(obj, "x");
            double? y = obj == null ? null : Coordinate(obj, "y");
            double? z = obj == null ? null : Coordinate(obj, "z");
            if (!x.HasValue || !y.HasValue || !z.HasValue)
                throw CommandErrors.Validation($"'{name}' must be an object with numeric x, y and z in millimeters.", cmd, requestId);

            return new MmPoint(x.Value, y.Value, z.Value);
        }

        private static double? Coordinate(JObject obj, string axis)
        {
            JToken token = obj.GetValue(axis, StringComparison.OrdinalIgnoreCase);
            return token != null && (token.Type == JTokenType.Integer || token.Type == JTokenType.Float)
                ? token.Value<double>()
                : (double?)null;
        }

        private static List<ParameterFilterRule> ParameterFilters(JObject parameters, string cmd, string requestId)
        {
            var rules = new List<ParameterFilterRule>();
            JToken token = parameters?["parameterFilters"];
            if (IsAbsent(token))
                return rules;

            var array = token as JArray;
            if (array == null)
                throw CommandErrors.Validation("'parameterFilters' must be an array of { name, operator, value } objects.", cmd, requestId);

            for (int i = 0; i < array.Count; i++)
            {
                var item = array[i] as JObject;
                string name = item?["name"]?.Type == JTokenType.String ? item["name"].Value<string>().Trim() : null;
                string op = item?["operator"]?.Type == JTokenType.String ? item["operator"].Value<string>().Trim() : null;
                if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(op))
                    throw CommandErrors.Validation($"parameterFilters[{i}] needs a string 'name' and a string 'operator'.", cmd, requestId);

                ParameterOperator parsed;
                if (!TryParseOperator(op, out parsed))
                {
                    throw CommandErrors.Validation(
                        $"parameterFilters[{i}] has unknown operator '{op}'. Use equals, notEquals, contains, startsWith, isEmpty or hasValue.",
                        cmd, requestId);
                }

                string value = null;
                if (parsed != ParameterOperator.IsEmpty && parsed != ParameterOperator.HasValue)
                {
                    JToken valueToken = item["value"];
                    bool scalar = valueToken != null && (valueToken.Type == JTokenType.String || valueToken.Type == JTokenType.Integer ||
                                                         valueToken.Type == JTokenType.Float || valueToken.Type == JTokenType.Boolean);
                    if (!scalar)
                        throw CommandErrors.Validation($"parameterFilters[{i}] with operator '{op}' needs a 'value'.", cmd, requestId);

                    value = valueToken.Type == JTokenType.Boolean
                        ? (valueToken.Value<bool>() ? "Yes" : "No") // Revit displays Yes/No parameters this way
                        : valueToken.Value<string>();
                }

                rules.Add(new ParameterFilterRule { Name = name, Operator = parsed, Value = value });
            }

            return rules;
        }

        private static bool TryParseOperator(string text, out ParameterOperator op)
        {
            switch (text.ToLowerInvariant())
            {
                case "equals": op = ParameterOperator.Equals; return true;
                case "notequals": op = ParameterOperator.NotEquals; return true;
                case "contains": op = ParameterOperator.Contains; return true;
                case "startswith": op = ParameterOperator.StartsWith; return true;
                case "isempty": op = ParameterOperator.IsEmpty; return true;
                case "hasvalue": op = ParameterOperator.HasValue; return true;
                default: op = ParameterOperator.Equals; return false;
            }
        }
    }
}
