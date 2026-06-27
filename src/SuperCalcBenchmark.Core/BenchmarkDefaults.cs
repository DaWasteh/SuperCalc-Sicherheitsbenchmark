namespace SuperCalcBenchmark.Core;

public static class BenchmarkDefaults
{
    // Official runs must be patient enough for local large reasoning models.
    // At 3 tokens/sec, ~80k visible thinking chars + ~30k final-output chars
    // is roughly 27.5k generated tokens before prompt-reading and safety margin.
    public const int SlowModelMinimumTokensPerSecond = 3;
    public const int SlowModelReasoningBudgetCharacters = 80_000;
    public const int SlowModelOutputBudgetCharacters = 30_000;
    public const int EstimatedCharactersPerGeneratedToken = 4;
    public const int PromptReadSafetySeconds = 1_200;
    public const int OfficialRequestTimeoutSeconds = 14_400;
    public const int ModelListTimeoutSeconds = 30;

    public static TimeSpan OfficialRequestTimeout => TimeSpan.FromSeconds(OfficialRequestTimeoutSeconds);
}
