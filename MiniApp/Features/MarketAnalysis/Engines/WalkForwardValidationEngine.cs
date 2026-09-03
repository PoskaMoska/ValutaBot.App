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
        public readonly bool[] RecentOutcomes = new bool[10];
        public int OutcomeIndex { get; set; }
        public int OutcomeCount { get; set; }
    }

    private readonly ConcurrentDictionary<SignalKey, CooloffState> _cooloffMap = new();

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

    /// <summary>
    /// Records trade outcome to manage drawdown cooloff phase.
    /// Triggers 15-minute cooloff if 3 consecutive losses occur.
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
                if (state.ConsecutiveLosses >= 3)
                {
                    state.CooloffUntil = DateTime.UtcNow.AddMinutes(15);
                    state.ConsecutiveLosses = 0;
                    BotLogger.Warn($"[Drawdown Protection] 3 consecutive losses for {key}. Cooloff until {state.CooloffUntil:HH:mm:ss}");
                }
            }

            // Rolling win-rate защита: обновляем ring buffer и проверяем win rate последних 10 сделок.
            // Если < 35% побед — триггер cooloff (10 минут).
            // До этого: 5 потерь из 7 (результат 71% lose) не триггерил систему если между ними были победы.
            state.RecentOutcomes[state.OutcomeIndex] = isWin;
            state.OutcomeIndex = (state.OutcomeIndex + 1) % 10;
            if (state.OutcomeCount < 10) state.OutcomeCount++;

            if (state.OutcomeCount >= 10 && DateTime.UtcNow >= state.CooloffUntil)
            {
                int wins = 0;
                for (int i = 0; i < state.OutcomeCount; i++) if (state.RecentOutcomes[i]) wins++;
                double rollingWinRate = (double)wins / state.OutcomeCount;

                if (rollingWinRate < 0.35)
                {
                    state.CooloffUntil = DateTime.UtcNow.AddMinutes(10);
                    state.ConsecutiveLosses = 0;
                    // FIX H-5: Reset ring buffer after cooloff trigger.
                    // Without this, the next trade after cooloff re-checks the same
                    // stale 10-trade window, immediately triggers another 10-min cooloff,
                    // trapping the bot in "1 trade per 10 minutes" mode all day.
                    state.OutcomeCount = 0;
                    state.OutcomeIndex = 0;
                    BotLogger.Warn($"[Drawdown Protection] Rolling win rate {rollingWinRate:P0} < 35% for {key}. Cooloff 10 min until {state.CooloffUntil:HH:mm:ss}. Buffer reset.");
                }
            }
        }
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

