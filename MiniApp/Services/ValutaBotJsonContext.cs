using System.Text.Json.Serialization;
using System.Collections.Generic;

namespace ValutaBot.MiniApp
{
    [JsonSourceGenerationOptions(NumberHandling = JsonNumberHandling.AllowReadingFromString, PropertyNameCaseInsensitive = true)]
    [JsonSerializable(typeof(double[][]))]
    [JsonSerializable(typeof(global::ValutaBot.MiniApp.MLPythonService.MLPythonPrediction))]
    [JsonSerializable(typeof(Dictionary<string, string>))]
    internal partial class ValutaBotJsonContext : JsonSerializerContext
    {
    }
}
