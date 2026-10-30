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
        string RegimeDescription,   // Описание состояния рынка
        bool HasBullishWick = false, // Тень отскока снизу (отбой от поддержки/EMA)
        bool HasBearishWick = false, // Тень отскока сверху (отбой от сопротивления/EMA)
        bool IsExtremeOverbought = false, // Экстремальный вылет вверх >2.8 сигмы (Mean Reversion)
        bool IsExtremeOversold = false    // Экстремальный вылет вниз <-2.8 сигмы (Mean Reversion)
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

            double tr = candles[i].High - candles[i].Low;
            if (i > 0)
            {
                double prevClose = candles[i - 1].Close;
                tr = Math.Max(tr, Math.Max(Math.Abs(candles[i].High - prevClose), Math.Abs(candles[i].Low - prevClose)));
            }
            sumTr += tr;
        }

        double std = Math.Sqrt(sumSqDiff / window);
        double currentPrice = candles[count - 1].Close;

        // Защита от деления на ноль при мертвом флэте, а также от поврежденных цен NaN/Infinity
        if (std < 1e-7 || currentPrice <= 0 || double.IsNaN(currentPrice) || double.IsInfinity(currentPrice) || double.IsNaN(std) || double.IsInfinity(std))
        {
            return new NoiseAuditResult(false, 0.0, 0.0, brokerNoiseFloorBps, "Полный штиль котировок (0 волатильности)");
        }

        // 4. Z-Score и волатильность в bps
        double zScore = (currentPrice - ema) / std;
        double avgTr = sumTr / window;
        double localAtrBps = (avgTr / currentPrice) * 10000.0;
        var lastCandle = candles[count - 1];
        double candleRange = lastCandle.High - lastCandle.Low;
        double candleRangeBps = (candleRange / currentPrice) * 10000.0;

        // Детекция аномального ценового спайка (Spike / Outlier):
        // Одиночная свеча размахом > 3.0x среднего True Range либо размах > 5x порога шума при высоком Z-score
        bool isSpike = (avgTr > 1e-7 && candleRange > 3.0 * avgTr) ||
                       (candleRangeBps > 5.0 * brokerNoiseFloorBps && Math.Abs(zScore) >= 2.2);

        // 5. Правило «Снайперского импульса»:
        // А) Амплитуда свечей превышает порог тикового шума брокера
        // Б) Z-score показывает наличие направленного импульса (|Z| >= 0.8)
        // В) Отсутствует аномальный спайк-выброс (не ловим нож на панических свечах)
        bool hasSufficientSpread = localAtrBps >= brokerNoiseFloorBps;
        bool hasDirectionalPush = Math.Abs(zScore) >= 0.8;

        bool isTradeable = hasSufficientSpread && hasDirectionalPush && !isSpike;

        // 6. Детекция теней отскока (Wick Rejection) на последней закрытой свече
        bool hasBullishWick = false;
        bool hasBearishWick = false;
        if (candleRange > 1e-7 && !isSpike)
        {
            double upperWick = lastCandle.High - Math.Max(lastCandle.Open, lastCandle.Close);
            double lowerWick = Math.Min(lastCandle.Open, lastCandle.Close) - lastCandle.Low;
            double body = Math.Abs(lastCandle.Close - lastCandle.Open);

            // Фильтрация доджи/волчков: тень должна быть асимметричной (доминировать над противоположной тенью),
            // а размах свечи должен быть не менее половины порога шума брокера
            bool hasMinAmplitude = candleRangeBps >= brokerNoiseFloorBps * 0.5;

            // Длинная нижняя тень: покупатели вытолкнули цену вверх
            hasBullishWick = hasMinAmplitude &&
                             lowerWick > upperWick * 1.25 &&
                             (lowerWick >= 1.2 * body || (lowerWick / candleRange >= 0.40));

            // Длинная верхняя тень: продавцы прижали цену вниз
            hasBearishWick = hasMinAmplitude &&
                             upperWick > lowerWick * 1.25 &&
                             (upperWick >= 1.2 * body || (upperWick / candleRange >= 0.40));
        }

        // 7. Экстремальный вылет цены от EMA (Mean Reversion затухание)
        bool isExtremeOverbought = zScore >= 2.8;
        bool isExtremeOversold = zScore <= -2.8;

        string regime;
        if (isSpike)
        {
            regime = "Аномальный спайк волатильности: вход заблокирован во избежание ложного пробоя";
        }
        else if (isTradeable)
        {
            regime = hasBullishWick ? "Импульс + Бычий отскок тени (Bullish Wick)" :
                     hasBearishWick ? "Импульс + Медвежий отскок тени (Bearish Wick)" :
                     "Импульс подтвержден: амплитуда перекрывает шум";
        }
        else
        {
            regime = !hasSufficientSpread 
                ? "Шумовой флэт: амплитуда свечи меньше порога шума" 
                : (isExtremeOverbought ? "Экстремальный перегрев (>2.8σ, возможен отскок вниз)" :
                   isExtremeOversold ? "Экстремальная перепроданность (<-2.8σ, возможен отскок вверх)" :
                   "Локальное затухание волатильности");
        }

        return new NoiseAuditResult(
            IsTradeableImpulse: isTradeable,
            ZScore: zScore,
            LocalAtrBps: localAtrBps,
            NoiseFloorBps: brokerNoiseFloorBps,
            RegimeDescription: regime,
            HasBullishWick: hasBullishWick,
            HasBearishWick: hasBearishWick,
            IsExtremeOverbought: isExtremeOverbought,
            IsExtremeOversold: isExtremeOversold
        );
    }
}
