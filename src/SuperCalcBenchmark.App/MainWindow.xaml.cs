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

    // Live-streaming UI state. Tokens arrive via IProgress and are appended to these
    // text boxes in real time. _activeLivePanel points at whichever run is currently
    // streaming (Run 1 first, then Run 2).
    private StackPanel? _activeLivePanel;
    private TextBox? _liveReasoningBox;
    private TextBox? _liveContentBox;
    private TextBlock? _liveStatusBlock;
    private int _liveReasoningChars;
    private int _liveContentChars;

    public MainWindow()
    {
        InitializeComponent();
        _repositoryRoot = FindRepositoryRoot();
        DotNetInfoTextBlock.Text = $".NET {Environment.Version.Major} | {_repositoryRoot}";
        ShowRawOutputPlaceholder(Run1RawPanel, "Noch kein Run-1-Output. Nach dem Benchmark siehst du hier Prompt, Thinking, Output und Raw API Response.");
        ShowRawOutputPlaceholder(Run2RawPanel, "Noch kein Run-2-Output. Nach dem Benchmark siehst du hier Prompt, Thinking, Output und Raw API Response.");
        AppendLog("Bereit. Wenn du ein neues Modell in llama-server geladen hast: Refresh Models klicken, Modell wählen, Benchmark starten.");
        RefreshComparison(preserveSelection: false);
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
        AppendLog($"Request-Settings: max_tokens={options.MaxTokens}, response_format={!options.SkipResponseFormat}, disable_thinking={options.DisableThinking}, timeout={options.Timeout.TotalSeconds:0}s");

        try
        {
            var runner = new BenchmarkRunner();

            // IProgress created on the UI thread → Report() callbacks are marshalled
            // back to the UI thread automatically, so we can touch controls directly.
            var streamProgress = new Progress<ChatStreamDelta>(OnStreamDelta);

            // Prepare the live view for Run 1 before the first token arrives.
            BeginLiveRun(Run1RawPanel, "Run 1 — Blind Analysis");

            var result = await runner.RunAsync(
                options,
                Progress,
                _benchmarkCancellation.Token,
                onRunCompleted: OnRunCompleted,
                streamProgress: streamProgress);

            _lastResult = result;

            // Final pass: comparison panel + open buttons (per-run UI already rendered).
            ApplyComparisonAndPaths(result);
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

    private void PreviewPromptButton_Click(object sender, RoutedEventArgs e)
    {
        var model = ModelComboBox.SelectedItem?.ToString();
        if (string.IsNullOrWhiteSpace(model))
        {
            model = "<model-from-refresh-models>";
        }

        try
        {
            var options = BuildOptions(model);
            var source = SourceDocument.Load(options.SourcePath);
            var promptBuilder = new PromptBuilder();
            var run1Prompt = promptBuilder.BuildAnalysisPrompt(source, options.AnalysisPromptPath, options.SchemaPath);
            var run1Request = LlamaCppClient.BuildChatRequestJsonForDiagnostics(
                options.Model,
                BenchmarkRunner.BuildSystemPrompt("Run 1 blind security analysis"),
                run1Prompt,
                options);

            var run2Prompt = promptBuilder.BuildSelfValidationPrompt(
                source,
                options.SelfValidatePromptPath,
                options.SchemaPath,
                "<Run-1 response will be inserted here after Run 1>");
            var run2Request = LlamaCppClient.BuildChatRequestJsonForDiagnostics(
                options.Model,
                BenchmarkRunner.BuildSystemPrompt("Run 2 self-validation"),
                run2Prompt,
                options);

            PopulatePromptPreviewPanel(Run1RawPanel, "Run 1 Prompt Preview — nichts wurde an den Server gesendet", run1Prompt, run1Request);
            PopulatePromptPreviewPanel(Run2RawPanel, "Run 2 Prompt Preview — Platzhalter statt echter Run-1-Antwort", run2Prompt, run2Request);
            StatusTextBlock.Text = "Prompt/Request Preview erzeugt. Benchmark wurde nicht gestartet.";
            AppendLog($"Prompt Preview erzeugt: Run1 {run1Prompt.Length:N0} chars, Run2+Platzhalter {run2Prompt.Length:N0} chars.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Prompt Preview fehlgeschlagen", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
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
            DisableThinking = DisableThinkingCheckBox.IsChecked == true,
            ArchiveDirectory = Path.Combine(_repositoryRoot, ArchiveStore.DefaultArchiveFolderName),
            QuantOverride = string.IsNullOrWhiteSpace(QuantTextBox.Text) ? null : QuantTextBox.Text.Trim()
        };
    }

    // ---- Live streaming + per-run rendering ---------------------------------

    private void BeginLiveRun(StackPanel panel, string runLabel)
    {
        _activeLivePanel = panel;
        _liveReasoningChars = 0;
        _liveContentChars = 0;

        panel.Children.Clear();

        _liveStatusBlock = new TextBlock
        {
            Text = $"{runLabel}: warte auf erste Tokens vom Server...",
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.SteelBlue,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 6)
        };
        panel.Children.Add(_liveStatusBlock);

        _liveReasoningBox = CreateLiveBox();
        _liveContentBox = CreateLiveBox();

        panel.Children.Add(new Expander
        {
            Header = "LIVE Thinking (reasoning_content)",
            IsExpanded = true,
            Margin = new Thickness(0, 4, 0, 0),
            Content = _liveReasoningBox
        });
        panel.Children.Add(new Expander
        {
            Header = "LIVE Output (message.content)",
            IsExpanded = true,
            Margin = new Thickness(0, 6, 0, 0),
            Content = _liveContentBox
        });
    }

    private static TextBox CreateLiveBox()
    {
        return new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            MinHeight = 90,
            MaxHeight = 300,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            BorderBrush = Brushes.LightGray
        };
    }

    private void OnStreamDelta(ChatStreamDelta delta)
    {
        switch (delta.Kind)
        {
            case ChatStreamDeltaKind.AttemptStart:
                // A new (possibly retried) attempt began. Clear live buffers so tokens
                // from a failed attempt don't bleed into the next one.
                _liveReasoningChars = 0;
                _liveContentChars = 0;
                if (_liveReasoningBox is not null) _liveReasoningBox.Clear();
                if (_liveContentBox is not null) _liveContentBox.Clear();
                if (_liveStatusBlock is not null)
                {
                    var attemptNo = delta.AttemptIndex + 1;
                    _liveStatusBlock.Text = delta.AttemptCount > 1
                        ? $"Versuch {attemptNo}/{delta.AttemptCount} ({delta.AttemptLabel}) — streamt..."
                        : $"Streamt... ({delta.AttemptLabel})";
                }
                break;

            case ChatStreamDeltaKind.Reasoning:
                if (_liveReasoningBox is not null)
                {
                    _liveReasoningBox.AppendText(delta.Text);
                    _liveReasoningBox.ScrollToEnd();
                }
                _liveReasoningChars += delta.Text.Length;
                UpdateLiveStatusCounts();
                break;

            case ChatStreamDeltaKind.Content:
                if (_liveContentBox is not null)
                {
                    _liveContentBox.AppendText(delta.Text);
                    _liveContentBox.ScrollToEnd();
                }
                _liveContentChars += delta.Text.Length;
                UpdateLiveStatusCounts();
                break;
        }
    }

    private void UpdateLiveStatusCounts()
    {
        if (_liveStatusBlock is null)
        {
            return;
        }

        _liveStatusBlock.Text = $"Streamt... Thinking: {_liveReasoningChars:N0} chars | Output: {_liveContentChars:N0} chars";
    }

    private void OnRunCompleted(BenchmarkRunArtifacts artifacts)
    {
        // The runner awaits with ConfigureAwait(false), so this callback fires on a
        // thread-pool thread. Marshal onto the UI thread before touching any controls.
        Dispatcher.Invoke(() =>
        {
            // Render the finished run's score, matrix, findings and the full (non-live)
            // raw-output panel right away. For Run 2, also flip the live view over first.
            var isRun2 = string.Equals(artifacts.RunName, "Run 2", StringComparison.OrdinalIgnoreCase);

            if (!isRun2)
            {
                Run1ScoreTextBlock.Text = $"{artifacts.Score.ScorePercent:0.##}/100";
                Run1DetailsTextBlock.Text = FormatScoreDetails(artifacts);
                Run1MatrixGrid.ItemsSource = artifacts.Score.Vulnerabilities;
                Run1FindingsGrid.ItemsSource = artifacts.Score.Findings;
                PopulateRawOutputPanel(Run1RawPanel, artifacts);
                AppendLoopWarnings(artifacts);

                // Run 1 done → spin up the live view for Run 2.
                Run2ScoreTextBlock.Text = "Läuft...";
                BeginLiveRun(Run2RawPanel, "Run 2 — Self-Validation");
            }
            else
            {
                Run2ScoreTextBlock.Text = $"{artifacts.Score.ScorePercent:0.##}/100";
                Run2DetailsTextBlock.Text = FormatScoreDetails(artifacts);
                Run2MatrixGrid.ItemsSource = artifacts.Score.Vulnerabilities;
                Run2FindingsGrid.ItemsSource = artifacts.Score.Findings;
                PopulateRawOutputPanel(Run2RawPanel, artifacts);
                AppendLoopWarnings(artifacts);
            }
        });
    }

    private void ApplyComparisonAndPaths(BenchmarkRunResult result)
    {
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

        // A new run just landed in the archive → refresh the comparison tab so it shows up
        // without the user having to click "Archiv neu laden".
        RefreshComparison(preserveSelection: true);
    }

    // ---- Comparison tab ------------------------------------------------------

    private const string AllFamiliesLabel = "Alle Modelle";

    private string ArchiveRoot => Path.Combine(_repositoryRoot, ArchiveStore.DefaultArchiveFolderName);

    private IReadOnlyList<ArchiveGroup> _comparisonGroups = [];

    private void RefreshComparisonButton_Click(object sender, RoutedEventArgs e)
        => RefreshComparison(preserveSelection: true);

    private void ComparisonFilter_Changed(object sender, SelectionChangedEventArgs e)
    {
        // ComboBoxItem IsSelected in XAML can fire SelectionChanged while InitializeComponent()
        // is still constructing the Vergleich tab. At that point controls declared later in
        // the XAML (for example ComparisonGrid) are not assigned yet.
        if (ComparisonGrid is null || OpenComparisonButton is null || ComparisonStatusTextBlock is null)
        {
            return;
        }

        RebuildComparisonGrid();
    }

    private void RefreshComparison(bool preserveSelection)
    {
        try
        {
            var store = new ArchiveStore(ArchiveRoot);
            _comparisonGroups = store.LoadGroups();

            var previous = preserveSelection ? (FamilyFilterComboBox.SelectedItem as string) : null;

            var families = new List<string> { AllFamiliesLabel };
            families.AddRange(_comparisonGroups
                .Select(g => g.ModelFamily)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase));

            FamilyFilterComboBox.ItemsSource = families;
            FamilyFilterComboBox.SelectedItem = previous is not null && families.Contains(previous)
                ? previous
                : AllFamiliesLabel;

            RebuildComparisonGrid();
        }
        catch (Exception ex)
        {
            ComparisonStatusTextBlock.Text = "Archiv konnte nicht geladen werden: " + ex.Message;
            AppendLog("FEHLER beim Laden des Archivs: " + ex.Message);
        }
    }

    private void RebuildComparisonGrid()
    {
        var report = BuildCurrentComparison();
        ComparisonGrid.ItemsSource = report.Series;
        OpenComparisonButton.IsEnabled = !report.IsEmpty;

        if (report.IsEmpty)
        {
            ComparisonStatusTextBlock.Text = _comparisonGroups.Count == 0
                ? "Noch keine archivierten Runs. Starte einen Benchmark — danach erscheinen die Ergebnisse hier."
                : "Für die gewählte Modellfamilie gibt es keine Runs.";
            return;
        }

        var aggregate = SelectedAggregate == ComparisonAggregate.Best ? "Bester Run" : "Durchschnitt";
        ComparisonStatusTextBlock.Text =
            $"{report.Series.Count} Modell/Quant-Gruppe(n), {report.VulnerabilityAxis.Count} Schwachstellen · Wertung: {aggregate}. " +
            "Für Säulen- und Netzdiagramm auf 'Diagramme öffnen (HTML)' klicken.";
    }

    private ComparisonReport BuildCurrentComparison()
    {
        var benchmarkId = _comparisonGroups
            .SelectMany(g => g.Records)
            .Select(r => r.BenchmarkId)
            .FirstOrDefault() ?? "supercalc";

        var family = FamilyFilterComboBox.SelectedItem as string;
        var familyFilter = string.Equals(family, AllFamiliesLabel, StringComparison.Ordinal) ? null : family;

        return ComparisonReport.Build(_comparisonGroups, benchmarkId, SelectedAggregate, familyFilter);
    }

    private ComparisonAggregate SelectedAggregate =>
        (AggregateComboBox.SelectedItem as ComboBoxItem)?.Content as string == "Bester Run"
            ? ComparisonAggregate.Best
            : ComparisonAggregate.Average;

    private void OpenComparisonButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var report = BuildCurrentComparison();
            if (report.IsEmpty)
            {
                return;
            }

            var outputDir = Path.Combine(ArchiveRoot, "_reports");
            var htmlPath = new ComparisonHtmlWriter().Write(report, outputDir);
            AppendLog("Vergleichs-HTML erzeugt: " + htmlPath);
            OpenPath(htmlPath);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Vergleich konnte nicht erzeugt werden:\n\n" + ex.Message,
                "Fehler", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OpenArchiveFolderButton_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(ArchiveRoot);
        OpenPath(ArchiveRoot);
    }

    private void ResetResultUi()
    {
        _lastResult = null;
        _activeLivePanel = null;
        _liveReasoningBox = null;
        _liveContentBox = null;
        _liveStatusBlock = null;
        _liveReasoningChars = 0;
        _liveContentChars = 0;
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

    private static void PopulatePromptPreviewPanel(StackPanel panel, string title, string prompt, string requestJson)
    {
        panel.Children.Clear();
        panel.Children.Add(new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8),
            Margin = new Thickness(0, 0, 0, 8),
            BorderBrush = Brushes.SteelBlue,
            Background = new SolidColorBrush(Color.FromRgb(240, 247, 255)),
            Child = new TextBlock
            {
                Text = title,
                Foreground = Brushes.SteelBlue,
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap
            }
        });
        panel.Children.Add(CreateTextExpander(
            "Erste Request-JSON (System + User + Parameter)",
            requestJson,
            Brushes.DarkSlateBlue,
            FontStyles.Normal,
            isExpanded: true,
            background: Color.FromRgb(246, 248, 255)));
        panel.Children.Add(CreateTextExpander(
            $"User-Prompt ({prompt.Length:N0} chars)",
            prompt,
            Brushes.Black,
            FontStyles.Normal,
            isExpanded: true,
            background: Colors.White));
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
        PreviewPromptButton.IsEnabled = !busy;
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
