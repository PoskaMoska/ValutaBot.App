namespace ValutaBot.MiniApp;

public class TradingBotSettings
{
    public double MlWeight { get; set; } = 0.6;
    public double MathWeight { get; set; } = 0.4;
    public int FastFailTimeoutSeconds { get; set; } = 1;
    public int HttpRetryDelayMs { get; set; } = 500;
    public int MaxHttpRetries { get; set; } = 1;

    public bool EnableMachineLearning { get; set; } = true;
    public bool EnableSmc { get; set; } = true;
    public bool EnableOrderFlow { get; set; } = true;
    public bool EnableAutoCalibration { get; set; } = true;

    // CircuitBreaker settings (must not be hardcoded in services)
    public int CircuitBreakerWindowSize { get; set; } = 10;                 // WINDOW_SIZE
    public int CircuitBreakerMaxConsecutiveLosses { get; set; } = 3;        // MAX_CONSECUTIVE_LOSSES
    public double CircuitBreakerMinWinRate { get; set; } = 0.40;            // MIN_WIN_RATE
    public int CircuitBreakerCooldownMinutes { get; set; } = 120;           // COOLDOWN_MINUTES (2 hours)
    public int CircuitBreakerDbCacheTtlSeconds { get; set; } = 30;          // DB_CACHE_TTL_SECONDS
}
