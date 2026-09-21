using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using MiniAppController = ValutaBot.MiniApp.MiniAppController;

namespace ValutaBot.Tests.Replay;

/// <summary>
/// Загружает JSON-слепки рыночных сценариев из папки TestData/Scenarios
/// и конвертирует их в OhlcCandle[] для прогона через движки.
/// </summary>
public static class TestScenarioLoader
{
    private static readonly string ScenariosDir = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory,
        "TestData", "Scenarios");

    public static MarketScenario Load(string scenarioName)
    {
        string filePath = Path.Combine(ScenariosDir, $"{scenarioName}.json");
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"Сценарий '{scenarioName}' не найден: {filePath}");

        string json = File.ReadAllText(filePath);
        var raw = JsonSerializer.Deserialize<RawScenario>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        if (raw == null)
            throw new InvalidDataException($"Не удалось десериализовать сценарий '{scenarioName}'");

        var candles = new List<MiniAppController.OhlcCandle>();
        foreach (var c in raw.Candles)
        {
            candles.Add(new MiniAppController.OhlcCandle(
                Open:      c.O,
                High:      c.H,
                Low:       c.L,
                Close:     c.C,
                Volume:    c.V,
                Timestamp: DateTime.Parse(c.Ts, null,
                    System.Globalization.DateTimeStyles.AdjustToUniversal)));
        }

        return new MarketScenario
        {
            Name        = raw.Name ?? scenarioName,
            Description = raw.Description ?? "",
            Asset       = raw.Asset ?? "EUR/USD",
            Timeframe   = raw.Timeframe ?? "1m",
            Candles     = candles.ToArray(),
            CurrentPrice = candles.Count > 0 ? candles[^1].Close : 1.0,

            // Expected values for assertions
            ExpectedOrderFlowState       = raw.ExpectedOrderFlowState,
            ExpectedSmcBosDirection      = raw.ExpectedSmcBosDirection,
            ExpectedSmcSweepDirection    = raw.ExpectedSmcSweepDirection,
            ExpectedTaDirection          = raw.ExpectedTaDirection,
            ExpectedAutoCalibRegime      = raw.ExpectedAutoCalibRegime,
            ExpectedNoNaN                = raw.ExpectedNoNaN,
            ExpectedNoInfinity           = raw.ExpectedNoInfinity,
            ExpectedNoException          = raw.ExpectedNoException,
            ExpectedScoreMin             = raw.ExpectedScoreInRange?.GetValueOrDefault("min") ?? -2.0,
            ExpectedScoreMax             = raw.ExpectedScoreInRange?.GetValueOrDefault("max") ?? 2.0,
        };
    }

    // ─── DTOs ────────────────────────────────────────────────────────────────
    private sealed class RawScenario
    {
        public string? Name        { get; set; }
        public string? Description { get; set; }
        public string? Asset       { get; set; }
        public string? Timeframe   { get; set; }

        public string? ExpectedOrderFlowState    { get; set; }
        public string? ExpectedSmcBosDirection   { get; set; }
        public string? ExpectedSmcSweepDirection { get; set; }
        public string? ExpectedTaDirection       { get; set; }
        public string? ExpectedAutoCalibRegime   { get; set; }
        public bool    ExpectedNoNaN             { get; set; }
        public bool    ExpectedNoInfinity        { get; set; }
        public bool    ExpectedNoException       { get; set; }
        public Dictionary<string, double>? ExpectedScoreInRange { get; set; }

        public List<RawCandle> Candles { get; set; } = new();
    }

    private sealed class RawCandle
    {
        public double O  { get; set; }
        public double H  { get; set; }
        public double L  { get; set; }
        public double C  { get; set; }
        public double V  { get; set; }
        public string Ts { get; set; } = "";
    }
}

/// <summary>
/// Готовый рыночный сценарий с OhlcCandle[] и ожидаемыми значениями для ассертов.
/// </summary>
public sealed class MarketScenario
{
    public string Name        { get; init; } = "";
    public string Description { get; init; } = "";
    public string Asset       { get; init; } = "";
    public string Timeframe   { get; init; } = "";
    public MiniAppController.OhlcCandle[] Candles     { get; init; } = Array.Empty<MiniAppController.OhlcCandle>();
    public double CurrentPrice { get; init; }

    public string? ExpectedOrderFlowState    { get; init; }
    public string? ExpectedSmcBosDirection   { get; init; }
    public string? ExpectedSmcSweepDirection { get; init; }
    public string? ExpectedTaDirection       { get; init; }
    public string? ExpectedAutoCalibRegime   { get; init; }
    public bool    ExpectedNoNaN             { get; init; }
    public bool    ExpectedNoInfinity        { get; init; }
    public bool    ExpectedNoException       { get; init; }
    public double  ExpectedScoreMin          { get; init; } = -2.0;
    public double  ExpectedScoreMax          { get; init; } =  2.0;
}
