namespace ValutaBot.MiniApp;

public class TradingBotSettings
{
    public double MlWeight { get; set; } = 0.6;
    public double MathWeight { get; set; } = 0.4;
    public int FastFailTimeoutSeconds { get; set; } = 6;
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
    public int CircuitBreakerCooldownMinutes { get; set; } = 15;            // COOLDOWN_MINUTES (15 min, dataset mode)
    public int CircuitBreakerDbCacheTtlSeconds { get; set; } = 30;          // DB_CACHE_TTL_SECONDS

    public int DatasetReadinessThreshold { get; set; } = 1_000;            // Minimum outcomes before CB activates

    /// <summary>
    /// When true, the AutoTradingScanner completely bypasses the Circuit Breaker
    /// so dataset accumulation never stops due to consecutive losses.
    /// Defaults to false so Circuit Breaker actively protects trading.
    /// </summary>
    public bool DatasetCollectionMode { get; set; } = false;
}
