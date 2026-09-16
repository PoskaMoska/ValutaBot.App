using System;

namespace ValutaBot.Core
{
    public enum Direction
    {
        Up,
        Down,
        Neutral
    }

    public static class DirectionExtensions
    {
        public static string ToSignal(this Direction direction) => direction switch
        {
            Direction.Up => "BUY",
            Direction.Down => "PUT",
            _ => "NEUTRAL"
        };

        public static string ToUiLabel(this Direction direction) => direction switch
        {
            Direction.Up => "ВВЕРХ",
            Direction.Down => "ВНИЗ",
            _ => "НЕЙТРАЛЬНО"
        };

        public static Direction FromScore(double score, double threshold = 0.02)
        {
            if (score > threshold) return Direction.Up;
            if (score < -threshold) return Direction.Down;
            return Direction.Neutral;
        }

        public static Direction FromMlConfidence(string directionString, double confidence, double threshold = 0.52)
        {
            if (directionString == "BUY" && confidence >= threshold) return Direction.Up;
            if (directionString == "PUT" && confidence >= threshold) return Direction.Down;
            return Direction.Neutral;
        }
    }
}
