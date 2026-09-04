using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using SuperCalcBenchmark.Core;

namespace SuperCalcBenchmark.App;

/// <summary>
/// Campaign tab: drives the AutoTuner control API to benchmark several models × llama-server
/// builds back to back. The per-run UI (live panels, matrices, ledger) is shared with the
/// single-model flow; this file only adds catalogue loading, plan building and stop control.
/// </summary>
public partial class MainWindow
{
    private readonly ObservableCollection<CampaignModelRow> _campaignModels = [];
    private readonly ObservableCollection<CampaignRuntimeRow> _campaignRuntimes = [];
    private readonly ObservableCollection<CampaignProgressRow> _campaignProgress = [];
    private AutoTunerConnection? _autoTunerConnection;
    private CampaignRunner? _campaignRunner;
    private bool _campaignUiInitialized;

    private void EnsureCampaignUi()
    {
        if (_campaignUiInitialized)
        {
            return;
        }

        _campaignUiInitialized = true;
        CampaignModelsGrid.ItemsSource = _campaignModels;
        CampaignRuntimesGrid.ItemsSource = _campaignRuntimes;
        CampaignProgressGrid.ItemsSource = _campaignProgress;
        _campaignModels.CollectionChanged += (_, _) => UpdateCampaignPlanText();
        _campaignRuntimes.CollectionChanged += (_, _) => UpdateCampaignPlanText();
        CampaignRepeatsTextBox.TextChanged += (_, _) => UpdateCampaignPlanText();

        var discovered = AutoTunerDiscovery.Discover();
        if (discovered is not null)
        {
            AutoTunerUrlTextBox.Text = discovered.BaseUrl;
            AutoTunerStatusTextBlock.Text = discovered.HasToken
                ? $"AutoTuner-Zugang gefunden ({discovered.Source}{(discovered.Version is null ? string.Empty : ", v" + discovered.Version)}). Auf „Verbinden“ klicken."
                : $"AutoTuner-URL gefunden ({discovered.Source}), aber kein Token. Token eintragen oder Sidecar-Datei prüfen.";
        }
        else
        {
            AutoTunerUrlTextBox.Text = "http://127.0.0.1:1233";
        }
    }

    private async void ConnectAutoTunerButton_Click(object sender, RoutedEventArgs e)
    {
        EnsureCampaignUi();
        var connection = AutoTunerDiscovery.Discover(
            string.IsNullOrWhiteSpace(AutoTunerUrlTextBox.Text) ? null : AutoTunerUrlTextBox.Text.Trim(),
            string.IsNullOrWhiteSpace(AutoTunerTokenBox.Password) ? null : AutoTunerTokenBox.Password.Trim());
        if (connection is null)
        {
            AutoTunerStatusTextBlock.Text = "Keine AutoTuner-URL. Bitte URL eintragen oder die External control API im AutoTuner aktivieren.";
            return;
        }

        ConnectAutoTunerButton.IsEnabled = false;
        AutoTunerStatusTextBlock.Text = $"Verbinde mit {connection.BaseUrl} ...";
        try
        {
            using var client = new AutoTunerClient(connection, timeout: TimeSpan.FromSeconds(60));
            var health = await client.GetHealthAsync();
            var models = await client.GetModelsAsync();
            var runtimes = await client.GetRuntimesAsync();
            AutoTunerStatus? status = null;
            try
            {
                status = await client.GetStatusAsync();
            }
            catch (AutoTunerApiException)
            {
                // Status is informational only.
            }

            var previouslySelectedModels = _campaignModels.Where(m => m.Selected).Select(m => m.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var previouslySelectedRuntimes = _campaignRuntimes.Where(r => r.Selected).Select(r => r.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            _campaignModels.Clear();
            foreach (var model in models.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase))
            {
                var row = new CampaignModelRow(model) { Selected = previouslySelectedModels.Contains(model.Id) };
                row.PropertyChanged += (_, _) => UpdateCampaignPlanText();
                _campaignModels.Add(row);
            }

            _campaignRuntimes.Clear();
            foreach (var runtime in runtimes)
            {
                var row = new CampaignRuntimeRow(runtime) { Selected = previouslySelectedRuntimes.Contains(runtime.Id) };
                row.PropertyChanged += (_, _) => UpdateCampaignPlanText();
                _campaignRuntimes.Add(row);
            }

            _autoTunerConnection = connection;
            var runtimeNote = runtimes.Count == 0 ? " Keine Build-Liste (AutoTuner < 5.3.9?): Kampagne nutzt den im AutoTuner gewählten Build." : $" {runtimes.Count} Build(s).";
            AutoTunerStatusTextBlock.Text = $"Verbunden: AutoTuner {health.Version} ({connection.Source}), {models.Count} Modell(e).{runtimeNote}{(status is null ? string.Empty : $" Status: {status.Status}{(status.ActiveModel is null ? string.Empty : ", aktiv " + status.ActiveModel)}.")}";
            AppendLog($"AutoTuner {health.Version} verbunden ({connection.BaseUrl}, Quelle {connection.Source}): {models.Count} Modelle, {runtimes.Count} Builds.");
            StartCampaignButton.IsEnabled = _campaignModels.Count > 0 && _campaignRunner is null;
            UpdateCampaignPlanText();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Includes JSON contract mismatches: an async void handler must never let an
            // exception escape, otherwise the whole app would terminate.
            var hint = ex is AutoTunerApiException { Status: 401 }
                ? " Token fehlt oder ist falsch: im AutoTuner unter Settings → External control API kopieren und hier eintragen."
                : string.Empty;
            AutoTunerStatusTextBlock.Text = "Verbindung fehlgeschlagen: " + ex.Message + hint;
            AppendLog("FEHLER AutoTuner: " + ex);
        }
        finally
        {
            ConnectAutoTunerButton.IsEnabled = true;
        }
    }

    private void UpdateCampaignPlanText()
    {
        if (!_campaignUiInitialized)
        {
            return;
        }

        var models = _campaignModels.Count(m => m.Selected);
        var runtimes = _campaignRuntimes.Count(r => r.Selected);
        var repeats = int.TryParse(CampaignRepeatsTextBox.Text, out var parsed) && parsed > 0 ? parsed : 1;
        var perModel = Math.Max(1, runtimes);
        CampaignPlanTextBlock.Text = models == 0
            ? "Keine Modelle angehakt. Modelle (und optional Builds) anhaken, dann „Kampagne starten“. Keine Builds angehakt = AutoTuner-Standardbuild je Modell."
            : $"Plan: {models} Modell(e) × {(runtimes == 0 ? "Standardbuild" : runtimes + " Build(s)")} × {repeats} Run(s) = {models * perModel * repeats} Benchmark-Run(s) (je Run 1 + Run 2 + Run 3, einzeln archiviert).";
    }

    private async void StartCampaignButton_Click(object sender, RoutedEventArgs e)
    {
        EnsureCampaignUi();
        if (_autoTunerConnection is null)
        {
            MessageBox.Show(this, "Bitte zuerst mit dem AutoTuner verbinden.", "Kampagne", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var selectedModels = _campaignModels.Where(m => m.Selected).ToList();
        if (selectedModels.Count == 0)
        {
            MessageBox.Show(this, "Bitte mindestens ein Modell anhaken.", "Kampagne", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var unrunnable = selectedModels.Where(m => !m.Runnable).ToList();
        if (unrunnable.Count > 0)
        {
            MessageBox.Show(this, "Nicht als Server lauffähig: " + string.Join(", ", unrunnable.Select(m => m.Name)), "Kampagne", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        int repeats;
        BenchmarkOptions baseOptions;
        try
        {
            repeats = ParseInt(CampaignRepeatsTextBox.Text, "Runs pro Modell × Build", 1, min: 1);
            baseOptions = BuildOptions("<campaign>").With(model: string.Empty);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Ungültige Optionen", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var selectedRuntimes = _campaignRuntimes.Where(r => r.Selected).ToList();
        var items = new List<CampaignItem>();
        foreach (var model in selectedModels)
        {
            if (selectedRuntimes.Count == 0)
            {
                items.Add(new CampaignItem { ModelId = model.Id, ModelName = model.Name, Repeats = repeats, QuantOverride = baseOptions.QuantOverride });
                continue;
            }

            foreach (var runtime in selectedRuntimes)
            {
                items.Add(new CampaignItem { ModelId = model.Id, ModelName = model.Name, RuntimeId = runtime.Id, RuntimeLabel = runtime.Label, Repeats = repeats, QuantOverride = baseOptions.QuantOverride });
            }
        }

        var plan = new CampaignPlan
        {
            Items = items,
            BaseOptions = baseOptions,
            AutoTuner = _autoTunerConnection,
            StopOnError = CampaignStopOnErrorCheckBox.IsChecked == true,
            StopServerAtEnd = CampaignUnloadCheckBox.IsChecked == true,
            SeedStart = baseOptions.Seed
        };

        _campaignProgress.Clear();
        for (var i = 0; i < items.Count; i++)
        {
            _campaignProgress.Add(new CampaignProgressRow { Index = i + 1, Label = items[i].Label, State = "Wartet", RepeatsDisplay = $"0/{items[i].Repeats}" });
        }

        var runner = new CampaignRunner();
        _campaignRunner = runner;
        _benchmarkCancellation = new CancellationTokenSource();
        SetBusy(true, benchmarkRunning: true);
        SetCampaignControls(running: true);
        var streamProgress = new Progress<ChatStreamDelta>(OnStreamDelta);
        ResetResultUi(clearLog: true);
        AppendLog($"Kampagne gestartet: {items.Count} Modell/Build-Einträge × {repeats} Run(s) über AutoTuner {_autoTunerConnection.BaseUrl}.");
        AppendLog($"Request-Settings: max_tokens={baseOptions.MaxTokens}, response_format={!baseOptions.SkipResponseFormat}, disable_thinking={baseOptions.DisableThinking}, truth_audit={baseOptions.WithTruthAudit}, timeout={baseOptions.Timeout.TotalSeconds:0}s. Modell-Tuning (Kontext, Sampler, GPU) kommt aus dem AutoTuner.");
        StatusTextBlock.Text = "Kampagne läuft: AutoTuner lädt das erste Modell...";

        try
        {
            var summary = await runner.RunAsync(
                plan,
                Progress,
                onItemChanged: state => Dispatcher.Invoke(() => UpdateCampaignRow(state, items.IndexOf(state.Item))),
                onRunCompleted: OnRunCompleted,
                onResultCompleted: result => Dispatcher.Invoke(() =>
                {
                    _lastResult = result;
                    ApplyComparisonAndPaths(result);
                }),
                streamProgress: streamProgress,
                cancellationToken: _benchmarkCancellation.Token,
                onRunStarting: (state, repeat) => Dispatcher.Invoke(() =>
                {
                    ResetResultUi(clearLog: false);
                    StatusTextBlock.Text = $"Kampagne: {state.Item.Label} — Run {repeat}/{Math.Max(1, state.Item.Repeats)} (Run 1 + 2 + 3)...";
                    BeginLiveRun(Run1RawPanel, $"Run 1 — {state.Item.Label}", runNumber: 1);
                }),
                manualAbortTokenFactory: () =>
                {
                    return Dispatcher.Invoke(() =>
                    {
                        DisposeManualStopTokens();
                        _run1ManualStop = new CancellationTokenSource();
                        _run2ManualStop = new CancellationTokenSource();
                        _run3ManualStop = new CancellationTokenSource();
                        return (_run1ManualStop.Token, _run2ManualStop.Token, _run3ManualStop.Token);
                    });
                });

            var completed = summary.Items.Count(i => i.State == "Completed");
            var failed = summary.Items.Count(i => i.State == "Failed");
            var skipped = summary.Items.Count(i => i.State == "Skipped");
            StatusTextBlock.Text = $"Kampagne beendet ({summary.StopMode}): {completed} abgeschlossen, {failed} fehlgeschlagen, {skipped} übersprungen.";
            AppendLog(StatusTextBlock.Text);
        }
        catch (OperationCanceledException)
        {
            StatusTextBlock.Text = "Kampagne abgebrochen.";
            AppendLog("Kampagne abgebrochen.");
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = "Kampagne fehlgeschlagen.";
            AppendLog("FEHLER Kampagne: " + ex);
            MessageBox.Show(this, "Kampagne fehlgeschlagen:\n\n" + ex.Message, "Fehler", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _campaignRunner = null;
            _benchmarkCancellation?.Dispose();
            _benchmarkCancellation = null;
            DisposeManualStopTokens();
            _activeRunNumber = 0;
            SetCampaignControls(running: false);
            SetBusy(false, benchmarkRunning: false);
            StartCampaignButton.IsEnabled = _campaignModels.Count > 0;
        }
    }

    private void UpdateCampaignRow(CampaignItemProgress state, int index)
    {
        if (index < 0 || index >= _campaignProgress.Count)
        {
            return;
        }

        var row = _campaignProgress[index];
        row.State = state.State switch
        {
            CampaignItemState.Pending => "Wartet",
            CampaignItemState.Loading => "Lädt (AutoTuner)",
            CampaignItemState.Running => "Läuft",
            CampaignItemState.Completed => "Fertig",
            CampaignItemState.Failed => "Fehler",
            CampaignItemState.Skipped => "Übersprungen",
            _ => state.State.ToString()
        };
        row.RepeatsDisplay = $"{state.CompletedRepeats}/{Math.Max(1, state.Item.Repeats)}";
        row.Backend = state.Runtime is null ? string.Empty : RuntimeKeys.DisplayBackend(state.Runtime.Backend);
        row.Build = state.Runtime?.EngineVersion is { } build ? RuntimeKeys.ShortBuild(build) : string.Empty;
        row.BestDisplay = state.BestScore.HasValue ? state.BestScore.Value.ToString("0.##") : "—";
        row.MeanDisplay = state.MeanScore.HasValue ? state.MeanScore.Value.ToString("0.##") : "—";
        row.Message = state.Message;
    }

    private void SetCampaignControls(bool running)
    {
        StartCampaignButton.IsEnabled = !running && _campaignModels.Count > 0;
        ConnectAutoTunerButton.IsEnabled = !running;
        CampaignStopAfterRunButton.IsEnabled = running;
        CampaignStopAfterModelButton.IsEnabled = running;
        CampaignAbortButton.IsEnabled = running;
        CampaignRepeatsTextBox.IsReadOnly = running;
        CampaignModelsGrid.IsEnabled = !running;
        CampaignRuntimesGrid.IsEnabled = !running;
        SoftStopButton.IsEnabled = running;
    }

    /// <summary>Double-clicking a row toggles its checkbox; the checkbox itself toggles on a single click.</summary>
    private void CampaignGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not DataGrid grid || _campaignRunner is not null)
        {
            return;
        }

        switch (grid.SelectedItem)
        {
            case CampaignModelRow model when model.Runnable:
                model.Selected = !model.Selected;
                break;
            case CampaignRuntimeRow runtime when runtime.Available:
                runtime.Selected = !runtime.Selected;
                break;
        }
    }

    private void ToggleAllModelsButton_Click(object sender, RoutedEventArgs e)
    {
        var runnable = _campaignModels.Where(m => m.Runnable).ToList();
        var target = !runnable.All(m => m.Selected);
        foreach (var row in runnable)
        {
            row.Selected = target;
        }
    }

    private void ToggleAllRuntimesButton_Click(object sender, RoutedEventArgs e)
    {
        var available = _campaignRuntimes.Where(r => r.Available).ToList();
        var target = !available.All(r => r.Selected);
        foreach (var row in available)
        {
            row.Selected = target;
        }
    }

    private void CampaignStopAfterRunButton_Click(object sender, RoutedEventArgs e)
    {
        if (_campaignRunner is null)
        {
            return;
        }

        _campaignRunner.RequestStop(CampaignStopMode.AfterCurrentRun);
        CampaignStopAfterRunButton.IsEnabled = false;
        CampaignStopAfterModelButton.IsEnabled = false;
        AppendLog("Kampagne: Stopp nach dem aktuellen Run angefordert.");
        StatusTextBlock.Text = "Kampagne stoppt nach dem aktuellen Run.";
    }

    private void CampaignStopAfterModelButton_Click(object sender, RoutedEventArgs e)
    {
        if (_campaignRunner is null)
        {
            return;
        }

        _campaignRunner.RequestStop(CampaignStopMode.AfterCurrentItem);
        CampaignStopAfterModelButton.IsEnabled = false;
        AppendLog("Kampagne: Stopp nach dem aktuellen Modell/Build angefordert.");
        StatusTextBlock.Text = "Kampagne stoppt nach dem aktuellen Modell.";
    }

    private void CampaignAbortButton_Click(object sender, RoutedEventArgs e)
    {
        if (_campaignRunner is null)
        {
            return;
        }

        _campaignRunner.RequestStop(CampaignStopMode.Immediately);
        CampaignAbortButton.IsEnabled = false;
        CampaignStopAfterRunButton.IsEnabled = false;
        CampaignStopAfterModelButton.IsEnabled = false;
        AppendLog("Kampagne: sofortiger Abbruch angefordert.");
        StatusTextBlock.Text = "Kampagne wird abgebrochen...";
    }

    public sealed class CampaignModelRow : INotifyPropertyChanged
    {
        private bool _selected;

        public CampaignModelRow(AutoTunerModel model)
        {
            Id = model.Id;
            Name = model.DisplayName;
            Quant = model.Quant ?? ModelIdentity.Parse(model.Name).Quant;
            ParamsDisplay = model.ParamsB is > 0 ? $"{model.ParamsB:0.#}B" : string.Empty;
            ContextWindow = model.ContextWindow;
            DefaultRuntimeId = model.DefaultRuntimeId ?? string.Empty;
            Runnable = model.Runnable;
            Note = model.Runnable ? (model.Reasoning ? "reasoning" : string.Empty) : model.UnavailableReason;
        }

        public string Id { get; }
        public string Name { get; }
        public string Quant { get; }
        public string ParamsDisplay { get; }
        public int ContextWindow { get; }
        public string DefaultRuntimeId { get; }
        public bool Runnable { get; }
        public string Note { get; }

        public bool Selected
        {
            get => _selected;
            set { _selected = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public sealed class CampaignRuntimeRow : INotifyPropertyChanged
    {
        private bool _selected;

        public CampaignRuntimeRow(AutoTunerRuntime runtime)
        {
            Id = runtime.Id;
            Label = runtime.DisplayLabel;
            Backend = RuntimeKeys.DisplayBackend(runtime.Backend);
            Build = runtime.Build ?? (runtime.BuildInfo is null ? string.Empty : RuntimeKeys.ShortBuild(runtime.BuildInfo));
            DefaultDisplay = runtime.IsDefault ? "ja" : string.Empty;
            Available = runtime.Available;
            Note = runtime.Available ? string.Empty : runtime.UnavailableReason;
        }

        public string Id { get; }
        public string Label { get; }
        public string Backend { get; }
        public string Build { get; }
        public string DefaultDisplay { get; }
        public bool Available { get; }
        public string Note { get; }

        public bool Selected
        {
            get => _selected;
            set { _selected = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public sealed class CampaignProgressRow : INotifyPropertyChanged
    {
        private string _state = string.Empty;
        private string _repeatsDisplay = string.Empty;
        private string _backend = string.Empty;
        private string _build = string.Empty;
        private string _bestDisplay = "—";
        private string _meanDisplay = "—";
        private string _message = string.Empty;

        public int Index { get; init; }
        public string Label { get; init; } = string.Empty;
        public string State { get => _state; set { _state = value; OnPropertyChanged(); } }
        public string RepeatsDisplay { get => _repeatsDisplay; set { _repeatsDisplay = value; OnPropertyChanged(); } }
        public string Backend { get => _backend; set { _backend = value; OnPropertyChanged(); } }
        public string Build { get => _build; set { _build = value; OnPropertyChanged(); } }
        public string BestDisplay { get => _bestDisplay; set { _bestDisplay = value; OnPropertyChanged(); } }
        public string MeanDisplay { get => _meanDisplay; set { _meanDisplay = value; OnPropertyChanged(); } }
        public string Message { get => _message; set { _message = value; OnPropertyChanged(); } }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
