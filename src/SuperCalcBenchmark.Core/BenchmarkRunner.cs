using System.Text.Json;

namespace SuperCalcBenchmark.Core;

public sealed class BenchmarkRunner
{
    private const string ToolVersion = "0.6.8";

    private readonly GroundTruthStore _groundTruthStore;
    private readonly PromptBuilder _promptBuilder;
    private readonly ResponseParser _responseParser;
    private readonly ScoringEngine _scoringEngine;
    private readonly TruthAuditParser _truthAuditParser;
    private readonly TruthAuditScoringEngine _truthAuditScoringEngine;
    private readonly ReportWriter _reportWriter;

    public BenchmarkRunner(
        GroundTruthStore? groundTruthStore = null,
        PromptBuilder? promptBuilder = null,
        ResponseParser? responseParser = null,
        ScoringEngine? scoringEngine = null,
        TruthAuditParser? truthAuditParser = null,
        TruthAuditScoringEngine? truthAuditScoringEngine = null,
        ReportWriter? reportWriter = null)
    {
        _groundTruthStore = groundTruthStore ?? new GroundTruthStore();
        _promptBuilder = promptBuilder ?? new PromptBuilder();
        _responseParser = responseParser ?? new ResponseParser();
        _scoringEngine = scoringEngine ?? new ScoringEngine();
        _truthAuditParser = truthAuditParser ?? new TruthAuditParser();
        _truthAuditScoringEngine = truthAuditScoringEngine ?? new TruthAuditScoringEngine();
        _reportWriter = reportWriter ?? new ReportWriter();
    }

    public async Task<BenchmarkRunResult> RunAsync(
        BenchmarkOptions options,
        Action<string>? progress = null,
        CancellationToken cancellationToken = default,
        Action<BenchmarkRunArtifacts>? onRunCompleted = null,
        IProgress<ChatStreamDelta>? streamProgress = null,
        CancellationToken run1ManualAbortToken = default,
        CancellationToken run2ManualAbortToken = default,
        CancellationToken run3ManualAbortToken = default)
    {
        if (string.IsNullOrWhiteSpace(options.Model))
        {
            throw new ArgumentException("BenchmarkOptions.Model is required for a full LLM run.");
        }

        progress?.Invoke("Loading source and hidden ground truth...");
        var startedAt = DateTimeOffset.UtcNow;
        var source = SourceDocument.Load(options.SourcePath);
        var groundTruth = _groundTruthStore.Load(options.GroundTruthPath);
        var groundTruthSha256 = GroundTruthStore.ComputeSha256(options.GroundTruthPath);
        var scoringProfile = ScoringProfiles.Get(options.ScoringProfile);
        ValidatePreflight(options, source, groundTruth);

        using var client = new LlamaCppClient(options.Timeout);
        progress?.Invoke("Reading server context window...");
        var serverContextSize = await client.GetServerContextSizeAsync(options.ServerUrl, cancellationToken).ConfigureAwait(false);

        // Ask llama-server for the authoritative file type (PR #25134, b9860+) so the archive
        // no longer has to guess the quant from the model file name. Best-effort: if the
        // server is unreachable or omits meta.ftype, ModelIdentity falls back to name detection.
        string? serverFtype = null;
        try
        {
            serverFtype = await client.GetModelFtypeAsync(options.ServerUrl, options.Model, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(serverFtype))
            {
                progress?.Invoke($"Server reports model file type: {serverFtype}");
            }
        }
        catch
        {
            // Quant detection must never block a run; name-based fallback covers this.
        }

        var result = new BenchmarkRunResult
        {
            ToolVersion = ToolVersion,
            BenchmarkId = groundTruth.BenchmarkId,
            BenchmarkProfile = options.BenchmarkProfile,
            StartedAt = startedAt,
            ServerUrl = options.ServerUrl,
            Model = options.Model,
            MaxTokens = options.MaxTokens,
            TimeoutSeconds = (int)Math.Ceiling(options.Timeout.TotalSeconds),
            Seed = options.Seed,
            RepeatGroupId = options.RepeatGroupId,
            RepeatIndex = Math.Max(1, options.RepeatIndex),
            RepeatCount = Math.Max(1, options.RepeatCount),
            SkipResponseFormat = options.SkipResponseFormat,
            DisableThinking = options.DisableThinking,
            AbortOnLoop = options.AbortOnLoop,
            ServerContextSize = serverContextSize,
            DetectedQuant = serverFtype,
            SourceFile = options.SourcePath,
            SourceSha256 = source.Sha256,
            ExpectedSourceSha256 = groundTruth.SourceSha256,
            SourceHashMatches = string.Equals(source.Sha256, groundTruth.SourceSha256, StringComparison.OrdinalIgnoreCase),
            OutputDirectory = _reportWriter.CreateRunDirectory(options, startedAt)
        };

        progress?.Invoke("Building Run 1 prompt...");
        var run1Prompt = _promptBuilder.BuildAnalysisPrompt(source, options.AnalysisPromptPath, options.SchemaPath);
        var run1StartedAt = DateTimeOffset.UtcNow;
        progress?.Invoke("Sending Run 1 blind analysis to llama-server...");
        var run1Completion = await client.CreateChatCompletionAsync(
            options.ServerUrl,
            options.Model,
            BuildSystemPrompt("Run 1 blind security analysis"),
            run1Prompt,
            options,
            streamProgress,
            cancellationToken,
            run1ManualAbortToken).ConfigureAwait(false);

        if (run1Completion.ManuallyStopped)
        {
            progress?.Invoke("Run 1 manually stopped by user; parsing/scoring partial output and visible thinking...");
        }

        if (run1Completion.LoopDetected)
        {
            progress?.Invoke($"Run 1 stopped early by loop guard: {run1Completion.LoopDiagnosticsSummary}");
        }

        progress?.Invoke("Parsing and scoring Run 1...");
        var run1Content = SplitThinkingContent(run1Completion.AssistantContent, run1Completion.ReasoningContent);
        var run1Tokens = await CountCompletionTokensAsync(client, options.ServerUrl, options.Model, run1Content, run1Completion, cancellationToken).ConfigureAwait(false);
        var run1Parse = _responseParser.Parse(run1Content.OutputContent);
        var run1Score = _scoringEngine.Score(
            "Run 1",
            run1Parse.Findings,
            groundTruth,
            source,
            profile: scoringProfile,
            context: new ScoreComputationContext
            {
                GroundTruthSha256 = groundTruthSha256,
                SourceSha256 = source.Sha256,
                PromptVersion = PromptVersions.AnalysisV1
            });
        run1Score = ApplyAdjudicationIfConfigured(run1Score, options);
        var run1ReasoningDisclosure = BuildReasoningDisclosure("Run 1", run1Content.ReasoningContent, run1Score, groundTruth, source, scoringProfile, groundTruthSha256);
        var run1CompletedAt = DateTimeOffset.UtcNow;
        result.Run1 = new BenchmarkRunArtifacts
        {
            RunName = "Run 1",
            PromptVersion = PromptVersions.AnalysisV1,
            RunKind = "blind_analysis",
            StartedAt = run1StartedAt,
            CompletedAt = run1CompletedAt,
            Prompt = run1Prompt,
            Response = run1Content.OutputContent,
            ReasoningContent = run1Content.ReasoningContent,
            RawResponse = run1Completion.RawResponse,
            RequestJson = run1Completion.RequestJson,
            FinishReason = run1Completion.FinishReason,
            PromptTokens = run1Completion.PromptTokens,
            ResponseTokens = run1Tokens.Output,
            ReasoningTokens = run1Tokens.Reasoning,
            CompletionTokens = run1Tokens.Total,
            LoopDetected = run1Completion.LoopDetected,
            LoopDiagnosticsSummary = run1Completion.LoopDiagnosticsSummary,
            ManuallyStopped = run1Completion.ManuallyStopped,
            UsedResponseFormat = run1Completion.UsedResponseFormat,
            RetriedWithoutResponseFormat = run1Completion.RetriedWithoutResponseFormat,
            UsedThinkingControl = run1Completion.UsedThinkingControl,
            RetriedWithoutThinkingControl = run1Completion.RetriedWithoutThinkingControl,
            Parse = run1Parse,
            Score = run1Score,
            ReasoningDisclosure = run1ReasoningDisclosure
        };

        // Surface Run 1 to the caller immediately so the UI can render its score,
        // matrix and raw output while Run 2 is still in flight.
        onRunCompleted?.Invoke(result.Run1);

        progress?.Invoke("Building Run 2 self-validation prompt...");
        var run2Prompt = _promptBuilder.BuildSelfValidationPrompt(source, options.SelfValidatePromptPath, options.SchemaPath, run1Content.OutputContent);
        var run2StartedAt = DateTimeOffset.UtcNow;
        progress?.Invoke("Sending Run 2 self-validation to llama-server...");
        var run2Completion = await client.CreateChatCompletionAsync(
            options.ServerUrl,
            options.Model,
            BuildSystemPrompt("Run 2 self-validation"),
            run2Prompt,
            options,
            streamProgress,
            cancellationToken,
            run2ManualAbortToken).ConfigureAwait(false);

        if (run2Completion.ManuallyStopped)
        {
            progress?.Invoke("Run 2 manually stopped by user; parsing/scoring partial output and visible thinking...");
        }

        if (run2Completion.LoopDetected)
        {
            progress?.Invoke($"Run 2 stopped early by loop guard: {run2Completion.LoopDiagnosticsSummary}");
        }

        progress?.Invoke("Parsing and scoring Run 2...");
        var run2Content = SplitThinkingContent(run2Completion.AssistantContent, run2Completion.ReasoningContent);
        var run2Tokens = await CountCompletionTokensAsync(client, options.ServerUrl, options.Model, run2Content, run2Completion, cancellationToken).ConfigureAwait(false);
        var run2Parse = _responseParser.Parse(run2Content.OutputContent);
        var run2Score = _scoringEngine.Score(
            "Run 2",
            run2Parse.Findings,
            groundTruth,
            source,
            profile: scoringProfile,
            context: new ScoreComputationContext
            {
                GroundTruthSha256 = groundTruthSha256,
                SourceSha256 = source.Sha256,
                PromptVersion = PromptVersions.SelfValidateV1
            });
        run2Score = ApplyAdjudicationIfConfigured(run2Score, options);
        var run2ReasoningDisclosure = BuildReasoningDisclosure("Run 2", run2Content.ReasoningContent, run2Score, groundTruth, source, scoringProfile, groundTruthSha256);
        var run2CompletedAt = DateTimeOffset.UtcNow;
        result.Run2 = new BenchmarkRunArtifacts
        {
            RunName = "Run 2",
            PromptVersion = PromptVersions.SelfValidateV1,
            RunKind = "self_validation",
            StartedAt = run2StartedAt,
            CompletedAt = run2CompletedAt,
            Prompt = run2Prompt,
            Response = run2Content.OutputContent,
            ReasoningContent = run2Content.ReasoningContent,
            RawResponse = run2Completion.RawResponse,
            RequestJson = run2Completion.RequestJson,
            FinishReason = run2Completion.FinishReason,
            PromptTokens = run2Completion.PromptTokens,
            ResponseTokens = run2Tokens.Output,
            ReasoningTokens = run2Tokens.Reasoning,
            CompletionTokens = run2Tokens.Total,
            LoopDetected = run2Completion.LoopDetected,
            LoopDiagnosticsSummary = run2Completion.LoopDiagnosticsSummary,
            ManuallyStopped = run2Completion.ManuallyStopped,
            UsedResponseFormat = run2Completion.UsedResponseFormat,
            RetriedWithoutResponseFormat = run2Completion.RetriedWithoutResponseFormat,
            UsedThinkingControl = run2Completion.UsedThinkingControl,
            RetriedWithoutThinkingControl = run2Completion.RetriedWithoutThinkingControl,
            Parse = run2Parse,
            Score = run2Score,
            ReasoningDisclosure = run2ReasoningDisclosure
        };

        onRunCompleted?.Invoke(result.Run2);

        result.Comparison = _scoringEngine.Compare(run1Score, run2Score);

        if (options.WithTruthAudit)
        {
            var auditTarget = SelectTruthAuditTarget(result, options.TruthAuditSource);
            progress?.Invoke($"Building Run 3 truth-audit prompt for {auditTarget.Artifacts.RunName}...");
            var run3Prompt = _promptBuilder.BuildTruthAuditPrompt(
                groundTruth,
                options.TruthAuditPromptPath,
                options.TruthAuditSchemaPath,
                auditTarget.Artifacts.RunName,
                auditTarget.Artifacts.Response,
                source);
            var run3StartedAt = DateTimeOffset.UtcNow;
            progress?.Invoke("Sending Run 3 truth audit to llama-server...");
            var run3Completion = await client.CreateChatCompletionAsync(
                options.ServerUrl,
                options.Model,
                BuildSystemPrompt("Run 3 truth audit / non-blind accountability evaluation"),
                run3Prompt,
                options,
                streamProgress,
                cancellationToken,
                run3ManualAbortToken).ConfigureAwait(false);

            if (run3Completion.ManuallyStopped)
            {
                progress?.Invoke("Run 3 manually stopped by user; parsing/scoring partial output and visible thinking...");
            }

            progress?.Invoke("Parsing and scoring Run 3 truth audit...");
            var run3Content = SplitThinkingContent(run3Completion.AssistantContent, run3Completion.ReasoningContent);
            var run3Tokens = await CountCompletionTokensAsync(client, options.ServerUrl, options.Model, run3Content, run3Completion, cancellationToken).ConfigureAwait(false);
            var truthAuditResponse = _truthAuditParser.Parse(run3Content.OutputContent);
            var truthAudit = _truthAuditScoringEngine.Score(
                truthAuditResponse,
                auditTarget.Artifacts.Score,
                auditTarget.Artifacts.Response,
                auditTarget.Artifacts.RunName,
                auditTarget.SelectionReason);
            var run3CompletedAt = DateTimeOffset.UtcNow;
            result.Run3 = new BenchmarkRunArtifacts
            {
                RunName = "Run 3",
                PromptVersion = PromptVersions.TruthAuditV1,
                RunKind = "truth_audit",
                GroundTruthVisibleToModel = true,
                StartedAt = run3StartedAt,
                CompletedAt = run3CompletedAt,
                Prompt = run3Prompt,
                Response = run3Content.OutputContent,
                ReasoningContent = run3Content.ReasoningContent,
                RawResponse = run3Completion.RawResponse,
                RequestJson = run3Completion.RequestJson,
                FinishReason = run3Completion.FinishReason,
                PromptTokens = run3Completion.PromptTokens,
                ResponseTokens = run3Tokens.Output,
                ReasoningTokens = run3Tokens.Reasoning,
                CompletionTokens = run3Tokens.Total,
                LoopDetected = run3Completion.LoopDetected,
                LoopDiagnosticsSummary = run3Completion.LoopDiagnosticsSummary,
                ManuallyStopped = run3Completion.ManuallyStopped,
                UsedResponseFormat = run3Completion.UsedResponseFormat,
                RetriedWithoutResponseFormat = run3Completion.RetriedWithoutResponseFormat,
                UsedThinkingControl = run3Completion.UsedThinkingControl,
                RetriedWithoutThinkingControl = run3Completion.RetriedWithoutThinkingControl,
                Parse = new ParseResult { AssistantContent = run3Content.OutputContent, ParsedJson = truthAuditResponse.TruthItems.Count > 0, ParseMode = truthAuditResponse.TruthItems.Count > 0 ? "truth_audit_json" : "truth_audit_unparsed" },
                Score = new ScoringResult
                {
                    RunName = "Run 3",
                    ScoringProfile = auditTarget.Artifacts.Score.ScoringProfile,
                    ScoringProfileVersion = auditTarget.Artifacts.Score.ScoringProfileVersion,
                    ScoringEngineVersion = auditTarget.Artifacts.Score.ScoringEngineVersion,
                    ParserVersion = ResponseParser.CurrentParserVersion,
                    GroundTruthSha256 = groundTruthSha256,
                    SourceSha256 = source.Sha256,
                    PromptVersion = PromptVersions.TruthAuditV1,
                    ScorePercent = truthAudit.AccountabilityScore,
                    RawPoints = truthAudit.AccountabilityScore
                },
                TruthAudit = truthAudit
            };

            onRunCompleted?.Invoke(result.Run3);
        }

        result.CompletedAt = DateTimeOffset.UtcNow;

        progress?.Invoke("Writing run artifacts and report...");
        _reportWriter.Write(result);
        TryArchive(result, options, progress);
        progress?.Invoke($"Done. Report: {Path.Combine(result.OutputDirectory, "report.md")}");
        return result;
    }

    public async Task<BenchmarkRunResult> AddTruthAuditAsync(
        BenchmarkRunResult result,
        BenchmarkOptions options,
        Action<string>? progress = null,
        CancellationToken cancellationToken = default,
        Action<BenchmarkRunArtifacts>? onRunCompleted = null,
        IProgress<ChatStreamDelta>? streamProgress = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (string.IsNullOrWhiteSpace(options.Model))
        {
            throw new ArgumentException("BenchmarkOptions.Model is required for a truth-audit LLM run.");
        }

        var source = SourceDocument.Load(options.SourcePath);
        var groundTruth = _groundTruthStore.Load(options.GroundTruthPath);
        var groundTruthSha256 = GroundTruthStore.ComputeSha256(options.GroundTruthPath);
        ValidatePreflight(options, source, groundTruth);

        using var client = new LlamaCppClient(options.Timeout);
        var auditTarget = SelectTruthAuditTarget(result, options.TruthAuditSource);
        progress?.Invoke($"Building Run 3 truth-audit prompt for {auditTarget.Artifacts.RunName}...");
        var run3Prompt = _promptBuilder.BuildTruthAuditPrompt(
            groundTruth,
            options.TruthAuditPromptPath,
            options.TruthAuditSchemaPath,
            auditTarget.Artifacts.RunName,
            auditTarget.Artifacts.Response,
            source);
        var run3StartedAt = DateTimeOffset.UtcNow;
        progress?.Invoke("Sending Run 3 truth audit to llama-server...");
        var run3Completion = await client.CreateChatCompletionAsync(
            options.ServerUrl,
            options.Model,
            BuildSystemPrompt("Run 3 truth audit / non-blind accountability evaluation"),
            run3Prompt,
            options,
            streamProgress,
            cancellationToken).ConfigureAwait(false);

        progress?.Invoke("Parsing and scoring Run 3 truth audit...");
        var run3Content = SplitThinkingContent(run3Completion.AssistantContent, run3Completion.ReasoningContent);
        var run3Tokens = await CountCompletionTokensAsync(client, options.ServerUrl, options.Model, run3Content, run3Completion, cancellationToken).ConfigureAwait(false);
        var truthAuditResponse = _truthAuditParser.Parse(run3Content.OutputContent);
        var truthAudit = _truthAuditScoringEngine.Score(
            truthAuditResponse,
            auditTarget.Artifacts.Score,
            auditTarget.Artifacts.Response,
            auditTarget.Artifacts.RunName,
            auditTarget.SelectionReason);
        var run3CompletedAt = DateTimeOffset.UtcNow;
        result.Run3 = new BenchmarkRunArtifacts
        {
            RunName = "Run 3",
            PromptVersion = PromptVersions.TruthAuditV1,
            RunKind = "truth_audit",
            GroundTruthVisibleToModel = true,
            StartedAt = run3StartedAt,
            CompletedAt = run3CompletedAt,
            Prompt = run3Prompt,
            Response = run3Content.OutputContent,
            ReasoningContent = run3Content.ReasoningContent,
            RawResponse = run3Completion.RawResponse,
            RequestJson = run3Completion.RequestJson,
            FinishReason = run3Completion.FinishReason,
            PromptTokens = run3Completion.PromptTokens,
            ResponseTokens = run3Tokens.Output,
            ReasoningTokens = run3Tokens.Reasoning,
            CompletionTokens = run3Tokens.Total,
            LoopDetected = run3Completion.LoopDetected,
            LoopDiagnosticsSummary = run3Completion.LoopDiagnosticsSummary,
            ManuallyStopped = run3Completion.ManuallyStopped,
            UsedResponseFormat = run3Completion.UsedResponseFormat,
            RetriedWithoutResponseFormat = run3Completion.RetriedWithoutResponseFormat,
            UsedThinkingControl = run3Completion.UsedThinkingControl,
            RetriedWithoutThinkingControl = run3Completion.RetriedWithoutThinkingControl,
            Parse = new ParseResult { AssistantContent = run3Content.OutputContent, ParsedJson = truthAuditResponse.TruthItems.Count > 0, ParseMode = truthAuditResponse.TruthItems.Count > 0 ? "truth_audit_json" : "truth_audit_unparsed" },
            Score = new ScoringResult
            {
                RunName = "Run 3",
                ScoringProfile = auditTarget.Artifacts.Score.ScoringProfile,
                ScoringProfileVersion = auditTarget.Artifacts.Score.ScoringProfileVersion,
                ScoringEngineVersion = auditTarget.Artifacts.Score.ScoringEngineVersion,
                ParserVersion = ResponseParser.CurrentParserVersion,
                GroundTruthSha256 = groundTruthSha256,
                SourceSha256 = source.Sha256,
                PromptVersion = PromptVersions.TruthAuditV1,
                ScorePercent = truthAudit.AccountabilityScore,
                RawPoints = truthAudit.AccountabilityScore
            },
            TruthAudit = truthAudit
        };

        result.CompletedAt = DateTimeOffset.UtcNow;
        onRunCompleted?.Invoke(result.Run3);
        progress?.Invoke("Writing updated truth-audit artifacts and report...");
        _reportWriter.Write(result);
        TryArchive(result, options, progress);
        return result;
    }

    public BenchmarkRunResult ScoreFixture(
        BenchmarkOptions options,
        string responsePath,
        string runName = "Fixture")
    {
        var startedAt = DateTimeOffset.UtcNow;
        var source = SourceDocument.Load(options.SourcePath);
        var groundTruth = _groundTruthStore.Load(options.GroundTruthPath);
        var groundTruthSha256 = GroundTruthStore.ComputeSha256(options.GroundTruthPath);
        var scoringProfile = ScoringProfiles.Get(options.ScoringProfile);
        ValidatePreflight(options, source, groundTruth);

        var response = File.ReadAllText(responsePath, System.Text.Encoding.UTF8);
        var content = SplitThinkingContent(response, reasoningContent: string.Empty);
        var parse = _responseParser.Parse(content.OutputContent);
        var score = _scoringEngine.Score(
            runName,
            parse.Findings,
            groundTruth,
            source,
            profile: scoringProfile,
            context: new ScoreComputationContext
            {
                GroundTruthSha256 = groundTruthSha256,
                SourceSha256 = source.Sha256,
                PromptVersion = PromptVersions.Fixture
            });
        score = ApplyAdjudicationIfConfigured(score, options);
        var reasoningDisclosure = BuildReasoningDisclosure(runName, content.ReasoningContent, score, groundTruth, source, scoringProfile, groundTruthSha256);
        var outputDirectory = _reportWriter.CreateRunDirectory(WithModelFallback(options, runName), startedAt);
        var completedAt = DateTimeOffset.UtcNow;

        var result = new BenchmarkRunResult
        {
            ToolVersion = ToolVersion,
            BenchmarkId = groundTruth.BenchmarkId,
            BenchmarkProfile = "fixture",
            StartedAt = startedAt,
            CompletedAt = completedAt,
            ServerUrl = "fixture",
            Model = string.IsNullOrWhiteSpace(options.Model) ? runName : options.Model,
            MaxTokens = options.MaxTokens,
            TimeoutSeconds = (int)Math.Ceiling(options.Timeout.TotalSeconds),
            Seed = options.Seed,
            RepeatGroupId = options.RepeatGroupId,
            RepeatIndex = Math.Max(1, options.RepeatIndex),
            RepeatCount = Math.Max(1, options.RepeatCount),
            SkipResponseFormat = options.SkipResponseFormat,
            DisableThinking = options.DisableThinking,
            AbortOnLoop = options.AbortOnLoop,
            SourceFile = options.SourcePath,
            SourceSha256 = source.Sha256,
            ExpectedSourceSha256 = groundTruth.SourceSha256,
            SourceHashMatches = string.Equals(source.Sha256, groundTruth.SourceSha256, StringComparison.OrdinalIgnoreCase),
            OutputDirectory = outputDirectory,
            Run1 = new BenchmarkRunArtifacts
            {
                RunName = runName,
                PromptVersion = PromptVersions.Fixture,
                RunKind = "fixture",
                StartedAt = startedAt,
                CompletedAt = completedAt,
                Prompt = string.Empty,
                Response = content.OutputContent,
                ReasoningContent = content.ReasoningContent,
                RawResponse = response,
                RequestJson = string.Empty,
                UsedResponseFormat = false,
                Parse = parse,
                Score = score,
                ReasoningDisclosure = reasoningDisclosure
            }
        };

        _reportWriter.Write(result);
        TryArchive(result, options, progress: null);
        return result;

        static BenchmarkOptions WithModelFallback(BenchmarkOptions original, string fallbackModel)
        {
            return new BenchmarkOptions
            {
                ServerUrl = original.ServerUrl,
                Model = string.IsNullOrWhiteSpace(original.Model) ? fallbackModel : original.Model,
                SourcePath = original.SourcePath,
                GroundTruthPath = original.GroundTruthPath,
                AnalysisPromptPath = original.AnalysisPromptPath,
                SelfValidatePromptPath = original.SelfValidatePromptPath,
                TruthAuditPromptPath = original.TruthAuditPromptPath,
                SchemaPath = original.SchemaPath,
                TruthAuditSchemaPath = original.TruthAuditSchemaPath,
                OutputDirectory = original.OutputDirectory,
                Temperature = original.Temperature,
                TopP = original.TopP,
                MaxTokens = original.MaxTokens,
                Seed = original.Seed,
                Repeats = original.Repeats,
                SeedStart = original.SeedStart,
                RepeatGroupId = original.RepeatGroupId,
                RepeatIndex = original.RepeatIndex,
                RepeatCount = original.RepeatCount,
                TruthAuditRepeatMode = original.TruthAuditRepeatMode,
                Timeout = original.Timeout,
                AllowHashMismatch = original.AllowHashMismatch,
                SkipResponseFormat = original.SkipResponseFormat,
                DisableThinking = original.DisableThinking,
                BenchmarkProfile = original.BenchmarkProfile,
                ScoringProfile = original.ScoringProfile,
                WithTruthAudit = original.WithTruthAudit,
                TruthAuditSource = original.TruthAuditSource,
                AbortOnLoop = original.AbortOnLoop,
                ArchiveDirectory = original.ArchiveDirectory,
                QuantOverride = original.QuantOverride,
                AdjudicationPath = original.AdjudicationPath
            };
        }
    }

    private static ScoringResult ApplyAdjudicationIfConfigured(ScoringResult score, BenchmarkOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.AdjudicationPath))
        {
            return score;
        }

        return AdjudicationApplier.ApplyFromFile(score, options.AdjudicationPath);
    }

    private static async Task<(int? Output, int? Reasoning, int? Total)> CountCompletionTokensAsync(
        LlamaCppClient client,
        string serverUrl,
        string model,
        (string OutputContent, string ReasoningContent) content,
        ChatCompletionResult completion,
        CancellationToken cancellationToken)
    {
        try
        {
            var outputTask = client.CountTokensAsync(serverUrl, model, content.OutputContent, cancellationToken);
            var reasoningTask = client.CountTokensAsync(serverUrl, model, content.ReasoningContent, cancellationToken);
            await Task.WhenAll(outputTask, reasoningTask).ConfigureAwait(false);

            var output = await outputTask.ConfigureAwait(false);
            var reasoning = await reasoningTask.ConfigureAwait(false);
            var visibleTotal = output.HasValue && reasoning.HasValue ? output.Value + reasoning.Value : (int?)null;
            return (output, reasoning, completion.CompletionTokens ?? visibleTotal);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException)
        {
            // Token metrics are diagnostics and must never invalidate an otherwise valid benchmark run.
            return (null, null, completion.CompletionTokens);
        }
    }

    private static (string OutputContent, string ReasoningContent) SplitThinkingContent(string assistantContent, string reasoningContent)
    {
        var (outputContent, inlineReasoning) = ExtractInlineThinkBlocks(assistantContent);
        var combinedReasoning = CombineReasoning(reasoningContent, inlineReasoning);
        return (outputContent, combinedReasoning);
    }

    private static (string OutputContent, string InlineReasoning) ExtractInlineThinkBlocks(string assistantContent)
    {
        assistantContent ??= string.Empty;
        var output = new System.Text.StringBuilder();
        var reasoning = new System.Text.StringBuilder();
        var cursor = 0;
        var extractedAnyBlock = false;

        while (cursor < assistantContent.Length)
        {
            var start = assistantContent.IndexOf("<think", cursor, StringComparison.OrdinalIgnoreCase);
            if (start < 0)
            {
                output.Append(assistantContent, cursor, assistantContent.Length - cursor);
                break;
            }

            var tagEnd = assistantContent.IndexOf('>', start);
            if (tagEnd < 0)
            {
                output.Append(assistantContent, cursor, assistantContent.Length - cursor);
                break;
            }

            var end = assistantContent.IndexOf("</think>", tagEnd + 1, StringComparison.OrdinalIgnoreCase);
            if (end < 0)
            {
                output.Append(assistantContent, cursor, assistantContent.Length - cursor);
                break;
            }

            extractedAnyBlock = true;
            output.Append(assistantContent, cursor, start - cursor);
            var block = assistantContent[(tagEnd + 1)..end].Trim();
            if (!string.IsNullOrWhiteSpace(block))
            {
                if (reasoning.Length > 0)
                {
                    reasoning.AppendLine().AppendLine();
                }

                reasoning.Append(block);
            }

            cursor = end + "</think>".Length;
        }

        return extractedAnyBlock
            ? (output.ToString().Trim(), reasoning.ToString().Trim())
            : (assistantContent, string.Empty);
    }

    private static string CombineReasoning(string reasoningContent, string inlineReasoning)
    {
        reasoningContent ??= string.Empty;
        inlineReasoning ??= string.Empty;
        if (string.IsNullOrWhiteSpace(reasoningContent))
        {
            return inlineReasoning.Trim();
        }

        if (string.IsNullOrWhiteSpace(inlineReasoning))
        {
            return reasoningContent;
        }

        return reasoningContent.TrimEnd() + Environment.NewLine + Environment.NewLine + inlineReasoning.Trim();
    }

    private ReasoningDisclosureDiagnostics BuildReasoningDisclosure(
        string runName,
        string reasoningContent,
        ScoringResult outputScore,
        GroundTruthDocument groundTruth,
        SourceDocument source,
        ScoringProfile scoringProfile,
        string groundTruthSha256)
    {
        var reasoningParse = _responseParser.Parse(reasoningContent);
        var reasoningScore = _scoringEngine.Score(
            $"{runName} Thinking",
            reasoningParse.Findings,
            groundTruth,
            source,
            profile: scoringProfile,
            context: new ScoreComputationContext
            {
                GroundTruthSha256 = groundTruthSha256,
                SourceSha256 = source.Sha256,
                PromptVersion = PromptVersions.ReasoningDisclosure
            });
        return ReasoningDisclosureAnalyzer.Analyze(reasoningContent, reasoningParse, reasoningScore, outputScore);
    }

    private static (BenchmarkRunArtifacts Artifacts, string SelectionReason) SelectTruthAuditTarget(BenchmarkRunResult result, string requestedSource)
    {
        var source = (requestedSource ?? "best").Trim().ToLowerInvariant();
        if (source is "run1" or "run-1" or "1")
        {
            return (result.Run1, "forced_run1");
        }

        if (source is "run2" or "run-2" or "2")
        {
            return result.Run2 is null
                ? (result.Run1, "forced_run2_unavailable_fallback_run1")
                : (result.Run2, "forced_run2");
        }

        if (result.Run2 is null)
        {
            return (result.Run1, "only_run1_available");
        }

        if (result.Run2.Score.ScorePercent > result.Run1.Score.ScorePercent)
        {
            return (result.Run2, "higher_score");
        }

        if (Math.Abs(result.Run2.Score.ScorePercent - result.Run1.Score.ScorePercent) < 0.0001)
        {
            return (result.Run2, "tie_prefer_run2");
        }

        return (result.Run1, "higher_score");
    }

    private static void TryArchive(BenchmarkRunResult result, BenchmarkOptions options, Action<string>? progress)
    {
        if (string.IsNullOrWhiteSpace(options.ArchiveDirectory))
        {
            return;
        }

        try
        {
            var store = new ArchiveStore(options.ArchiveDirectory);
            result.ArchivedRecordPath = store.Save(result, options.QuantOverride);
            progress?.Invoke($"Archived scorecard: {result.ArchivedRecordPath}");
        }
        catch (Exception ex)
        {
            // Archiving is a convenience layer; never fail a completed benchmark because the
            // archive write hit a permissions or path issue.
            progress?.Invoke($"Warning: could not archive run ({ex.Message}).");
        }
    }

    private void ValidatePreflight(BenchmarkOptions options, SourceDocument source, GroundTruthDocument groundTruth)
    {
        if (!File.Exists(options.SourcePath))
        {
            throw new FileNotFoundException("Source file not found.", options.SourcePath);
        }

        if (!File.Exists(options.GroundTruthPath))
        {
            throw new FileNotFoundException("Ground-truth file not found.", options.GroundTruthPath);
        }

        if (!string.Equals(source.Sha256, groundTruth.SourceSha256, StringComparison.OrdinalIgnoreCase) && !options.AllowHashMismatch)
        {
            throw new InvalidOperationException(
                $"Source hash mismatch. Ground truth expects {groundTruth.SourceSha256}, actual {source.Sha256}. Use --allow-hash-mismatch only for local scorer development.");
        }

        if (groundTruth.Policy?.HiddenFromModel != true)
        {
            throw new InvalidOperationException("Ground-truth policy must set hidden_from_model=true.");
        }

        foreach (var vulnerability in groundTruth.Vulnerabilities)
        {
            var evidenceAnchors = vulnerability.EvidenceAnchors.HasAny
                ? vulnerability.EvidenceAnchors.Must.Concat(vulnerability.EvidenceAnchors.Should)
                : vulnerability.RequiredEvidence;
            foreach (var evidence in evidenceAnchors.Where(e => !string.IsNullOrWhiteSpace(e)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!source.Text.Contains(evidence, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException($"Ground truth drift: {vulnerability.Id} evidence is missing from source: {evidence}");
                }
            }
        }
    }

    public static string BuildSystemPrompt(string runKind)
    {
        return "You are participating in the SuperCalc local LLM security benchmark. "
               + $"Task: {runKind}. "
               + "Analyze only the provided source text. Do not use external files or assume an expected answer count. "
               + "Return strict JSON only.";
    }
}
