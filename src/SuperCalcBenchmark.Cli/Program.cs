using SuperCalcBenchmark.Core;

namespace SuperCalcBenchmark.Cli;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        System.Globalization.CultureInfo.DefaultThreadCurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
        System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = System.Globalization.CultureInfo.InvariantCulture;
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        var parsed = ParsedArgs.Parse(args);
        if (parsed.HelpRequested)
        {
            PrintUsage();
            return 0;
        }

        var command = parsed.Command;
        if (string.IsNullOrWhiteSpace(command))
        {
            command = parsed.Has("--model") ? "run" : "help";
        }

        try
        {
            return command.ToLowerInvariant() switch
            {
                "models" => await ListModelsAsync(parsed),
                "validate" => Validate(parsed),
                "score-fixture" or "fixture" => ScoreFixture(parsed),
                "run" or "benchmark" => await RunBenchmarkAsync(parsed),
                "help" => PrintUsageAndReturn(),
                _ => UnknownCommand(command)
            };
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Aborted.");
            return 130;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR: {ex.Message}");
            if (parsed.Has("--verbose"))
            {
                Console.Error.WriteLine(ex);
            }

            return 1;
        }
    }

    private static async Task<int> ListModelsAsync(ParsedArgs args)
    {
        var server = args.Get("--server", "http://127.0.0.1:1234");
        using var client = new LlamaCppClient(TimeSpan.FromSeconds(args.GetInt("--timeout-seconds", 30)));
        var models = await client.GetModelsAsync(server);
        if (models.Count == 0)
        {
            Console.WriteLine("No models returned by server.");
            return 2;
        }

        foreach (var model in models)
        {
            Console.WriteLine(model);
        }

        return 0;
    }

    private static int Validate(ParsedArgs args)
    {
        var options = BuildOptions(args, requireModel: false);
        var store = new GroundTruthStore();
        var result = store.Validate(options.GroundTruthPath, options.SourcePath);

        Console.WriteLine($"Ground truth: {options.GroundTruthPath}");
        Console.WriteLine($"Source:       {options.SourcePath}");
        Console.WriteLine($"Expected SHA: {result.ExpectedSourceSha256}");
        Console.WriteLine($"Actual SHA:   {result.ActualSourceSha256}");
        Console.WriteLine($"Vulns:        {result.VulnerabilityCount}");
        Console.WriteLine($"Valid:        {result.IsValid}");

        foreach (var issue in result.Issues)
        {
            Console.WriteLine($"[{issue.Severity}] {issue.Message}");
        }

        return result.IsValid ? 0 : 1;
    }

    private static int ScoreFixture(ParsedArgs args)
    {
        var response = args.Get("--response", args.Get("--fixture", string.Empty));
        if (string.IsNullOrWhiteSpace(response))
        {
            throw new ArgumentException("score-fixture requires --response <file>.");
        }

        var options = BuildOptions(args, requireModel: false);
        var runner = new BenchmarkRunner();
        var result = runner.ScoreFixture(options, response, args.Get("--run-name", "Fixture"));
        PrintScore(result.Run1.Score);
        Console.WriteLine($"Report: {Path.Combine(result.OutputDirectory, "report.md")}");
        return 0;
    }

    private static async Task<int> RunBenchmarkAsync(ParsedArgs args)
    {
        var options = BuildOptions(args, requireModel: true);
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cts.Cancel();
        };

        var runner = new BenchmarkRunner();
        var result = await runner.RunAsync(options, message => Console.Error.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}"), cts.Token);

        PrintScore(result.Run1.Score);
        if (result.Run2 is not null)
        {
            PrintScore(result.Run2.Score);
        }

        Console.WriteLine($"Report: {Path.Combine(result.OutputDirectory, "report.md")}");
        return 0;
    }

    private static BenchmarkOptions BuildOptions(ParsedArgs args, bool requireModel)
    {
        var model = args.Get("--model", string.Empty);
        if (requireModel && string.IsNullOrWhiteSpace(model))
        {
            throw new ArgumentException("A model id is required. Use --model <MODEL> or run 'models' first.");
        }

        return new BenchmarkOptions
        {
            ServerUrl = args.Get("--server", "http://127.0.0.1:1234"),
            Model = model,
            SourcePath = args.Get("--source", "enhanced_calc.cpp"),
            GroundTruthPath = args.Get("--ground-truth", Path.Combine("benchmarks", "supercalc-v3", "ground_truth.json")),
            AnalysisPromptPath = args.Get("--analysis-prompt", Path.Combine("benchmarks", "supercalc-v3", "prompts", "analysis_v1.md")),
            SelfValidatePromptPath = args.Get("--self-prompt", Path.Combine("benchmarks", "supercalc-v3", "prompts", "self_validate_v1.md")),
            SchemaPath = args.Get("--schema", Path.Combine("benchmarks", "supercalc-v3", "schemas", "llm_findings.schema.json")),
            OutputDirectory = args.GetNullable("--out"),
            Temperature = args.GetDouble("--temperature", 0.0),
            TopP = args.GetDouble("--top-p", 1.0),
            MaxTokens = args.GetInt("--max-tokens", 8192),
            Seed = args.GetInt("--seed", 12345),
            Timeout = TimeSpan.FromSeconds(args.GetInt("--timeout-seconds", 1200)),
            AllowHashMismatch = args.Has("--allow-hash-mismatch"),
            SkipResponseFormat = args.Has("--skip-response-format")
        };
    }

    private static void PrintScore(ScoringResult score)
    {
        Console.WriteLine($"{score.RunName}: {score.ScorePercent:0.##}/100 | TP {score.FullTruePositives} full + {score.PartialTruePositives} partial | FP {score.FalsePositives} | FN {score.Missed} | Precision {score.Precision:P1} | Recall {score.Recall:P1}");
    }

    private static int PrintUsageAndReturn()
    {
        PrintUsage();
        return 0;
    }

    private static int UnknownCommand(string command)
    {
        Console.Error.WriteLine($"Unknown command: {command}");
        PrintUsage();
        return 2;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("SuperCalcBenchmark.Cli (.NET 10)");
        Console.WriteLine();
        Console.WriteLine("Commands:");
        Console.WriteLine("  models           List model ids from llama-server /v1/models");
        Console.WriteLine("  validate         Validate ground_truth.json against enhanced_calc.cpp");
        Console.WriteLine("  run              Execute Run 1 + Run 2 against llama-server and score offline");
        Console.WriteLine("  score-fixture    Score a saved model response without contacting llama-server");
        Console.WriteLine();
        Console.WriteLine("Examples:");
        Console.WriteLine("  dotnet run --project src/SuperCalcBenchmark.Cli -- models --server http://127.0.0.1:1234");
        Console.WriteLine("  dotnet run --project src/SuperCalcBenchmark.Cli -- validate");
        Console.WriteLine("  dotnet run --project src/SuperCalcBenchmark.Cli -- run --model MODEL_ID --max-tokens 12000");
        Console.WriteLine("  dotnet run --project src/SuperCalcBenchmark.Cli -- score-fixture --response tools/response-fixtures/perfect.json --out results/perfect");
        Console.WriteLine();
        Console.WriteLine("Common options:");
        Console.WriteLine("  --server <url>             Default: http://127.0.0.1:1234");
        Console.WriteLine("  --model <id>               Required for run");
        Console.WriteLine("  --source <file>            Default: enhanced_calc.cpp");
        Console.WriteLine("  --ground-truth <file>      Default: benchmarks/supercalc-v3/ground_truth.json");
        Console.WriteLine("  --out <dir>                Default: %LOCALAPPDATA%/SuperCalcBenchmark/Runs/<timestamp_model>");
        Console.WriteLine("  --temperature <number>     Default: 0.0");
        Console.WriteLine("  --top-p <number>           Default: 1.0");
        Console.WriteLine("  --seed <int>               Default: 12345");
        Console.WriteLine("  --max-tokens <int>         Default: 8192");
        Console.WriteLine("  --timeout-seconds <int>    Default: 1200");
        Console.WriteLine("  --skip-response-format     Do not send llama.cpp response_format");
        Console.WriteLine("  --allow-hash-mismatch      Development escape hatch; do not use for official scoring");
    }

    private sealed class ParsedArgs
    {
        private readonly Dictionary<string, string?> _options = new(StringComparer.OrdinalIgnoreCase);

        public string Command { get; private set; } = string.Empty;
        public bool HelpRequested => Has("--help") || Has("-h") || string.Equals(Command, "help", StringComparison.OrdinalIgnoreCase);

        public static ParsedArgs Parse(string[] args)
        {
            var parsed = new ParsedArgs();
            var index = 0;
            if (args.Length > 0 && !args[0].StartsWith("-", StringComparison.Ordinal))
            {
                parsed.Command = args[0];
                index = 1;
            }

            while (index < args.Length)
            {
                var current = args[index];
                if (!current.StartsWith("-", StringComparison.Ordinal))
                {
                    index++;
                    continue;
                }

                if (index + 1 < args.Length && !args[index + 1].StartsWith("-", StringComparison.Ordinal))
                {
                    parsed._options[current] = args[index + 1];
                    index += 2;
                }
                else
                {
                    parsed._options[current] = null;
                    index++;
                }
            }

            return parsed;
        }

        public bool Has(string name) => _options.ContainsKey(name);

        public string? GetNullable(string name) => _options.TryGetValue(name, out var value) ? value : null;

        public string Get(string name, string defaultValue) => _options.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value! : defaultValue;

        public int GetInt(string name, int defaultValue)
        {
            var value = GetNullable(name);
            return int.TryParse(value, out var parsed) ? parsed : defaultValue;
        }

        public double GetDouble(string name, double defaultValue)
        {
            var value = GetNullable(name);
            return double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? parsed : defaultValue;
        }
    }
}
