using System;

namespace ValutaBot.MiniApp
{
    public static class ConfluenceFormattingService
    {
        public static (int Boost, string Label) GetMtfLabelAndBoost(double confluenceRatio)
        {
            return confluenceRatio switch
            {
                >= 0.99 => (15, "?? ИДЕАЛЬНЫЙ СИГНАЛ (3 ТФ - 100%)"),
                >= 0.65 => (7, "?? СИЛЬНЫЙ СИГНАЛ (2 ТФ - 67%)"),
                _ => (0, "?? СЛАБЫЙ СИГНАЛ (1 ТФ - 33%)")
            };
        }

        public static string BuildCombinedReasoningText(SmcSignal smcSignal, OrderflowSignal ofSignal, MlSignal mlSignal)
        {
            string modelAccText = mlSignal.Accuracy.HasValue
                ? $" [Точность {Math.Round(mlSignal.Accuracy.Value * 100, 1)}%]"
                : "";

            string smcText = !string.IsNullOrEmpty(smcSignal.Reasoning)
                ? $"• ?? SMC Структура: {smcSignal.Reasoning}"
                : "• ?? SMC Структура: недостаточно данных";

            string flowText = !string.IsNullOrEmpty(ofSignal.Description)
                ? $"• ?? Order Flow & CVD: {ofSignal.Description}"
                : "• ?? Order Flow & CVD: нет выраженных объемов";

            string lgbmText = !string.IsNullOrEmpty(mlSignal.Direction) && mlSignal.Direction != "NEUTRAL"
                ? $"• ?? Нейросеть (LightGBM): {(mlSignal.Direction == "BUY" ? "ВВЕРХ ??" : "ВНИЗ ??")} ({Math.Round(mlSignal.Confidence * 100)}% уверенности){modelAccText}"
                : (mlSignal.ModelVersion == "disabled"
                    ? "• ?? Нейросеть (LightGBM): Отключена пользователем"
                    : mlSignal.ModelVersion == "forex-only"
                        ? "• ?? Нейросеть (LightGBM): Недоступна для крипты"
                        : mlSignal.ModelVersion == "not-trained"
                            ? "• ?? Нейросеть (LightGBM): Модель обучается (зайдите через пару минут)"
                            : mlSignal.ModelVersion == "offline"
                                ? "• ?? Нейросеть (LightGBM): Сервер недоступен (Оффлайн)"
                                : $"• ?? Нейросеть (LightGBM): НЕЙТРАЛЬНО (0% уверенности){modelAccText}");

            return $"{smcText}\n{flowText}\n{lgbmText}";
        }
    }
}
