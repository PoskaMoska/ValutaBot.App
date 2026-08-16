using System.Text.Json.Serialization;
using System.Collections.Generic;

namespace ValutaBot.MiniApp
{
    [JsonSourceGenerationOptions(NumberHandling = JsonNumberHandling.AllowReadingFromString, PropertyNameCaseInsensitive = true)]
    [JsonSerializable(typeof(double[][]))]
    [JsonSerializable(typeof(global::ValutaBot.MiniApp.MLPythonService.PredictResponseDto))]
    [JsonSerializable(typeof(Dictionary<string, string>))]
    [JsonSerializable(typeof(global::ValutaBot.MiniApp.TwelveDataService.TwelveDataResponse))]
    [JsonSerializable(typeof(global::ValutaBot.MiniApp.TwelveDataService.TwelveDataPriceResponse))]
    internal partial class ValutaBotJsonContext : JsonSerializerContext
    {
    }
}
