namespace SuperCalcBenchmark.Core;

public sealed class BenchmarkRunner
{
    private const string ToolVersion = "0.1.0";

    private readonly GroundTruthStore _groundTruthStore;
    private readonly PromptBuilder _promptBuilder;
    private readonly ResponseParser _responseParser;
    private readonly ScoringEngine _scoringEngine;
    private readonly ReportWriter _reportWriter;

    public BenchmarkRunner(
        GroundTruthStore? groundTruthStore = null,
        PromptBuilder? promptBuilder = null,
        ResponseParser? responseParser = null,
        ScoringEngine? scoringEngine = null,
        ReportWriter? reportWriter = null)
    {
        _groundTruthStore = groundTruthStore ?? new GroundTruthStore();
        _promptBuilder = promptBuilder ?? new PromptBuilder();
        _responseParser = responseParser ?? new ResponseParser();
        _scoringEngine = scoringEngine ?? new ScoringEngine();
        _reportWriter = reportWriter ?? new ReportWriter();
    }

    public async Task<BenchmarkRunResult> RunAsync(
        BenchmarkOptions options,
        Action<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(options.Model))
        {
            throw new ArgumentException("BenchmarkOptions.Model is required for a full LLM run.");
        }

        progress?.Invoke("Loading source and hidden ground truth...");
        var startedAt = DateTimeOffset.UtcNow;
        var source = SourceDocument.Load(options.SourcePath);
        var groundTruth = _groundTruthStore.Load(options.GroundTruthPath);
        ValidatePreflight(options, source, groundTruth);

        var result = new BenchmarkRunResult
        {
            ToolVersion = ToolVersion,
            BenchmarkId = groundTruth.BenchmarkId,
            StartedAt = startedAt,
            ServerUrl = options.ServerUrl,
            Model = options.Model,
            SourceFile = options.SourcePath,
            SourceSha256 = source.Sha256,
            ExpectedSourceSha256 = groundTruth.SourceSha256,
            SourceHashMatches = string.Equals(source.Sha256, groundTruth.SourceSha256, StringComparison.OrdinalIgnoreCase),
            OutputDirectory = _reportWriter.CreateRunDirectory(options, startedAt)
        };

        using var client = new LlamaCppClient(options.Timeout);

        progress?.Invoke("Building Run 1 prompt...");
        var run1Prompt = _promptBuilder.BuildAnalysisPrompt(source, options.AnalysisPromptPath, options.SchemaPath);
        progress?.Invoke("Sending Run 1 blind analysis to llama-server...");
        var run1Completion = await client.CreateChatCompletionAsync(
            options.ServerUrl,
            options.Model,
            BuildSystemPrompt("Run 1 blind security analysis"),
            run1Prompt,
            options,
            cancellationToken).ConfigureAwait(false);

        progress?.Invoke("Parsing and scoring Run 1...");
        var run1Parse = _responseParser.Parse(run1Completion.AssistantContent);
        var run1Score = _scoringEngine.Score("Run 1", run1Parse.Findings, groundTruth, source);
        result.Run1 = new BenchmarkRunArtifacts
        {
            RunName = "Run 1",
            Prompt = run1Prompt,
            Response = run1Completion.AssistantContent,
            RawResponse = run1Completion.RawResponse,
            RequestJson = run1Completion.RequestJson,
            UsedResponseFormat = run1Completion.UsedResponseFormat,
            Parse = run1Parse,
            Score = run1Score
        };

        progress?.Invoke("Building Run 2 self-validation prompt...");
        var run2Prompt = _promptBuilder.BuildSelfValidationPrompt(source, options.SelfValidatePromptPath, options.SchemaPath, run1Completion.AssistantContent);
        progress?.Invoke("Sending Run 2 self-validation to llama-server...");
        var run2Completion = await client.CreateChatCompletionAsync(
            options.ServerUrl,
            options.Model,
            BuildSystemPrompt("Run 2 self-validation"),
            run2Prompt,
            options,
            cancellationToken).ConfigureAwait(false);

        progress?.Invoke("Parsing and scoring Run 2...");
        var run2Parse = _responseParser.Parse(run2Completion.AssistantContent);
        var run2Score = _scoringEngine.Score("Run 2", run2Parse.Findings, groundTruth, source);
        result.Run2 = new BenchmarkRunArtifacts
        {
            RunName = "Run 2",
            Prompt = run2Prompt,
            Response = run2Completion.AssistantContent,
            RawResponse = run2Completion.RawResponse,
            RequestJson = run2Completion.RequestJson,
            UsedResponseFormat = run2Completion.UsedResponseFormat,
            Parse = run2Parse,
            Score = run2Score
        };

        result.Comparison = _scoringEngine.Compare(run1Score, run2Score);
        result.CompletedAt = DateTimeOffset.UtcNow;

        progress?.Invoke("Writing run artifacts and report...");
        _reportWriter.Write(result);
        progress?.Invoke($"Done. Report: {Path.Combine(result.OutputDirectory, "report.md")}");
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
        ValidatePreflight(options, source, groundTruth);

        var response = File.ReadAllText(responsePath, System.Text.Encoding.UTF8);
        var parse = _responseParser.Parse(response);
        var score = _scoringEngine.Score(runName, parse.Findings, groundTruth, source);
        var outputDirectory = _reportWriter.CreateRunDirectory(WithModelFallback(options, runName), startedAt);

        var result = new BenchmarkRunResult
        {
            ToolVersion = ToolVersion,
            BenchmarkId = groundTruth.BenchmarkId,
            StartedAt = startedAt,
            CompletedAt = DateTimeOffset.UtcNow,
            ServerUrl = "fixture",
            Model = string.IsNullOrWhiteSpace(options.Model) ? runName : options.Model,
            SourceFile = options.SourcePath,
            SourceSha256 = source.Sha256,
            ExpectedSourceSha256 = groundTruth.SourceSha256,
            SourceHashMatches = string.Equals(source.Sha256, groundTruth.SourceSha256, StringComparison.OrdinalIgnoreCase),
            OutputDirectory = outputDirectory,
            Run1 = new BenchmarkRunArtifacts
            {
                RunName = runName,
                Prompt = string.Empty,
                Response = response,
                RawResponse = response,
                RequestJson = string.Empty,
                UsedResponseFormat = false,
                Parse = parse,
                Score = score
            }
        };

        _reportWriter.Write(result);
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
                SchemaPath = original.SchemaPath,
                OutputDirectory = original.OutputDirectory,
                Temperature = original.Temperature,
                TopP = original.TopP,
                MaxTokens = original.MaxTokens,
                Seed = original.Seed,
                Timeout = original.Timeout,
                AllowHashMismatch = original.AllowHashMismatch,
                SkipResponseFormat = original.SkipResponseFormat
            };
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
            foreach (var evidence in vulnerability.RequiredEvidence.Where(e => !string.IsNullOrWhiteSpace(e)))
            {
                if (!source.Text.Contains(evidence, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException($"Ground truth drift: {vulnerability.Id} evidence is missing from source: {evidence}");
                }
            }
        }
    }

    private static string BuildSystemPrompt(string runKind)
    {
        return "You are participating in the SuperCalc local LLM security benchmark. "
               + $"Task: {runKind}. "
               + "Analyze only the provided source text. Do not use external files or assume an expected answer count. "
               + "Return strict JSON only.";
    }
}
