using System;
using System.Collections.Concurrent;

namespace ValutaBot.MiniApp;

/// <summary>
/// Drawdown Protection & Anti-Overfitting Regime Protection Engine.
/// Prevents consecutive losses during sudden market regime shifts & news events
/// by tracking drawdown cooloff phases.
/// </summary>
public class WalkForwardValidationEngine : IWalkForwardValidationEngine
{
    private class CooloffState
    {
        public int ConsecutiveLosses { get; set; }
        public DateTime CooloffUntil { get; set; } = DateTime.MinValue;

        // Скользящее окно последних 10 сделок для расчёта rolling win rate.
        // Ring buffer: true = победа, false = поражение.
        public bool[] RecentOutcomes { get; set; } = new bool[10];
        public int OutcomeIndex { get; set; }
        public int OutcomeCount { get; set; }
    }

    private readonly ConcurrentDictionary<SignalKey, CooloffState> _cooloffMap = new();
    private static readonly string _savePath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", "walkforward_state.json");
    private static readonly System.Threading.SemaphoreSlim _saveLock = new(1, 1);

    public WalkForwardValidationEngine()
    {
        LoadState();
    }

    private void LoadState()
    {
        try
        {
            if (System.IO.File.Exists(_savePath))
            {
                var json = System.IO.File.ReadAllText(_savePath);
                var dict = System.Text.Json.JsonSerializer.Deserialize<System.Collections.Generic.Dictionary<string, CooloffState>>(json);
                if (dict != null)
                {
                    foreach (var kvp in dict)
                    {
                        int lastUnderscore = kvp.Key.LastIndexOf('_');
                        if (lastUnderscore > 0 && lastUnderscore < kvp.Key.Length - 1)
                        {
                            string asset = kvp.Key[..lastUnderscore];
                            string timeframe = kvp.Key[(lastUnderscore + 1)..];
                            var key = new SignalKey(asset, timeframe);
                            _cooloffMap[key] = kvp.Value;
                        }
                    }
                    BotLogger.Info($"[WalkForward] Restored {dict.Count} drawdown states from file.");
                }
            }
        }
        catch (Exception ex)
        {
            BotLogger.Error("[WalkForward] Failed to load state", ex);
        }
    }

    private void SaveStateAsync()
    {
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            if (!await _saveLock.WaitAsync(0)) return;
            try
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_savePath)!);
                var dict = new System.Collections.Generic.Dictionary<string, CooloffState>();
                foreach (var kvp in _cooloffMap)
                {
                    // Сохраняем только активные или недавние кулоффы (чистим мусор)
                    if (kvp.Value.ConsecutiveLosses > 0 || kvp.Value.CooloffUntil > DateTime.UtcNow.AddHours(-1))
                    {
                        dict[kvp.Key.ToString()] = kvp.Value;
                    }
                }
                var json = System.Text.Json.JsonSerializer.Serialize(dict);
                string tmpPath = _savePath + ".tmp";
                await System.IO.File.WriteAllTextAsync(tmpPath, json);
                System.IO.File.Move(tmpPath, _savePath, overwrite: true);
            }
            catch (Exception ex)
            {
                BotLogger.Error("[WalkForward] Failed to save state", ex);
            }
            finally
            {
                _saveLock.Release();
            }
        });
    }

    /// <summary>
    /// Проверяет активен ли cooloff для данного актива/таймфрейма.
    /// </summary>
    public WalkForwardResult ValidateWalkForward(string asset, string timeframe)
    {
        var key = new SignalKey(asset, timeframe);
        var cooloff = _cooloffMap.GetOrAdd(key, _ => new CooloffState());

        bool isCooloffActive;
        DateTime cooloffUntil;
        lock (cooloff)
        {
            cooloffUntil    = cooloff.CooloffUntil;
            isCooloffActive = DateTime.UtcNow < cooloffUntil;
        }

        if (isCooloffActive)
        {
            BotLogger.Warn($"[Drawdown Protection] Cooloff active for {key} until {cooloffUntil:HH:mm:ss}.");
            return new WalkForwardResult(
                IsOverfitted:     true,
                IsCooloffActive:  true,
                WeightMultiplier: 0.10,
                StatusReasoning:  "Фаза охлаждения после серии убытков (Drawdown Protection Active).",
                CooloffUntil:     cooloffUntil
            );
        }

        return new WalkForwardResult(
            IsOverfitted:     false,
            IsCooloffActive:  false,
            WeightMultiplier: 1.0,
            StatusReasoning:  "Авто-калибровка весов (AutoCalibrationEngine активен)."
        );
    }

    private const int MaxConsecutiveLosses = 5;
    private const double MinRollingWinRate = 0.35;
    private const int CooloffDurationMinutes = 10;

    /// <summary>
    /// Records trade outcome to manage drawdown cooloff phase.
    /// Triggers a 10-minute cooloff on 5 consecutive losses or when 10-trade rolling win rate drops below 35%.
    /// </summary>
    public void RecordTradeOutcome(string asset, string timeframe, bool isWin)
    {
        var key = new SignalKey(asset, timeframe);
        var state = _cooloffMap.GetOrAdd(key, _ => new CooloffState());

        lock (state)
        {
            if (isWin)
            {
                state.ConsecutiveLosses = 0;
            }
            else
            {
                state.ConsecutiveLosses++;
                if (state.ConsecutiveLosses >= MaxConsecutiveLosses)
                {
                    state.CooloffUntil = DateTime.UtcNow.AddMinutes(CooloffDurationMinutes);
                    state.ConsecutiveLosses = 0;
                    BotLogger.Warn($"[Drawdown Protection] {MaxConsecutiveLosses} consecutive losses for {key}. Cooloff {CooloffDurationMinutes}m until {state.CooloffUntil:HH:mm:ss}");
                }
            }

            // Rolling win-rate protection over the last 10 trades
            state.RecentOutcomes[state.OutcomeIndex] = isWin;
            state.OutcomeIndex = (state.OutcomeIndex + 1) % 10;
            if (state.OutcomeCount < 10) state.OutcomeCount++;

            if (state.OutcomeCount >= 10 && DateTime.UtcNow >= state.CooloffUntil)
            {
                int wins = 0;
                for (int i = 0; i < state.OutcomeCount; i++) if (state.RecentOutcomes[i]) wins++;
                double rollingWinRate = (double)wins / state.OutcomeCount;

                if (rollingWinRate < MinRollingWinRate)
                {
                    state.CooloffUntil = DateTime.UtcNow.AddMinutes(CooloffDurationMinutes);
                    state.ConsecutiveLosses = 0;
                    // Reset ring buffer after trigger so stale outcomes do not immediately re-lock after cooloff
                    state.OutcomeCount = 0;
                    state.OutcomeIndex = 0;
                    BotLogger.Warn($"[Drawdown Protection] Rolling win rate {rollingWinRate:P0} < {MinRollingWinRate:P0} for {key}. Cooloff {CooloffDurationMinutes}m until {state.CooloffUntil:HH:mm:ss}. Buffer reset.");
                }
            }
        }
        SaveStateAsync();
    }

    public readonly record struct SignalKey(string Asset, string Timeframe)
    {
        public override string ToString() => $"{Asset}_{Timeframe}";
    }

    public readonly record struct WalkForwardResult(
        bool IsOverfitted,
        bool IsCooloffActive,
        double WeightMultiplier,
        string StatusReasoning,
        DateTime CooloffUntil = default
    );
}

