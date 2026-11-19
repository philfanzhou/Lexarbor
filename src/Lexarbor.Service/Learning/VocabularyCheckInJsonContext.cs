using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lexarbor.Learning.Service;

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(VocabularyCheckInResponse))]
internal partial class VocabularyCheckInJsonContext : JsonSerializerContext;
