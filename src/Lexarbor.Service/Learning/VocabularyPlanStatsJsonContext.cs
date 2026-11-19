using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lexarbor.Learning.Service;

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(VocabularyPlanStatsResponse))]
internal partial class VocabularyPlanStatsJsonContext : JsonSerializerContext;
