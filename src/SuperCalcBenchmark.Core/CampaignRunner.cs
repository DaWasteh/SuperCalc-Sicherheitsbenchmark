using System.Text.Json;
using System.Text.Json.Serialization;

namespace SuperCalcBenchmark.Core;

/// <summary>One model × llama-server build combination to benchmark N times.</summary>
public sealed class CampaignItem
{
    /// <summary>AutoTuner model id (stable catalogue id) or, without AutoTuner, the llama-server model id.</summary>
    public string ModelId { get; init; } = string.Empty;

    /// <summary>Display name of the model (AutoTuner name / llama-server alias).</summary>
    public string ModelName { get; init; } = string.Empty;

    /// <summary>AutoTuner runtime id (llama-server build). Null = AutoTuner default / current server.</summary>
    public string? RuntimeId { get; init; }

    public string? RuntimeLabel { get; init; }

    public int Repeats { get; init; } = 1;

    /// <summary>Manual quant label when the model name does not carry one.</summary>
    public string? QuantOverride { get; init; }

    public string Label => string.IsNullOrWhiteSpace(RuntimeLabel) && string.IsNullOrWhiteSpace(RuntimeId)
        ? (string.IsNullOrWhiteSpace(ModelName) ? ModelId : ModelName)
        : $"{(string.IsNullOrWhiteSpace(ModelName) ? ModelId : ModelName)} @ {RuntimeLabel ?? RuntimeId}";
}

public enum CampaignStopMode
{
    /// <summary>Keep going until every item finished.</summary>
    None,

    /// <summary>Finish the current Run 1/2/3 pass, archive it, then stop.</summary>
    AfterCurrentRun,

    /// <summary>Finish every repeat of the current model/backend item, then stop.</summary>
    AfterCurrentItem,

    /// <summary>Cancel the in-flight request immediately (partial output is still parsed).</summary>
    Immediately
}

public enum CampaignItemState
{
    Pending,
    Loading,
    Running,
    Completed,
    Failed,
    Skipped
}

public sealed class CampaignItemProgress
{
    public CampaignItem Item { get; init; } = new();
    public CampaignItemState State { get; set; } = CampaignItemState.Pending;
    public int CompletedRepeats { get; set; }
    public string Message { get; set; } = string.Empty;
    public ServerRuntimeInfo? Runtime { get; set; }
    public List<BenchmarkRunResult> Results { get; } = [];
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }

    public double? BestScore => Results.Count == 0 ? null : Results.Max(r => r.Run2?.Score.ScorePercent ?? r.Run1.Score.ScorePercent);
    public double? MeanScore => Results.Count == 0 ? null : Results.Average(r => r.Run2?.Score.ScorePercent ?? r.Run1.Score.ScorePercent);
}

public sealed class CampaignPlan
{
    public string CampaignId { get; init; } = string.Empty;
    public List<CampaignItem> Items { get; init; } = [];

    /// <summary>Base options (paths, timeout, truth audit, archive). Model/server/runtime are filled per item.</summary>
    public BenchmarkOptions BaseOptions { get; init; } = new();

    /// <summary>AutoTuner connection; null runs every item against <see cref="BenchmarkOptions.ServerUrl"/> without switching models.</summary>
    public AutoTunerConnection? AutoTuner { get; init; }

    public TimeSpan SwitchTimeout { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>Stop the whole campaign when an item fails to load or run (default: continue with the next item).</summary>
    public bool StopOnError { get; init; }

    /// <summary>Ask the AutoTuner to unload the last model when the campaign ends.</summary>
    public bool StopServerAtEnd { get; init; } = true;

    /// <summary>Seed for the first repeat of every item; later repeats increment it.</summary>
    public int? SeedStart { get; init; }

    public int TotalRuns => Items.Sum(item => Math.Max(1, item.Repeats));
}

public sealed class CampaignSummary
{
    [JsonPropertyName("campaignId")] public string CampaignId { get; init; } = string.Empty;
    [JsonPropertyName("startedAt")] public DateTimeOffset StartedAt { get; init; }
    [JsonPropertyName("completedAt")] public DateTimeOffset CompletedAt { get; init; }
    [JsonPropertyName("stopMode")] public string StopMode { get; init; } = "None";
    [JsonPropertyName("autoTunerUrl")] public string? AutoTunerUrl { get; init; }
    [JsonPropertyName("autoTunerVersion")] public string? AutoTunerVersion { get; init; }
    [JsonPropertyName("items")] public List<CampaignSummaryItem> Items { get; init; } = [];
}

public sealed class CampaignSummaryItem
{
    [JsonPropertyName("label")] public string Label { get; init; } = string.Empty;
    [JsonPropertyName("modelId")] public string ModelId { get; init; } = string.Empty;
    [JsonPropertyName("modelName")] public string ModelName { get; init; } = string.Empty;
    [JsonPropertyName("runtimeId")] public string? RuntimeId { get; init; }
    [JsonPropertyName("state")] public string State { get; init; } = string.Empty;
    [JsonPropertyName("message")] public string Message { get; init; } = string.Empty;
    [JsonPropertyName("repeatsPlanned")] public int RepeatsPlanned { get; init; }
    [JsonPropertyName("repeatsCompleted")] public int RepeatsCompleted { get; init; }
    [JsonPropertyName("engine")] public string? Engine { get; init; }
    [JsonPropertyName("backend")] public string? Backend { get; init; }
    [JsonPropertyName("build")] public string? Build { get; init; }
    [JsonPropertyName("bestScore")] public double? BestScore { get; init; }
    [JsonPropertyName("meanScore")] public double? MeanScore { get; init; }
    [JsonPropertyName("runDirectories")] public List<string> RunDirectories { get; init; } = [];
    [JsonPropertyName("archivedRecords")] public List<string> ArchivedRecords { get; init; } = [];
}

/// <summary>
/// Runs a list of model × llama-build items back to back. With an AutoTuner connection each
/// item is activated through the control API (the tuner applies its saved per-model settings),
/// the benchmark then talks to the returned llama-server URL directly. Stop requests are
/// honored at run and item boundaries; "immediately" cancels the in-flight request.
/// </summary>
public sealed class CampaignRunner
{
    private readonly BenchmarkRunner _runner;
    private volatile CampaignStopMode _stopMode = CampaignStopMode.None;
    private CancellationTokenSource? _immediateCancellation;

    public CampaignRunner(BenchmarkRunner? runner = null)
    {
        _runner = runner ?? new BenchmarkRunner();
    }

    public CampaignStopMode RequestedStop => _stopMode;

    /// <summary>Escalating stop request; a stronger mode always replaces a weaker one.</summary>
    public void RequestStop(CampaignStopMode mode)
    {
        if (mode > _stopMode)
        {
            _stopMode = mode;
        }

        if (mode == CampaignStopMode.Immediately)
        {
            try
            {
                _immediateCancellation?.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Campaign already finished.
            }
        }
    }

    public async Task<CampaignSummary> RunAsync(
        CampaignPlan plan,
        Action<string>? progress = null,
        Action<CampaignItemProgress>? onItemChanged = null,
        Action<BenchmarkRunArtifacts>? onRunCompleted = null,
        Action<BenchmarkRunResult>? onResultCompleted = null,
        IProgress<ChatStreamDelta>? streamProgress = null,
        CancellationToken cancellationToken = default,
        CancellationToken run1ManualAbortToken = default,
        CancellationToken run2ManualAbortToken = default,
        CancellationToken run3ManualAbortToken = default,
        Action<CampaignItemProgress, int>? onRunStarting = null,
        Func<(CancellationToken Run1, CancellationToken Run2, CancellationToken Run3)>? manualAbortTokenFactory = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.Items.Count == 0)
        {
            throw new ArgumentException("A campaign needs at least one model item.", nameof(plan));
        }

        _stopMode = CampaignStopMode.None;
        using var immediate = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _immediateCancellation = immediate;
        var token = immediate.Token;

        var campaignId = string.IsNullOrWhiteSpace(plan.CampaignId)
            ? "campaign-" + DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6]
            : plan.CampaignId;
        var startedAt = DateTimeOffset.UtcNow;
        var progressItems = plan.Items.Select(item => new CampaignItemProgress { Item = item }).ToList();
        AutoTunerClient? tuner = null;
        string? tunerVersion = null;

        try
        {
            if (plan.AutoTuner is not null)
            {
                tuner = new AutoTunerClient(plan.AutoTuner, timeout: plan.SwitchTimeout + TimeSpan.FromMinutes(5));
                try
                {
                    var health = await tuner.GetHealthAsync(token).ConfigureAwait(false);
                    tunerVersion = health.Version;
                    progress?.Invoke($"AutoTuner {health.Version} reachable at {plan.AutoTuner.BaseUrl} ({plan.AutoTuner.Source}).");
                }
                catch (Exception ex) when (ex is AutoTunerApiException or HttpRequestException or TaskCanceledException)
                {
                    throw new InvalidOperationException($"AutoTuner control API not reachable at {plan.AutoTuner.BaseUrl}: {ex.Message}", ex);
                }
            }

            progress?.Invoke($"Campaign {campaignId}: {plan.Items.Count} item(s), {plan.TotalRuns} benchmark run(s) planned.");

            for (var itemIndex = 0; itemIndex < progressItems.Count; itemIndex++)
            {
                var state = progressItems[itemIndex];
                var item = state.Item;
                if (_stopMode != CampaignStopMode.None)
                {
                    state.State = CampaignItemState.Skipped;
                    state.Message = $"Skipped: stop requested ({_stopMode}).";
                    onItemChanged?.Invoke(state);
                    continue;
                }

                token.ThrowIfCancellationRequested();
                state.State = CampaignItemState.Loading;
                state.StartedAt = DateTimeOffset.UtcNow;
                state.Message = tuner is null ? "Using the configured server." : "Asking AutoTuner to load the model...";
                onItemChanged?.Invoke(state);
                progress?.Invoke($"=== Item {itemIndex + 1}/{progressItems.Count}: {item.Label} ===");

                string serverUrl = plan.BaseOptions.ServerUrl;
                string modelId = item.ModelId;
                string? serverApiKey = plan.BaseOptions.ServerApiKey;
                ServerRuntimeInfo? presetRuntime = null;

                if (tuner is not null)
                {
                    try
                    {
                        var status = await tuner.SwitchAsync(item.ModelId, item.RuntimeId, plan.SwitchTimeout, progress: progress, cancellationToken: token).ConfigureAwait(false);
                        if (string.IsNullOrWhiteSpace(status.BackendUrl))
                        {
                            throw new AutoTunerApiException("AutoTuner did not return a backend_url for the loaded model.", 500, "invalid_backend");
                        }

                        serverUrl = status.BackendUrl.TrimEnd('/');
                        modelId = string.IsNullOrWhiteSpace(status.Alias) ? item.ModelName : status.Alias;
                        if (string.IsNullOrWhiteSpace(modelId))
                        {
                            modelId = item.ModelId;
                        }

                        serverApiKey = status.BackendApiKey ?? serverApiKey;
                        presetRuntime = status.ToRuntimeInfo(tunerVersion);
                        state.Runtime = presetRuntime;
                        progress?.Invoke($"AutoTuner ready: {modelId} on {serverUrl} ({presetRuntime.DisplayLabel}).");
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex) when (ex is AutoTunerApiException or HttpRequestException or TaskCanceledException or InvalidOperationException)
                    {
                        state.State = CampaignItemState.Failed;
                        state.CompletedAt = DateTimeOffset.UtcNow;
                        state.Message = "Load failed: " + ex.Message;
                        onItemChanged?.Invoke(state);
                        progress?.Invoke($"Item failed to load: {ex.Message}");
                        if (plan.StopOnError)
                        {
                            break;
                        }

                        continue;
                    }
                }

                state.State = CampaignItemState.Running;
                state.Message = "Benchmarking...";
                onItemChanged?.Invoke(state);

                var repeats = Math.Max(1, item.Repeats);
                var repeatGroupId = repeats > 1
                    ? $"{campaignId}-{TextUtil.SafeFileNamePart(item.ModelId)}-{TextUtil.SafeFileNamePart(item.RuntimeId ?? "default")}"
                    : string.Empty;
                var seedStart = plan.SeedStart ?? plan.BaseOptions.Seed;
                var itemFailed = false;

                for (var repeat = 1; repeat <= repeats; repeat++)
                {
                    if (_stopMode >= CampaignStopMode.AfterCurrentRun && repeat > 1 && _stopMode != CampaignStopMode.AfterCurrentItem)
                    {
                        state.Message = $"Stopped after {state.CompletedRepeats}/{repeats} repeat(s) ({_stopMode}).";
                        break;
                    }

                    if (_stopMode == CampaignStopMode.Immediately)
                    {
                        break;
                    }

                    token.ThrowIfCancellationRequested();
                    var options = plan.BaseOptions.With(
                        model: modelId,
                        serverUrl: serverUrl,
                        seed: seedStart + repeat - 1,
                        repeatGroupId: repeatGroupId,
                        repeatIndex: repeat,
                        repeatCount: repeats,
                        clearOutputDirectory: string.IsNullOrWhiteSpace(plan.BaseOptions.OutputDirectory),
                        outputDirectory: string.IsNullOrWhiteSpace(plan.BaseOptions.OutputDirectory)
                            ? null
                            : Path.Combine(plan.BaseOptions.OutputDirectory, TextUtil.SafeFileNamePart(item.Label), $"repeat-{repeat:D3}"),
                        presetRuntime: presetRuntime,
                        campaignId: campaignId,
                        campaignItemLabel: item.Label,
                        serverApiKey: serverApiKey,
                        quantOverride: item.QuantOverride);

                    progress?.Invoke(repeats > 1 ? $"--- {item.Label}: repeat {repeat}/{repeats} (seed {options.Seed}) ---" : $"--- {item.Label} (seed {options.Seed}) ---");
                    onRunStarting?.Invoke(state, repeat);
                    var abortTokens = manualAbortTokenFactory?.Invoke() ?? (run1ManualAbortToken, run2ManualAbortToken, run3ManualAbortToken);
                    try
                    {
                        var result = await _runner.RunAsync(
                            options,
                            progress,
                            token,
                            onRunCompleted,
                            streamProgress,
                            abortTokens.Run1,
                            abortTokens.Run2,
                            abortTokens.Run3).ConfigureAwait(false);
                        state.Results.Add(result);
                        state.CompletedRepeats = repeat;
                        state.Runtime ??= result.Runtime;
                        state.Message = $"{state.CompletedRepeats}/{repeats} repeat(s) done, best {state.BestScore:0.##}";
                        onItemChanged?.Invoke(state);
                        onResultCompleted?.Invoke(result);
                    }
                    catch (OperationCanceledException) when (_stopMode == CampaignStopMode.Immediately && !cancellationToken.IsCancellationRequested)
                    {
                        state.Message = "Cancelled immediately by user.";
                        itemFailed = true;
                        break;
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        progress?.Invoke($"Run failed: {ex.Message}");
                        state.Message = "Run failed: " + ex.Message;
                        itemFailed = true;
                        if (plan.StopOnError)
                        {
                            break;
                        }
                    }

                    if (_stopMode == CampaignStopMode.AfterCurrentRun)
                    {
                        state.Message = $"Stopped after run ({state.CompletedRepeats}/{repeats} repeat(s)).";
                        break;
                    }
                }

                state.CompletedAt = DateTimeOffset.UtcNow;
                state.State = itemFailed && state.Results.Count == 0
                    ? CampaignItemState.Failed
                    : state.Results.Count > 0 ? CampaignItemState.Completed : CampaignItemState.Skipped;
                onItemChanged?.Invoke(state);

                if (itemFailed && plan.StopOnError)
                {
                    progress?.Invoke("Stopping campaign because an item failed (StopOnError).");
                    break;
                }

                if (_stopMode is CampaignStopMode.AfterCurrentRun or CampaignStopMode.AfterCurrentItem or CampaignStopMode.Immediately)
                {
                    progress?.Invoke($"Campaign stop honored after item {itemIndex + 1} ({_stopMode}).");
                    for (var rest = itemIndex + 1; rest < progressItems.Count; rest++)
                    {
                        progressItems[rest].State = CampaignItemState.Skipped;
                        progressItems[rest].Message = $"Skipped: stop requested ({_stopMode}).";
                        onItemChanged?.Invoke(progressItems[rest]);
                    }

                    break;
                }
            }
        }
        finally
        {
            if (tuner is not null && plan.StopServerAtEnd)
            {
                try
                {
                    progress?.Invoke("Asking AutoTuner to unload the last model...");
                    await tuner.StopAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is AutoTunerApiException or HttpRequestException or TaskCanceledException)
                {
                    progress?.Invoke($"AutoTuner stop failed (ignored): {ex.Message}");
                }
            }

            tuner?.Dispose();
            _immediateCancellation = null;
        }

        var summary = new CampaignSummary
        {
            CampaignId = campaignId,
            StartedAt = startedAt,
            CompletedAt = DateTimeOffset.UtcNow,
            StopMode = _stopMode.ToString(),
            AutoTunerUrl = plan.AutoTuner?.BaseUrl,
            AutoTunerVersion = tunerVersion,
            Items = progressItems.Select(state => new CampaignSummaryItem
            {
                Label = state.Item.Label,
                ModelId = state.Item.ModelId,
                ModelName = state.Item.ModelName,
                RuntimeId = state.Item.RuntimeId,
                State = state.State.ToString(),
                Message = state.Message,
                RepeatsPlanned = Math.Max(1, state.Item.Repeats),
                RepeatsCompleted = state.CompletedRepeats,
                Engine = state.Runtime?.Engine,
                Backend = state.Runtime?.Backend,
                Build = state.Runtime?.EngineVersion,
                BestScore = state.BestScore,
                MeanScore = state.MeanScore,
                RunDirectories = state.Results.Select(r => r.OutputDirectory).ToList(),
                ArchivedRecords = state.Results.Where(r => !string.IsNullOrWhiteSpace(r.ArchivedRecordPath)).Select(r => r.ArchivedRecordPath!).ToList()
            }).ToList()
        };

        TryWriteSummary(summary, progress);
        progress?.Invoke($"Campaign {campaignId} finished: {summary.Items.Count(i => i.State == "Completed")} completed, {summary.Items.Count(i => i.State == "Failed")} failed, {summary.Items.Count(i => i.State == "Skipped")} skipped.");
        return summary;
    }

    private static void TryWriteSummary(CampaignSummary summary, Action<string>? progress)
    {
        try
        {
            var directory = Path.Combine(BenchmarkPathResolver.ResolveDataRoot(), "Campaigns");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, TextUtil.SafeFileNamePart(summary.CampaignId) + ".json");
            File.WriteAllText(path, JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }), System.Text.Encoding.UTF8);
            progress?.Invoke("Campaign summary: " + path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            progress?.Invoke("Campaign summary could not be written: " + ex.Message);
        }
    }
}
