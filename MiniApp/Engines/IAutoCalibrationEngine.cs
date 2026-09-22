using System;

namespace ValutaBot.MiniApp;

public interface IAutoCalibrationEngine
{
    AutoCalibrationEngine.MarketRegime DetectMarketRegime(double adx, double volRatio, double rsi, ReadOnlySpan<double> prices = default);
    double GetCalibratedRegimeWeight(string sourceName, string asset, string timeframe, AutoCalibrationEngine.MarketRegime regime);
    void RecordSourceOutcome(string sourceName, string asset, string timeframe, bool isWin);
    double GetEmpiricalWinRate(string sourceName, string asset, string timeframe);
    string GetStatsReport(string sourceName, string asset, string timeframe);
    // L2-FIX: Персистентность EMA-весов
    void RestoreState(string sourceName, string asset, string timeframe, int totalTrades, double emaWinRate);
    IEnumerable<(AutoCalibrationEngine.SignalKey key, int totalTrades, double emaWinRate)> GetAllStats();
}
