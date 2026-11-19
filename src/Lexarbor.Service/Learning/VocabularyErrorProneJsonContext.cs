using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lexarbor.Learning.Service;

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(VocabularyErrorProneResponse))]
internal partial class VocabularyErrorProneJsonContext : JsonSerializerContext;
