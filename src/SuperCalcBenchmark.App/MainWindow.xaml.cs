using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using SuperCalcBenchmark.Core;

namespace SuperCalcBenchmark.App;

public partial class MainWindow : Window
{
    private readonly string _repositoryRoot;
    private CancellationTokenSource? _benchmarkCancellation;
    private BenchmarkRunResult? _lastResult;

    public MainWindow()
    {
        InitializeComponent();
        _repositoryRoot = FindRepositoryRoot();
        DotNetInfoTextBlock.Text = $".NET {Environment.Version.Major} | {_repositoryRoot}";
        ShowRawOutputPlaceholder(Run1RawPanel, "Noch kein Run-1-Output. Nach dem Benchmark siehst du hier Prompt, Thinking, Output und Raw API Response.");
        ShowRawOutputPlaceholder(Run2RawPanel, "Noch kein Run-2-Output. Nach dem Benchmark siehst du hier Prompt, Thinking, Output und Raw API Response.");
        AppendLog("Bereit. Wenn du ein neues Modell in llama-server geladen hast: Refresh Models klicken, Modell wählen, Benchmark starten.");
    }

    private async void RefreshModelsButton_Click(object sender, RoutedEventArgs e)
    {
        await RefreshModelsAsync();
    }

    private async Task RefreshModelsAsync()
    {
        SetBusy(true, benchmarkRunning: false);
        StatusTextBlock.Text = "Modelle werden geladen...";
        AppendLog($"GET {ServerUrlTextBox.Text.Trim().TrimEnd('/')}/v1/models");

        try
        {
            using var client = new LlamaCppClient(TimeSpan.FromSeconds(30));
            var models = await client.GetModelsAsync(ServerUrlTextBox.Text.Trim());
            ModelComboBox.ItemsSource = models;
            if (models.Count > 0)
            {
                ModelComboBox.SelectedIndex = 0;
                StatusTextBlock.Text = $"{models.Count} Modell(e) geladen. Du kannst jetzt Benchmark starten.";
                AppendLog($"Modelle geladen: {string.Join(", ", models)}");
            }
            else
            {
                StatusTextBlock.Text = "Server erreichbar, aber keine Modelle erhalten.";
                AppendLog("Keine Modelle erhalten.");
            }
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = "Model-Refresh fehlgeschlagen.";
            AppendLog("FEHLER beim Model-Refresh: " + ex.Message);
            MessageBox.Show(this,
                "Konnte keine Modelle laden. Läuft llama-server auf der angegebenen URL?\n\n" + ex.Message,
                "Refresh Models fehlgeschlagen",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        finally
        {
            SetBusy(false, benchmarkRunning: false);
        }
    }

    private async void StartBenchmarkButton_Click(object sender, RoutedEventArgs e)
    {
        var model = ModelComboBox.SelectedItem?.ToString();
        if (string.IsNullOrWhiteSpace(model))
        {
            MessageBox.Show(this,
                "Bitte zuerst Refresh Models klicken und ein Modell auswählen.",
                "Kein Modell ausgewählt",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        BenchmarkOptions options;
        try
        {
            options = BuildOptions(model);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Ungültige Optionen", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        ResetResultUi();
        _benchmarkCancellation = new CancellationTokenSource();
        SetBusy(true, benchmarkRunning: true);
        StatusTextBlock.Text = "Benchmark läuft... Run 1 + Run 2 können je nach Modell einige Minuten dauern.";
        AppendLog($"Benchmark startet für Modell: {model}");
        AppendLog($"Repository: {_repositoryRoot}");

        try
        {
            var runner = new BenchmarkRunner();
            var result = await runner.RunAsync(options, Progress, _benchmarkCancellation.Token);
            _lastResult = result;
            ApplyResult(result);
            StatusTextBlock.Text = "Benchmark abgeschlossen.";
            AppendLog("Benchmark abgeschlossen.");
        }
        catch (OperationCanceledException)
        {
            StatusTextBlock.Text = "Benchmark abgebrochen.";
            AppendLog("Benchmark abgebrochen.");
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = "Benchmark fehlgeschlagen.";
            AppendLog("FEHLER beim Benchmark: " + ex);
            MessageBox.Show(this,
                "Benchmark fehlgeschlagen:\n\n" + ex.Message,
                "Fehler",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            _benchmarkCancellation?.Dispose();
            _benchmarkCancellation = null;
            SetBusy(false, benchmarkRunning: false);
        }
    }

    private void CancelBenchmarkButton_Click(object sender, RoutedEventArgs e)
    {
        _benchmarkCancellation?.Cancel();
        CancelBenchmarkButton.IsEnabled = false;
        StatusTextBlock.Text = "Abbruch angefordert...";
        AppendLog("Abbruch angefordert...");
    }

    private void OpenReportButton_Click(object sender, RoutedEventArgs e)
    {
        if (_lastResult is null)
        {
            return;
        }

        OpenPath(Path.Combine(_lastResult.OutputDirectory, "report.md"));
    }

    private void OpenFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (_lastResult is null)
        {
            return;
        }

        OpenPath(_lastResult.OutputDirectory);
    }

    private BenchmarkOptions BuildOptions(string model)
    {
        var maxTokens = ParseInt(MaxTokensTextBox.Text, "Max Tokens", -1, min: -1);
        var timeoutSeconds = ParseInt(TimeoutTextBox.Text, "Timeout", 1200, min: 30);
        var seed = ParseInt(SeedTextBox.Text, "Seed", 12345, min: int.MinValue);
        var outputDirectory = string.IsNullOrWhiteSpace(OutputDirectoryTextBox.Text)
            ? null
            : Path.GetFullPath(OutputDirectoryTextBox.Text.Trim());

        return new BenchmarkOptions
        {
            ServerUrl = ServerUrlTextBox.Text.Trim(),
            Model = model,
            SourcePath = Path.Combine(_repositoryRoot, "enhanced_calc.cpp"),
            GroundTruthPath = Path.Combine(_repositoryRoot, "benchmarks", "supercalc-v3", "ground_truth.json"),
            AnalysisPromptPath = Path.Combine(_repositoryRoot, "benchmarks", "supercalc-v3", "prompts", "analysis_v1.md"),
            SelfValidatePromptPath = Path.Combine(_repositoryRoot, "benchmarks", "supercalc-v3", "prompts", "self_validate_v1.md"),
            SchemaPath = Path.Combine(_repositoryRoot, "benchmarks", "supercalc-v3", "schemas", "llm_findings.schema.json"),
            OutputDirectory = outputDirectory,
            Temperature = 0.0,
            TopP = 1.0,
            MaxTokens = maxTokens,
            Seed = seed,
            Timeout = TimeSpan.FromSeconds(timeoutSeconds),
            SkipResponseFormat = SkipResponseFormatCheckBox.IsChecked == true,
            DisableThinking = DisableThinkingCheckBox.IsChecked == true
        };
    }

    private void ApplyResult(BenchmarkRunResult result)
    {
        Run1ScoreTextBlock.Text = $"{result.Run1.Score.ScorePercent:0.##}/100";
        Run1DetailsTextBlock.Text = FormatScoreDetails(result.Run1);
        Run1MatrixGrid.ItemsSource = result.Run1.Score.Vulnerabilities;
        Run1FindingsGrid.ItemsSource = result.Run1.Score.Findings;
        PopulateRawOutputPanel(Run1RawPanel, result.Run1);
        AppendLoopWarnings(result.Run1);

        if (result.Run2 is not null)
        {
            Run2ScoreTextBlock.Text = $"{result.Run2.Score.ScorePercent:0.##}/100";
            Run2DetailsTextBlock.Text = FormatScoreDetails(result.Run2);
            Run2MatrixGrid.ItemsSource = result.Run2.Score.Vulnerabilities;
            Run2FindingsGrid.ItemsSource = result.Run2.Score.Findings;
            PopulateRawOutputPanel(Run2RawPanel, result.Run2);
            AppendLoopWarnings(result.Run2);
        }

        if (result.Comparison is not null)
        {
            ComparisonTextBlock.Text =
                $"TP behalten: {result.Comparison.KeptTruePositiveIds.Count}\n" +
                $"TP verloren: {result.Comparison.DroppedTruePositiveIds.Count}\n" +
                $"TP neu: {result.Comparison.AddedTruePositiveIds.Count}\n" +
                $"FP Run1 → Run2: {result.Comparison.Run1FalsePositives} → {result.Comparison.Run2FalsePositives}\n" +
                $"TP-Retention: {result.Comparison.TruePositiveRetention:P1}";
        }

        OutputPathTextBlock.Text = result.OutputDirectory;
        OpenReportButton.IsEnabled = File.Exists(Path.Combine(result.OutputDirectory, "report.md"));
        OpenFolderButton.IsEnabled = Directory.Exists(result.OutputDirectory);
    }

    private void ResetResultUi()
    {
        _lastResult = null;
        Run1ScoreTextBlock.Text = "Läuft...";
        Run1DetailsTextBlock.Text = string.Empty;
        Run2ScoreTextBlock.Text = "Wartet auf Run 1";
        Run2DetailsTextBlock.Text = string.Empty;
        ComparisonTextBlock.Text = "Nach dem Benchmark sichtbar";
        Run1MatrixGrid.ItemsSource = null;
        Run2MatrixGrid.ItemsSource = null;
        Run1FindingsGrid.ItemsSource = null;
        Run2FindingsGrid.ItemsSource = null;
        ShowRawOutputPlaceholder(Run1RawPanel, "Run 1 läuft bzw. wartet auf Server-Antwort...");
        ShowRawOutputPlaceholder(Run2RawPanel, "Run 2 wartet auf Run 1...");
        ProgressLogTextBox.Clear();
        OutputPathTextBlock.Text = "Run läuft...";
        OpenReportButton.IsEnabled = false;
        OpenFolderButton.IsEnabled = false;
    }

    private static string FormatScoreDetails(BenchmarkRunArtifacts artifacts)
    {
        var score = artifacts.Score;
        var details = $"TP: {score.FullTruePositives} full + {score.PartialTruePositives} partial | " +
                      $"FP: {score.FalsePositives} | FN: {score.Missed}\n" +
                      $"Precision: {score.Precision:P1} | Recall: {score.Recall:P1} | F1: {score.F1:P1}\n" +
                      $"Finish: {artifacts.FinishReason} | Content: {artifacts.Response.Length} chars | Reasoning: {artifacts.ReasoningContent.Length} chars";

        if (!string.IsNullOrWhiteSpace(artifacts.Parse.Warning))
        {
            details += $"\nParse: {artifacts.Parse.Warning}";
        }

        return details;
    }

    private void AppendLoopWarnings(BenchmarkRunArtifacts artifacts)
    {
        var responseLoop = OutputLoopDetector.Analyze(artifacts.Response);
        var reasoningLoop = OutputLoopDetector.Analyze(artifacts.ReasoningContent);
        if (responseLoop.HasSuspectedLoop)
        {
            AppendLog($"WARNUNG {artifacts.RunName}: möglicher Loop im finalen Output — {responseLoop.Summary}");
        }

        if (reasoningLoop.HasSuspectedLoop)
        {
            AppendLog($"WARNUNG {artifacts.RunName}: möglicher Loop im Thinking — {reasoningLoop.Summary}");
        }
    }

    private static void PopulateRawOutputPanel(StackPanel panel, BenchmarkRunArtifacts artifacts)
    {
        panel.Children.Clear();
        panel.Children.Add(CreateDiagnosticsBlock(artifacts));
        panel.Children.Add(CreateTextExpander(
            "Gesendeter Request JSON (System + User + Parameter)",
            artifacts.RequestJson,
            Brushes.DarkSlateBlue,
            FontStyles.Normal,
            isExpanded: false,
            background: Color.FromRgb(246, 248, 255)));
        panel.Children.Add(CreateTextExpander(
            "User-Prompt aus dem Programm",
            artifacts.Prompt,
            Brushes.Black,
            FontStyles.Normal,
            isExpanded: false,
            background: Colors.White));
        panel.Children.Add(CreateTextExpander(
            $"assistant message.content — OUTPUT ({artifacts.Response.Length:N0} chars)",
            artifacts.Response,
            Brushes.Red,
            FontStyles.Normal,
            isExpanded: true,
            background: Color.FromRgb(255, 246, 246)));
        panel.Children.Add(CreateTextExpander(
            $"assistant message.reasoning_content — THINKING ({artifacts.ReasoningContent.Length:N0} chars)",
            artifacts.ReasoningContent,
            Brushes.DimGray,
            FontStyles.Italic,
            isExpanded: false,
            background: Color.FromRgb(248, 248, 248)));
        panel.Children.Add(CreateTextExpander(
            $"Raw API response, unverändert ({artifacts.RawResponse.Length:N0} chars)",
            artifacts.RawResponse,
            Brushes.Black,
            FontStyles.Normal,
            isExpanded: false,
            background: Colors.White));
    }

    private static void ShowRawOutputPlaceholder(StackPanel panel, string message)
    {
        panel.Children.Clear();
        panel.Children.Add(new TextBlock
        {
            Text = message,
            Foreground = Brushes.DimGray,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(4)
        });
    }

    private static Border CreateDiagnosticsBlock(BenchmarkRunArtifacts artifacts)
    {
        var responseLoop = OutputLoopDetector.Analyze(artifacts.Response);
        var reasoningLoop = OutputLoopDetector.Analyze(artifacts.ReasoningContent);
        var hasWarning = responseLoop.HasSuspectedLoop
            || reasoningLoop.HasSuspectedLoop
            || (string.IsNullOrWhiteSpace(artifacts.Response) && !string.IsNullOrWhiteSpace(artifacts.ReasoningContent));

        var builder = new StringBuilder();
        builder.AppendLine($"Finish: {EmptyFallback(artifacts.FinishReason)} | Output: {artifacts.Response.Length:N0} chars | Thinking: {artifacts.ReasoningContent.Length:N0} chars");
        builder.AppendLine($"response_format: {artifacts.UsedResponseFormat} | retry ohne response_format: {artifacts.RetriedWithoutResponseFormat} | thinking-disable hint: {artifacts.UsedThinkingControl}");
        builder.AppendLine($"Loop-Check Output: {responseLoop.Summary}");
        builder.AppendLine($"Loop-Check Thinking: {reasoningLoop.Summary}");

        if (string.IsNullOrWhiteSpace(artifacts.Response) && !string.IsNullOrWhiteSpace(artifacts.ReasoningContent))
        {
            builder.AppendLine("WARNUNG: Server lieferte nur reasoning_content, aber keine finale message.content. Das sieht oft nach Max-Token-Erschöpfung oder endlosem Thinking aus.");
        }

        AppendRepetitionDetails(builder, "Output", responseLoop);
        AppendRepetitionDetails(builder, "Thinking", reasoningLoop);

        return new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8),
            Margin = new Thickness(0, 0, 0, 8),
            BorderBrush = hasWarning ? Brushes.DarkOrange : Brushes.LightGray,
            Background = new SolidColorBrush(hasWarning ? Color.FromRgb(255, 248, 225) : Color.FromRgb(247, 247, 247)),
            Child = new TextBlock
            {
                Text = builder.ToString().TrimEnd(),
                FontFamily = new FontFamily("Segoe UI"),
                Foreground = hasWarning ? Brushes.DarkRed : Brushes.DimGray,
                TextWrapping = TextWrapping.Wrap
            }
        };
    }

    private static void AppendRepetitionDetails(StringBuilder builder, string label, OutputLoopDiagnostics diagnostics)
    {
        if (!diagnostics.HasSuspectedLoop)
        {
            return;
        }

        foreach (var repetition in diagnostics.Repetitions.Take(3))
        {
            builder.AppendLine($"{label} Wiederholung: {repetition.Kind} x{repetition.Occurrences} → {repetition.Snippet}");
        }
    }

    private static Expander CreateTextExpander(
        string header,
        string text,
        Brush foreground,
        FontStyle fontStyle,
        bool isExpanded,
        Color background)
    {
        var displayText = string.IsNullOrEmpty(text) ? "<empty>" : text;
        var document = new FlowDocument
        {
            PagePadding = new Thickness(8),
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            PageWidth = 20000,
            Background = new SolidColorBrush(background)
        };
        document.Blocks.Add(new Paragraph(new Run(displayText))
        {
            Margin = new Thickness(0),
            Foreground = foreground,
            FontStyle = fontStyle
        });

        return new Expander
        {
            Header = header,
            IsExpanded = isExpanded,
            Margin = new Thickness(0, 6, 0, 0),
            Content = new RichTextBox
            {
                IsReadOnly = true,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12,
                MinHeight = 90,
                MaxHeight = 360,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                BorderBrush = Brushes.LightGray,
                Background = new SolidColorBrush(background),
                Document = document
            }
        };
    }

    private static string EmptyFallback(string value) => string.IsNullOrWhiteSpace(value) ? "<empty>" : value;

    private void Progress(string message)
    {
        Dispatcher.Invoke(() =>
        {
            StatusTextBlock.Text = message;
            AppendLog(message);
        });
    }

    private void AppendLog(string message)
    {
        ProgressLogTextBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
        ProgressLogTextBox.ScrollToEnd();
    }

    private void SetBusy(bool busy, bool benchmarkRunning)
    {
        RefreshModelsButton.IsEnabled = !busy;
        StartBenchmarkButton.IsEnabled = !busy;
        ServerUrlTextBox.IsEnabled = !busy;
        ModelComboBox.IsEnabled = !busy;
        MaxTokensTextBox.IsEnabled = !busy;
        TimeoutTextBox.IsEnabled = !busy;
        SeedTextBox.IsEnabled = !busy;
        OutputDirectoryTextBox.IsEnabled = !busy;
        SkipResponseFormatCheckBox.IsEnabled = !busy;
        DisableThinkingCheckBox.IsEnabled = !busy;
        CancelBenchmarkButton.IsEnabled = benchmarkRunning;
    }

    private static int ParseInt(string value, string label, int defaultValue, int min)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        if (!int.TryParse(value.Trim(), out var parsed) || parsed < min)
        {
            throw new ArgumentException($"{label} muss eine ganze Zahl >= {min} sein.");
        }

        return parsed;
    }

    private static void OpenPath(string path)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true
        });
    }

    private static string FindRepositoryRoot()
    {
        foreach (var start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(start);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "enhanced_calc.cpp")) &&
                    File.Exists(Path.Combine(directory.FullName, "benchmarks", "supercalc-v3", "ground_truth.json")))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }
        }

        return Environment.CurrentDirectory;
    }
}
