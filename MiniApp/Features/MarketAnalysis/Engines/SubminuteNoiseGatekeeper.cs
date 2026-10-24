using System;
using System.Collections.Generic;
using ValutaBot.MiniApp;

namespace ValutaBot.MiniApp.Features.MarketAnalysis;

public static class SubminuteNoiseGatekeeper
{
    public readonly record struct NoiseAuditResult(
        bool IsTradeableImpulse,    // Пробит ли порог шума для снайперского входа (Golden Setup)
        double ZScore,              // Отклонение текущей цены от EMA в сигмах
        double LocalAtrBps,         // Локальный размах свечи в базисных пунктах (bps)
        double NoiseFloorBps,       // Порог шума брокера
        string RegimeDescription    // Описание состояния рынка
    );

    /// <summary>
    /// Оценивает качество микро-структуры и амплитуду свечей относительно брокерского шума Pocket Option.
    /// Не блокирует выдачу сигнала (бот всегда дает вердикт), но валидирует статус идеального сетапа (Golden Setup).
    /// </summary>
    public static NoiseAuditResult AuditCandleStream(
        IReadOnlyList<MiniAppController.OhlcCandle> candles,
        string timeframe = "s10")
    {
        if (candles == null || candles.Count < 10)
        {
            return new NoiseAuditResult(false, 0.0, 0.0, 1.0, "Недостаточно свечей для оценки микро-структуры");
        }

        int count = candles.Count;

        // 1. Порог микро-шума брокера Pocket Option (в bps = 0.0001)
        double brokerNoiseFloorBps = (timeframe?.ToLowerInvariant()) switch
        {
            "s5"  => 0.8,
            "s10" => 1.0,
            "s15" => 1.2,
            "s30" => 1.5,
            _     => 2.0
        };

        // 2. Расчет быстрой 7-периодной EMA цены закрытия без запаздывания SMA
        double alpha = 2.0 / (7.0 + 1.0);
        double ema = candles[0].Close;
        for (int i = 1; i < count; i++)
        {
            ema = alpha * candles[i].Close + (1.0 - alpha) * ema;
        }

        // 3. Расчет локальной дисперсии и среднего размаха (True Range) за последние 10 баров
        int window = Math.Min(10, count);
        double sumSqDiff = 0.0;
        double sumTr = 0.0;

        for (int i = count - window; i < count; i++)
        {
            double diff = candles[i].Close - ema;
            sumSqDiff += diff * diff;

            double tr = Math.Max(
                candles[i].High - candles[i].Low,
                i > 0 ? Math.Abs(candles[i].High - candles[i - 1].Close) : 0.0
            );
            sumTr += tr;
        }

        double std = Math.Sqrt(sumSqDiff / window);
        double currentPrice = candles[count - 1].Close;

        // Защита от деления на ноль при мертвом флэте
        if (std < 1e-7 || currentPrice <= 0)
        {
            return new NoiseAuditResult(false, 0.0, 0.0, brokerNoiseFloorBps, "Полный штиль котировок (0 волатильности)");
        }

        // 4. Z-Score и волатильность в bps
        double zScore = (currentPrice - ema) / std;
        double avgTr = sumTr / window;
        double localAtrBps = (avgTr / currentPrice) * 10000.0;

        // 5. Правило «Снайперского импульса»:
        // А) Амплитуда свечей превышает порог тикового шума брокера
        // Б) Z-score показывает наличие направленного импульса (|Z| >= 0.8)
        bool hasSufficientSpread = localAtrBps >= brokerNoiseFloorBps;
        bool hasDirectionalPush = Math.Abs(zScore) >= 0.8 && Math.Abs(zScore) <= 3.5;

        bool isTradeable = hasSufficientSpread && hasDirectionalPush;

        string regime = isTradeable
            ? "Импульс подтвержден: амплитуда перекрывает шум"
            : (!hasSufficientSpread 
                ? "Шумовой флэт: амплитуда свечи меньше порога шума" 
                : "Локальное затухание или экстремальный перегрев");

        return new NoiseAuditResult(isTradeable, zScore, localAtrBps, brokerNoiseFloorBps, regime);
    }
}
