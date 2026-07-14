using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using SuperCalcBenchmark.Core;

namespace SuperCalcBenchmark.App;

public partial class MainWindow : Window
{
    private readonly string _repositoryRoot;
    private CancellationTokenSource? _benchmarkCancellation;
    private CancellationTokenSource? _run1ManualStop;
    private CancellationTokenSource? _run2ManualStop;
    private CancellationTokenSource? _run3ManualStop;
    private int _activeRunNumber;
    private bool _stopAfterCurrentPassRequested;
    private BenchmarkRunResult? _lastResult;
    private BenchmarkTheme _currentTheme = BenchmarkTheme.Light;

    private enum BenchmarkTheme
    {
        Light,
        Dark
    }

    private const string WindowBackgroundBrushKey = "WindowBackgroundBrush";
    private const string SurfaceBrushKey = "SurfaceBrush";
    private const string SurfaceRaisedBrushKey = "SurfaceRaisedBrush";
    private const string ControlBackgroundBrushKey = "ControlBackgroundBrush";
    private const string ControlDisabledBackgroundBrushKey = "ControlDisabledBackgroundBrush";
    private const string ButtonBackgroundBrushKey = "ButtonBackgroundBrush";
    private const string ButtonHoverBackgroundBrushKey = "ButtonHoverBackgroundBrush";
    private const string BorderBrushKey = "BorderBrush";
    private const string TextPrimaryBrushKey = "TextPrimaryBrush";
    private const string TextSecondaryBrushKey = "TextSecondaryBrush";
    private const string TextDisabledBrushKey = "TextDisabledBrush";
    private const string AccentBrushKey = "AccentBrush";
    private const string AccentForegroundBrushKey = "AccentForegroundBrush";
    private const string SelectedBackgroundBrushKey = "SelectedBackgroundBrush";
    private const string SelectedForegroundBrushKey = "SelectedForegroundBrush";
    private const string DataGridHeaderBackgroundBrushKey = "DataGridHeaderBackgroundBrush";
    private const string DataGridAlternatingRowBrushKey = "DataGridAlternatingRowBrush";
    private const string InfoTextBrushKey = "InfoTextBrush";
    private const string InfoBackgroundBrushKey = "InfoBackgroundBrush";
    private const string WarningTextBrushKey = "WarningTextBrush";
    private const string WarningBackgroundBrushKey = "WarningBackgroundBrush";
    private const string DangerTextBrushKey = "DangerTextBrush";
    private const string DangerBackgroundBrushKey = "DangerBackgroundBrush";
    private const string CodeBackgroundBrushKey = "CodeBackgroundBrush";
    private const string RequestTextBrushKey = "RequestTextBrush";
    private const string RequestBackgroundBrushKey = "RequestBackgroundBrush";
    private const string OutputTextBrushKey = "OutputTextBrush";
    private const string OutputBackgroundBrushKey = "OutputBackgroundBrush";
    private const string ReasoningTextBrushKey = "ReasoningTextBrush";
    private const string ReasoningBackgroundBrushKey = "ReasoningBackgroundBrush";

    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaUseImmersiveDarkModeBefore20H1 = 19;
    private const int DwmwaCaptionColor = 35;
    private const int DwmwaTextColor = 36;

    private static readonly string[] ProtectedBenchmarkDataDirectories = ["archive", "artifacts", "results"];

    // Live-streaming UI state. Tokens arrive via IProgress and are appended to these
    // text boxes in real time. _activeLivePanel points at whichever run is currently
    // streaming (Run 1, then Run 2, then the automatic Run 3 truth audit).
    private StackPanel? _activeLivePanel;
    private TextBox? _liveReasoningBox;
    private TextBox? _liveContentBox;
    private TextBlock? _liveStatusBlock;
    private int _liveReasoningChars;
    private int _liveContentChars;

    public MainWindow()
    {
        InitializeComponent();
        RestoreWindowPlacement();
        SourceInitialized += MainWindow_SourceInitialized;
        ApplyTheme(BenchmarkTheme.Light);
        _repositoryRoot = FindRepositoryRoot();
        DotNetInfoTextBlock.Text = $"v{ReleaseUpdater.GetCurrentVersion()} | .NET {Environment.Version.Major} | {_repositoryRoot}";
        ShowRawOutputPlaceholder(Run1RawPanel, "Noch kein Run-1-Output. Nach dem Benchmark siehst du hier Prompt, Thinking, Output und Raw API Response.");
        ShowRawOutputPlaceholder(Run2RawPanel, "Noch kein Run-2-Output. Nach dem Benchmark siehst du hier Prompt, Thinking, Output und Raw API Response.");
        ShowRawOutputPlaceholder(Run3RawPanel, "Noch kein Run-3-Output. Run 3 läuft automatisch nach Run 2 als Truth-Audit / Ehrlichkeitstest.");
        AppendLog("Bereit. Wenn du ein neues Modell in llama-server geladen hast: Refresh Models klicken, Modell wählen, Benchmark starten.");
        InitializeComparisonPlaceholder();
        Loaded += MainWindow_Loaded;
    }

    private void ThemeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox comboBox)
        {
            ApplyTheme(comboBox.SelectedIndex == 1 ? BenchmarkTheme.Dark : BenchmarkTheme.Light);
        }
    }

    private void MainWindow_SourceInitialized(object? sender, EventArgs e)
    {
        ApplyWindowChromeTheme(_currentTheme);
    }

    private void ApplyTheme(BenchmarkTheme theme)
    {
        _currentTheme = theme;
        var resources = Application.Current.Resources;

        if (theme == BenchmarkTheme.Dark)
        {
            SetBrush(resources, WindowBackgroundBrushKey, "#0F172A");
            SetBrush(resources, SurfaceBrushKey, "#111827");
            SetBrush(resources, SurfaceRaisedBrushKey, "#1F2937");
            SetBrush(resources, ControlBackgroundBrushKey, "#0B1220");
            SetBrush(resources, ControlDisabledBackgroundBrushKey, "#1E293B");
            SetBrush(resources, ButtonBackgroundBrushKey, "#1F2937");
            SetBrush(resources, ButtonHoverBackgroundBrushKey, "#334155");
            SetBrush(resources, BorderBrushKey, "#475569");
            SetBrush(resources, TextPrimaryBrushKey, "#E5E7EB");
            SetBrush(resources, TextSecondaryBrushKey, "#AAB4C2");
            SetBrush(resources, TextDisabledBrushKey, "#94A3B8");
            SetBrush(resources, AccentBrushKey, "#60A5FA");
            SetBrush(resources, AccentForegroundBrushKey, "#06101F");
            SetBrush(resources, SelectedBackgroundBrushKey, "#1D4ED8");
            SetBrush(resources, SelectedForegroundBrushKey, "#F8FAFC");
            SetBrush(resources, DataGridHeaderBackgroundBrushKey, "#1E293B");
            SetBrush(resources, DataGridAlternatingRowBrushKey, "#172033");
            SetBrush(resources, InfoTextBrushKey, "#93C5FD");
            SetBrush(resources, InfoBackgroundBrushKey, "#0F2746");
            SetBrush(resources, WarningTextBrushKey, "#FBBF24");
            SetBrush(resources, WarningBackgroundBrushKey, "#3A2A0B");
            SetBrush(resources, DangerTextBrushKey, "#FCA5A5");
            SetBrush(resources, DangerBackgroundBrushKey, "#3A1017");
            SetBrush(resources, CodeBackgroundBrushKey, "#0B1220");
            SetBrush(resources, RequestTextBrushKey, "#C4B5FD");
            SetBrush(resources, RequestBackgroundBrushKey, "#151A33");
            SetBrush(resources, OutputTextBrushKey, "#FDA4AF");
            SetBrush(resources, OutputBackgroundBrushKey, "#311217");
            SetBrush(resources, ReasoningTextBrushKey, "#CBD5E1");
            SetBrush(resources, ReasoningBackgroundBrushKey, "#151B27");
            ApplySystemBrushes(resources, theme);
            ApplyWindowChromeTheme(theme);
            return;
        }

        SetBrush(resources, WindowBackgroundBrushKey, "#F5F7FA");
        SetBrush(resources, SurfaceBrushKey, "#FFFFFF");
        SetBrush(resources, SurfaceRaisedBrushKey, "#FAFBFC");
        SetBrush(resources, ControlBackgroundBrushKey, "#FFFFFF");
        SetBrush(resources, ControlDisabledBackgroundBrushKey, "#EEF1F5");
        SetBrush(resources, ButtonBackgroundBrushKey, "#F3F4F6");
        SetBrush(resources, ButtonHoverBackgroundBrushKey, "#E5E7EB");
        SetBrush(resources, BorderBrushKey, "#D0D7DE");
        SetBrush(resources, TextPrimaryBrushKey, "#1F2937");
        SetBrush(resources, TextSecondaryBrushKey, "#5B6472");
        SetBrush(resources, TextDisabledBrushKey, "#4B5563");
        SetBrush(resources, AccentBrushKey, "#2563EB");
        SetBrush(resources, AccentForegroundBrushKey, "#FFFFFF");
        SetBrush(resources, SelectedBackgroundBrushKey, "#DBEAFE");
        SetBrush(resources, SelectedForegroundBrushKey, "#1E3A8A");
        SetBrush(resources, DataGridHeaderBackgroundBrushKey, "#F3F4F6");
        SetBrush(resources, DataGridAlternatingRowBrushKey, "#F8FAFC");
        SetBrush(resources, InfoTextBrushKey, "#1D4ED8");
        SetBrush(resources, InfoBackgroundBrushKey, "#EFF6FF");
        SetBrush(resources, WarningTextBrushKey, "#B45309");
        SetBrush(resources, WarningBackgroundBrushKey, "#FFF7ED");
        SetBrush(resources, DangerTextBrushKey, "#B91C1C");
        SetBrush(resources, DangerBackgroundBrushKey, "#FEF2F2");
        SetBrush(resources, CodeBackgroundBrushKey, "#FFFFFF");
        SetBrush(resources, RequestTextBrushKey, "#4338CA");
        SetBrush(resources, RequestBackgroundBrushKey, "#F6F8FF");
        SetBrush(resources, OutputTextBrushKey, "#B91C1C");
        SetBrush(resources, OutputBackgroundBrushKey, "#FFF6F6");
        SetBrush(resources, ReasoningTextBrushKey, "#4B5563");
        SetBrush(resources, ReasoningBackgroundBrushKey, "#F8F8F8");
        ApplySystemBrushes(resources, theme);
        ApplyWindowChromeTheme(theme);
    }

    private void ApplyWindowChromeTheme(BenchmarkTheme theme)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        try
        {
            var isDark = theme == BenchmarkTheme.Dark ? 1 : 0;
            _ = DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref isDark, Marshal.SizeOf<int>());
            _ = DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkModeBefore20H1, ref isDark, Marshal.SizeOf<int>());

            var captionColor = ColorRef(theme == BenchmarkTheme.Dark ? "#0F172A" : "#F5F7FA");
            var titleTextColor = ColorRef(theme == BenchmarkTheme.Dark ? "#F8FAFC" : "#111827");
            _ = DwmSetWindowAttribute(handle, DwmwaCaptionColor, ref captionColor, Marshal.SizeOf<int>());
            _ = DwmSetWindowAttribute(handle, DwmwaTextColor, ref titleTextColor, Marshal.SizeOf<int>());
        }
        catch (DllNotFoundException)
        {
            // Wine or stripped-down Windows environments may not expose dwmapi.dll.
        }
        catch (EntryPointNotFoundException)
        {
            // Older Wine/Windows builds can lack newer DWM attributes.  The app still works.
        }
    }

    private static int ColorRef(string hex)
    {
        var color = (Color)ColorConverter.ConvertFromString(hex)!;
        return color.R | (color.G << 8) | (color.B << 16);
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int attributeValue, int attributeSize);

    private static void ApplySystemBrushes(ResourceDictionary resources, BenchmarkTheme theme)
    {
        if (theme == BenchmarkTheme.Dark)
        {
            // Several built-in WPF control templates (ComboBox, TabItem, disabled Button,
            // ScrollBar) use SystemColors instead of the control's Background setter.
            // Override them too, otherwise dark mode gets light-gray controls with pale text.
            SetBrush(resources, SystemColors.ControlBrushKey, "#1F2937");
            SetBrush(resources, SystemColors.ControlTextBrushKey, "#E5E7EB");
            SetBrush(resources, SystemColors.ControlDarkBrushKey, "#475569");
            SetBrush(resources, SystemColors.ControlDarkDarkBrushKey, "#64748B");
            SetBrush(resources, SystemColors.ControlLightBrushKey, "#334155");
            SetBrush(resources, SystemColors.ControlLightLightBrushKey, "#0B1220");
            SetBrush(resources, SystemColors.WindowBrushKey, "#0B1220");
            SetBrush(resources, SystemColors.WindowTextBrushKey, "#E5E7EB");
            SetBrush(resources, SystemColors.GrayTextBrushKey, "#94A3B8");
            SetBrush(resources, SystemColors.HighlightBrushKey, "#1D4ED8");
            SetBrush(resources, SystemColors.HighlightTextBrushKey, "#F8FAFC");
            SetBrush(resources, SystemColors.InactiveBorderBrushKey, "#475569");
            SetBrush(resources, SystemColors.ActiveBorderBrushKey, "#64748B");
            SetBrush(resources, SystemColors.WindowFrameBrushKey, "#475569");
            SetBrush(resources, SystemColors.MenuBrushKey, "#111827");
            SetBrush(resources, SystemColors.MenuTextBrushKey, "#E5E7EB");
            SetBrush(resources, SystemColors.MenuHighlightBrushKey, "#1D4ED8");
            SetBrush(resources, SystemColors.MenuBarBrushKey, "#111827");
            SetBrush(resources, SystemColors.ScrollBarBrushKey, "#1E293B");
            SetBrush(resources, SystemColors.HotTrackBrushKey, "#93C5FD");
            SetBrush(resources, SystemColors.InfoBrushKey, "#0F2746");
            SetBrush(resources, SystemColors.InfoTextBrushKey, "#E5E7EB");
            return;
        }

        SetBrush(resources, SystemColors.ControlBrushKey, "#F3F4F6");
        SetBrush(resources, SystemColors.ControlTextBrushKey, "#1F2937");
        SetBrush(resources, SystemColors.ControlDarkBrushKey, "#D0D7DE");
        SetBrush(resources, SystemColors.ControlDarkDarkBrushKey, "#9CA3AF");
        SetBrush(resources, SystemColors.ControlLightBrushKey, "#E5E7EB");
        SetBrush(resources, SystemColors.ControlLightLightBrushKey, "#FFFFFF");
        SetBrush(resources, SystemColors.WindowBrushKey, "#FFFFFF");
        SetBrush(resources, SystemColors.WindowTextBrushKey, "#1F2937");
        SetBrush(resources, SystemColors.GrayTextBrushKey, "#4B5563");
        SetBrush(resources, SystemColors.HighlightBrushKey, "#2563EB");
        SetBrush(resources, SystemColors.HighlightTextBrushKey, "#FFFFFF");
        SetBrush(resources, SystemColors.InactiveBorderBrushKey, "#D0D7DE");
        SetBrush(resources, SystemColors.ActiveBorderBrushKey, "#9CA3AF");
        SetBrush(resources, SystemColors.WindowFrameBrushKey, "#D0D7DE");
        SetBrush(resources, SystemColors.MenuBrushKey, "#FFFFFF");
        SetBrush(resources, SystemColors.MenuTextBrushKey, "#1F2937");
        SetBrush(resources, SystemColors.MenuHighlightBrushKey, "#DBEAFE");
        SetBrush(resources, SystemColors.MenuBarBrushKey, "#FFFFFF");
        SetBrush(resources, SystemColors.ScrollBarBrushKey, "#E5E7EB");
        SetBrush(resources, SystemColors.HotTrackBrushKey, "#1D4ED8");
        SetBrush(resources, SystemColors.InfoBrushKey, "#EFF6FF");
        SetBrush(resources, SystemColors.InfoTextBrushKey, "#1F2937");
    }

    private static void SetBrush(ResourceDictionary resources, object key, string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)!);
        brush.Freeze();
        resources[key] = brush;
    }

    private void InitializeComparisonPlaceholder()
    {
        FamilyFilterComboBox.ItemsSource = new List<string> { AllFamiliesLabel };
        FamilyFilterComboBox.SelectedItem = AllFamiliesLabel;
        ComparisonGrid.ItemsSource = Array.Empty<ComparisonGridRow>();
        OpenComparisonButton.IsEnabled = false;
        ComparisonStatusTextBlock.Text = "Archiv wird nach dem Start im Hintergrund geladen...";
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= MainWindow_Loaded;
        await RefreshComparisonAsync(preserveSelection: false);
    }

    private async void UpdateButton_Click(object sender, RoutedEventArgs e)
    {
        // Two update paths: a git checkout keeps using git pull --ff-only, the
        // standalone release EXE (no .git anywhere above) updates itself from the
        // latest GitHub release ZIP.
        if (!ReleaseUpdater.IsRunningFromGitCheckout(_repositoryRoot))
        {
            await RunReleaseUpdateAsync();
            return;
        }

        var confirmation = MessageBox.Show(this,
            "Das Update führt im Repository \"git pull --ff-only\" aus.\n\n" +
            "Vorher werden lokale Benchmarkdaten aus archive/, artifacts/ und results/ nach %LOCALAPPDATA%\\SuperCalcBenchmark\\UpdateBackups gesichert. " +
            "Es werden weder git reset noch git clean ausgeführt; vorhandene lokale Run-Ordner bleiben unangetastet.\n\n" +
            "Jetzt Updates ziehen?",
            "Update ziehen",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        await PullUpdatesAsync();
    }

    private async Task RunReleaseUpdateAsync()
    {
        SetBusy(true, benchmarkRunning: false);
        StatusTextBlock.Text = "Suche nach Updates...";
        var currentVersion = ReleaseUpdater.GetCurrentVersion();
        AppendLog($"Update-Prüfung (Standalone): installierte Version v{currentVersion}, frage GitHub-Releases ab...");

        try
        {
            var latest = await ReleaseUpdater.GetLatestReleaseAsync(CancellationToken.None);
            if (latest is null)
            {
                StatusTextBlock.Text = "Kein App-Release gefunden.";
                AppendLog("Kein App-Release mit win-x64-ZIP gefunden: " + ReleaseUpdater.ReleasesPageUrl);
                MessageBox.Show(this,
                    "Auf GitHub wurde kein App-Release mit einem win-x64-ZIP gefunden.\n\n" + ReleaseUpdater.ReleasesPageUrl,
                    "Update-Prüfung",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            AppendLog($"Neueste Release-Version: v{latest.Version} ({latest.TagName}, Asset {latest.AssetName}).");

            if (latest.Version <= currentVersion)
            {
                StatusTextBlock.Text = $"App ist aktuell (v{currentVersion}).";
                MessageBox.Show(this,
                    $"Du nutzt bereits die neueste Version (v{currentVersion}).",
                    "Kein Update nötig",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            var confirmation = MessageBox.Show(this,
                $"Update verfügbar: v{currentVersion} → v{latest.Version}\n\n" +
                $"Das Update ({ReleaseUpdater.FormatSize(latest.AssetSize)}) wird heruntergeladen. Danach startet die App neu. " +
                "Lokale Benchmarkdaten in archive/, artifacts/ und results/ neben der EXE werden nicht angetastet.\n\n" +
                "Jetzt herunterladen und installieren?",
                "Update verfügbar",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (confirmation != MessageBoxResult.Yes)
            {
                StatusTextBlock.Text = "Update abgebrochen.";
                return;
            }

            ReleaseUpdater.EnsureApplicationDirectoryWritable();

            StatusTextBlock.Text = $"Update auf v{latest.Version} wird heruntergeladen...";
            var progress = new Progress<string>(AppendLog);
            var staged = await ReleaseUpdater.DownloadAndStageAsync(latest, progress, CancellationToken.None);

            var restart = MessageBox.Show(this,
                $"Update v{latest.Version} ist bereit.\n\n" +
                "Die App wird jetzt beendet, die neuen Dateien werden eingespielt und die App startet automatisch neu.",
                "Update installieren",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Information);

            if (restart != MessageBoxResult.OK)
            {
                StatusTextBlock.Text = "Update heruntergeladen, Installation abgebrochen.";
                AppendLog("Installation abgebrochen. Vorbereitetes Update liegt unter: " + staged.PayloadDirectory);
                return;
            }

            AppendLog("Updater gestartet, App wird beendet...");
            ReleaseUpdater.LaunchUpdaterAndPrepareShutdown(staged);
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = "Update fehlgeschlagen.";
            AppendLog("FEHLER beim Release-Update: " + ex.Message);
            MessageBox.Show(this,
                "Das Update konnte nicht geladen werden. Du kannst das neueste Release auch manuell herunterladen:\n" +
                ReleaseUpdater.ReleasesPageUrl + "\n\n" + ex.Message,
                "Update fehlgeschlagen",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        finally
        {
            SetBusy(false, benchmarkRunning: false);
        }
    }

    private async Task PullUpdatesAsync()
    {
        SetBusy(true, benchmarkRunning: false);
        StatusTextBlock.Text = "Update läuft...";
        AppendLog("Update gestartet: lokale Benchmarkdaten schützen, dann git pull --ff-only.");

        try
        {
            var result = await Task.Run(RunSafeRepositoryUpdate);
            AppendUpdateResultLog(result);
            QueueComparisonRefresh(preserveSelection: true);

            StatusTextBlock.Text = result.HeadChanged
                ? "Update gezogen. Bitte App nach dem Build/Neustart erneut starten."
                : "Repository ist bereits aktuell.";

            MessageBox.Show(this,
                BuildUpdateSummary(result),
                "Update abgeschlossen",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = "Update fehlgeschlagen.";
            AppendLog("FEHLER beim Update: " + ex.Message);
            MessageBox.Show(this,
                "Update fehlgeschlagen. Es wurde kein reset/clean ausgeführt; lokale Benchmarkdaten bleiben erhalten.\n\n" + ex.Message,
                "Update fehlgeschlagen",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        finally
        {
            SetBusy(false, benchmarkRunning: false);
        }
    }

    private void AppendUpdateResultLog(SafeUpdateResult result)
    {
        AppendLog($"Update Git-Root: {result.RepositoryRoot}");
        AppendLog(result.HeadChanged
            ? $"Update gezogen: {result.HeadBefore} → {result.HeadAfter}"
            : $"Keine neuen Commits ({result.HeadAfter}).");

        if (result.Backup.RelativeFiles.Count > 0)
        {
            AppendLog($"Lokale Benchmarkdaten gesichert: {result.Backup.RelativeFiles.Count} Datei(en) → {result.Backup.BackupRoot}");
            AppendLog($"Lokale Benchmarkdaten nach Pull geprüft: {result.Restore.RestoredMissingFiles} wiederhergestellt, {result.Restore.UnchangedFiles} unverändert, {result.Restore.ConflictCopies.Count} Konfliktkopie(n).");
            foreach (var conflictCopy in result.Restore.ConflictCopies)
            {
                AppendLog("Lokale Daten-Konfliktkopie: " + conflictCopy);
            }
        }
        else
        {
            AppendLog("Keine lokalen/geänderten Benchmarkdaten in archive/, artifacts/ oder results/ gefunden.");
        }

        var gitOutput = string.Join(Environment.NewLine, result.PullOutput.Trim(), result.PullError.Trim()).Trim();
        foreach (var line in SplitLogLines(gitOutput))
        {
            AppendLog("git pull: " + line);
        }
    }

    private static string BuildUpdateSummary(SafeUpdateResult result)
    {
        var builder = new StringBuilder();
        builder.AppendLine(result.HeadChanged
            ? $"Update gezogen: {result.HeadBefore} → {result.HeadAfter}"
            : $"Keine neuen Commits ({result.HeadAfter}).");
        builder.AppendLine();

        if (result.Backup.RelativeFiles.Count == 0)
        {
            builder.AppendLine("Keine lokalen/geänderten Benchmarkdaten in archive/, artifacts/ oder results/ gefunden.");
        }
        else
        {
            builder.AppendLine($"Lokale Benchmarkdaten vorher gesichert: {result.Backup.RelativeFiles.Count} Datei(en).");
            builder.AppendLine(result.Backup.BackupRoot);
            builder.AppendLine($"Wiederhergestellt: {result.Restore.RestoredMissingFiles}; unverändert: {result.Restore.UnchangedFiles}; Konfliktkopien: {result.Restore.ConflictCopies.Count}.");

            if (result.Restore.ConflictCopies.Count > 0)
            {
                builder.AppendLine("Konfliktkopien wurden mit .local-update-* im jeweiligen Datenordner abgelegt.");
            }
        }

        builder.AppendLine();
        builder.AppendLine("Hinweis: Falls Quellcode aktualisiert wurde, die GUI schließen und setup.bat/start.vbs erneut ausführen.");
        return builder.ToString().TrimEnd();
    }

    private async void RefreshModelsButton_Click(object sender, RoutedEventArgs e)
    {
        await RefreshModelsAsync();
    }

    private async Task RefreshModelsAsync()
    {
        SetBusy(true, benchmarkRunning: false);
        QuantTextBox.Text = string.Empty;
        StatusTextBlock.Text = "Modelle werden geladen...";
        AppendLog("Quant-Eingabe zurückgesetzt; Refresh nutzt wieder Auto-Erkennung/leeres Feld.");
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

    private void ModelComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // SelectionChanged can fire while controls are still being constructed (the FamilyFilter
        // combo in particular sets IsSelected during InitializeComponent) or before models load.
        // Guard so we never touch a control that does not exist yet.
        if (QuantTextBox is null || ModelComboBox is null)
        {
            return;
        }

        var model = ModelComboBox.SelectedItem?.ToString();
        if (string.IsNullOrWhiteSpace(model))
        {
            return;
        }

        ResetQuantFieldForModel(model);
    }

    /// <summary>
    /// Resets the optional Quant field whenever the selected/refreshed model changes.
    /// If the model id or llama-server meta.ftype can resolve the quant automatically,
    /// the field deliberately stays empty so the archived run is tagged as auto/server
    /// detected instead of a manual override. We also intentionally do not pre-fill from
    /// older archive corrections: the same model id/family can be reloaded with a different
    /// quant, and a stale manual value would put the new run into the wrong bucket.
    /// </summary>
    private async void ResetQuantFieldForModel(string model)
    {
        QuantTextBox.Text = string.Empty;

        var identity = ModelIdentity.Parse(model);
        if (identity.QuantWasDetected)
        {
            return;
        }

        // Name-based detection failed: ask llama-server for the authoritative file type
        // (PR #25134). Short timeout so a cold/offline server never freezes model selection.
        var serverUrl = ServerUrlTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(serverUrl))
        {
            return;
        }

        try
        {
            using var client = new LlamaCppClient(TimeSpan.FromSeconds(5));
            var ftype = await client.GetModelFtypeAsync(serverUrl, model).ConfigureAwait(true);
            var serverQuant = ModelIdentity.NormalizeServerFtype(ftype);
            if (string.IsNullOrWhiteSpace(serverQuant))
            {
                return;
            }

            if (!string.Equals(ModelComboBox.SelectedItem?.ToString(), model, StringComparison.Ordinal))
            {
                return;
            }

            var manualText = QuantTextBox.Text.Trim();
            AppendLog($"Quant automatisch vom Server erkannt: {serverQuant}" +
                      (string.Equals(ftype!.Trim(), serverQuant, StringComparison.Ordinal) ? string.Empty : $" (ftype \"{ftype!.Trim()}\")") +
                      (string.IsNullOrWhiteSpace(manualText)
                          ? " — Eingabefeld bleibt frei, Korrektur möglich."
                          : $" — manuelle Eingabe \"{manualText}\" bleibt als Override stehen."));
        }
        catch
        {
            // Offline/unreachable server: keep the field empty and let the user enter a
            // one-off manual override if auto-detection is unavailable.
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
        int totalPasses;
        try
        {
            totalPasses = ParseInt(RepeatCountTextBox.Text, "Durchläufe", 1, min: 1);
            options = BuildOptions(model);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Ungültige Optionen", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _benchmarkCancellation = new CancellationTokenSource();
        _stopAfterCurrentPassRequested = false;
        SetBusy(true, benchmarkRunning: true);
        SoftStopButton.IsEnabled = totalPasses > 1;
        var completedPasses = 0;
        var softStopped = false;

        try
        {
            for (var pass = 1; pass <= totalPasses; pass++)
            {
                _benchmarkCancellation.Token.ThrowIfCancellationRequested();

                // Countdown im (während des Laufs ausgegrauten) Durchläufe-Feld:
                // zeigt, wie viele Durchläufe inklusive des aktuellen noch anstehen.
                RepeatCountTextBox.Text = (totalPasses - pass + 1).ToString();

                // Manual per-run stop tokens are per pass: stopping Run 1 in pass 3
                // must not affect Run 1 of pass 4.
                _run1ManualStop = new CancellationTokenSource();
                _run2ManualStop = new CancellationTokenSource();
                _run3ManualStop = new CancellationTokenSource();

                ResetResultUi(clearLog: pass == 1);
                var passLabel = totalPasses > 1 ? $" — Durchlauf {pass}/{totalPasses}" : string.Empty;
                StatusTextBlock.Text = $"Benchmark läuft{passLabel}... Run 1 + Run 2 + Run 3 Truth-Audit können je nach Modell einige Minuten dauern.";
                AppendLog($"Benchmark startet für Modell: {model}{passLabel}");
                if (pass == 1)
                {
                    AppendLog($"Repository: {_repositoryRoot}");
                    AppendLog($"Request-Settings: max_tokens={options.MaxTokens}, response_format={!options.SkipResponseFormat}, disable_thinking={options.DisableThinking}, truth_audit={options.WithTruthAudit}, loop_abort={options.AbortOnLoop}, timeout={options.Timeout.TotalSeconds:0}s");
                    if (totalPasses > 1)
                    {
                        AppendLog($"Geplante Durchläufe: {totalPasses} komplette Benchmarks hintereinander; jeder Durchlauf wird einzeln archiviert.");
                    }
                }

                var runner = new BenchmarkRunner();

                // IProgress created on the UI thread → Report() callbacks are marshalled
                // back to the UI thread automatically, so we can touch controls directly.
                var streamProgress = new Progress<ChatStreamDelta>(OnStreamDelta);

                // Prepare the live view for Run 1 before the first token arrives.
                BeginLiveRun(Run1RawPanel, "Run 1 — Blind Analysis", runNumber: 1);

                var result = await runner.RunAsync(
                    options,
                    Progress,
                    _benchmarkCancellation.Token,
                    onRunCompleted: OnRunCompleted,
                    streamProgress: streamProgress,
                    run1ManualAbortToken: _run1ManualStop.Token,
                    run2ManualAbortToken: _run2ManualStop.Token,
                    run3ManualAbortToken: _run3ManualStop.Token);

                _lastResult = result;

                // Final pass: comparison panel + open buttons (per-run UI already rendered).
                ApplyComparisonAndPaths(result);
                completedPasses = pass;
                DisposeManualStopTokens();
                AppendLog(totalPasses > 1
                    ? $"Durchlauf {pass}/{totalPasses} abgeschlossen."
                    : "Benchmark abgeschlossen.");

                if (_stopAfterCurrentPassRequested && pass < totalPasses)
                {
                    softStopped = true;
                    AppendLog($"Soft-Stopp: Durchlauf {pass}/{totalPasses} regulär beendet, {totalPasses - pass} ausstehende(r) Durchlauf/Durchläufe übersprungen.");
                    break;
                }
            }

            if (softStopped)
            {
                StatusTextBlock.Text = $"Benchmark per Soft-Stopp beendet ({completedPasses}/{totalPasses} Durchläufe abgeschlossen).";
            }
            else
            {
                StatusTextBlock.Text = totalPasses > 1
                    ? $"Alle {totalPasses} Benchmark-Durchläufe abgeschlossen."
                    : "Benchmark abgeschlossen.";
                if (totalPasses > 1)
                {
                    AppendLog($"Alle {totalPasses} Durchläufe abgeschlossen.");
                }
            }
        }
        catch (OperationCanceledException)
        {
            var message = totalPasses > 1
                ? $"Benchmark abgebrochen ({completedPasses}/{totalPasses} Durchläufe abgeschlossen)."
                : "Benchmark abgebrochen.";
            StatusTextBlock.Text = message;
            AppendLog(message);
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
            DisposeManualStopTokens();
            _activeRunNumber = 0;
            _stopAfterCurrentPassRequested = false;
            RepeatCountTextBox.Text = totalPasses.ToString();
            SetBusy(false, benchmarkRunning: false);
        }
    }

    private void DisposeManualStopTokens()
    {
        _run1ManualStop?.Dispose();
        _run1ManualStop = null;
        _run2ManualStop?.Dispose();
        _run2ManualStop = null;
        _run3ManualStop?.Dispose();
        _run3ManualStop = null;
    }

    private void CancelBenchmarkButton_Click(object sender, RoutedEventArgs e)
    {
        _benchmarkCancellation?.Cancel();
        CancelBenchmarkButton.IsEnabled = false;
        SoftStopButton.IsEnabled = false;
        SetManualAbortButtons(run1Enabled: false, run2Enabled: false);
        StatusTextBlock.Text = "Abbruch angefordert...";
        AppendLog("Abbruch angefordert...");
    }

    private void SoftStopButton_Click(object sender, RoutedEventArgs e)
    {
        if (_benchmarkCancellation is null || _stopAfterCurrentPassRequested)
        {
            return;
        }

        _stopAfterCurrentPassRequested = true;
        SoftStopButton.IsEnabled = false;
        var message = "Soft-Stopp angefordert: aktueller Durchlauf läuft zu Ende und wird archiviert, ausstehende Durchläufe werden übersprungen.";
        StatusTextBlock.Text = message;
        AppendLog(message);
    }

    private void AbortRun1Button_Click(object sender, RoutedEventArgs e)
    {
        RequestManualRunStop(runNumber: 1);
    }

    private void AbortRun2Button_Click(object sender, RoutedEventArgs e)
    {
        RequestManualRunStop(runNumber: 2);
    }

    private void AbortRun3Button_Click(object sender, RoutedEventArgs e)
    {
        RequestManualRunStop(runNumber: 3);
    }

    private void RequestManualRunStop(int runNumber)
    {
        var cts = runNumber switch
        {
            1 => _run1ManualStop,
            2 => _run2ManualStop,
            3 => _run3ManualStop,
            _ => null
        };
        if (cts is null || cts.IsCancellationRequested)
        {
            return;
        }

        cts.Cancel();
        switch (runNumber)
        {
            case 1:
                AbortRun1Button.IsEnabled = false;
                break;
            case 2:
                AbortRun2Button.IsEnabled = false;
                break;
            case 3:
                AbortRun3Button.IsEnabled = false;
                break;
        }

        var message = $"Run {runNumber} manuell gestoppt: aktueller Stream wird geschlossen, bisherige Tokens/Thinking werden analysiert.";
        StatusTextBlock.Text = message;
        AppendLog(message);

        if (_activeRunNumber == runNumber && _liveStatusBlock is not null)
        {
            _liveStatusBlock.Text = message;
            _liveStatusBlock.SetResourceReference(TextBlock.ForegroundProperty, WarningTextBrushKey);
        }
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
        var timeoutSeconds = ParseInt(TimeoutTextBox.Text, "Timeout", BenchmarkDefaults.OfficialRequestTimeoutSeconds, min: 30);
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
            TruthAuditPromptPath = Path.Combine(_repositoryRoot, "benchmarks", "supercalc-v3", "prompts", "truth_audit_v1.md"),
            SchemaPath = Path.Combine(_repositoryRoot, "benchmarks", "supercalc-v3", "schemas", "llm_findings.schema.json"),
            TruthAuditSchemaPath = Path.Combine(_repositoryRoot, "benchmarks", "supercalc-v3", "schemas", "truth_audit.schema.json"),
            OutputDirectory = outputDirectory,
            Temperature = 0.0,
            TopP = 1.0,
            MaxTokens = maxTokens,
            Seed = seed,
            Timeout = TimeSpan.FromSeconds(timeoutSeconds),
            SkipResponseFormat = SkipResponseFormatCheckBox.IsChecked == true,
            DisableThinking = DisableThinkingCheckBox.IsChecked == true,
            WithTruthAudit = true,
            TruthAuditRepeatMode = "always",
            TruthAuditSource = "best",
            ArchiveDirectory = Path.Combine(_repositoryRoot, ArchiveStore.DefaultArchiveFolderName),
            QuantOverride = string.IsNullOrWhiteSpace(QuantTextBox.Text) ? null : QuantTextBox.Text.Trim()
        };
    }

    // ---- Live streaming + per-run rendering ---------------------------------

    private void BeginLiveRun(StackPanel panel, string runLabel, int runNumber)
    {
        _activeLivePanel = panel;
        _activeRunNumber = runNumber;
        _liveReasoningChars = 0;
        _liveContentChars = 0;
        SetManualAbortButtons(run1Enabled: runNumber == 1, run2Enabled: runNumber == 2, run3Enabled: runNumber == 3);

        panel.Children.Clear();

        _liveStatusBlock = new TextBlock
        {
            Text = $"{runLabel}: warte auf erste Tokens vom Server...",
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 6)
        };
        _liveStatusBlock.SetResourceReference(TextBlock.ForegroundProperty, InfoTextBrushKey);
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
        var textBox = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            MinHeight = 90,
            MaxHeight = 300,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        textBox.SetResourceReference(Control.ForegroundProperty, TextPrimaryBrushKey);
        textBox.SetResourceReference(Control.BackgroundProperty, CodeBackgroundBrushKey);
        textBox.SetResourceReference(Control.BorderBrushProperty, BorderBrushKey);
        return textBox;
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
                    _liveStatusBlock.SetResourceReference(TextBlock.ForegroundProperty, InfoTextBrushKey);
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

            case ChatStreamDeltaKind.LoopDetected:
                if (_liveStatusBlock is not null)
                {
                    _liveStatusBlock.Text = "Loop erkannt — Anfrage wird abgebrochen. " + delta.Text;
                    _liveStatusBlock.SetResourceReference(TextBlock.ForegroundProperty, WarningTextBrushKey);
                }
                AppendLog("Loop erkannt; Streaming-Anfrage wird abgebrochen: " + delta.Text);
                break;
        }
    }

    private void UpdateLiveStatusCounts()
    {
        if (_liveStatusBlock is null)
        {
            return;
        }

        _liveStatusBlock.Text = "Streamt... exakte Tokenzahlen werden nach Abschluss mit dem Modell-Tokenizer ermittelt.";
    }

    private void OnRunCompleted(BenchmarkRunArtifacts artifacts)
    {
        // The runner awaits with ConfigureAwait(false), so this callback fires on a
        // thread-pool thread. Marshal onto the UI thread before touching any controls.
        Dispatcher.Invoke(() =>
        {
            // Render the finished run's score, matrix/audit grid and the full (non-live)
            // raw-output panel right away. Then prepare the live view for the next run.
            if (string.Equals(artifacts.RunName, "Run 1", StringComparison.OrdinalIgnoreCase))
            {
                Run1ScoreTextBlock.Text = $"{artifacts.Score.ScorePercent:0.##}/100";
                Run1DetailsTextBlock.Text = FormatScoreDetails(artifacts);
                Run1MatrixGrid.ItemsSource = artifacts.Score.Vulnerabilities;
                Run1FindingsGrid.ItemsSource = artifacts.Score.Findings;
                PopulateRawOutputPanel(Run1RawPanel, artifacts);
                AppendLoopWarnings(artifacts);

                // Run 1 done → spin up the live view for Run 2.
                AbortRun1Button.IsEnabled = false;
                Run2ScoreTextBlock.Text = "Läuft...";
                BeginLiveRun(Run2RawPanel, "Run 2 — Self-Validation", runNumber: 2);
                return;
            }

            if (string.Equals(artifacts.RunName, "Run 2", StringComparison.OrdinalIgnoreCase))
            {
                Run2ScoreTextBlock.Text = $"{artifacts.Score.ScorePercent:0.##}/100";
                Run2DetailsTextBlock.Text = FormatScoreDetails(artifacts);
                Run2MatrixGrid.ItemsSource = artifacts.Score.Vulnerabilities;
                Run2FindingsGrid.ItemsSource = artifacts.Score.Findings;
                PopulateRawOutputPanel(Run2RawPanel, artifacts);
                AbortRun2Button.IsEnabled = false;
                AppendLoopWarnings(artifacts);

                // Run 2 done → the GUI always runs the non-blind Run 3 honesty audit.
                Run3ScoreTextBlock.Text = "Läuft...";
                Run3DetailsTextBlock.Text = "Truth-Audit wird vorbereitet...";
                Run3AuditSummaryTextBlock.Text = "Run 3 läuft: Ground Truth ist für diesen Ehrlichkeits-/Accountability-Test absichtlich sichtbar.";
                BeginLiveRun(Run3RawPanel, "Run 3 — Truth Audit / Honesty", runNumber: 3);
                return;
            }

            if (string.Equals(artifacts.RunName, "Run 3", StringComparison.OrdinalIgnoreCase)
                || string.Equals(artifacts.RunKind, "truth_audit", StringComparison.OrdinalIgnoreCase))
            {
                var accountability = artifacts.TruthAudit?.AccountabilityScore ?? artifacts.Score.ScorePercent;
                Run3ScoreTextBlock.Text = $"{accountability:0.##}/100";
                Run3DetailsTextBlock.Text = FormatTruthAuditDetails(artifacts);
                Run3AuditSummaryTextBlock.Text = FormatTruthAuditDetails(artifacts);
                Run3AuditGrid.ItemsSource = artifacts.TruthAudit?.Items;
                PopulateRawOutputPanel(Run3RawPanel, artifacts);
                _activeRunNumber = 0;
                AbortRun3Button.IsEnabled = false;
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
        QueueComparisonRefresh(preserveSelection: true);
    }

    // ---- Comparison tab ------------------------------------------------------

    private const string AllFamiliesLabel = "Alle Modelle";

    private string ArchiveRoot => Path.Combine(_repositoryRoot, ArchiveStore.DefaultArchiveFolderName);

    private IReadOnlyList<ArchiveGroup> _comparisonGroups = [];
    private int _comparisonLoadVersion;
    private string? _editingComparisonGroupKey;
    private string? _editingComparisonOriginalModelFamily;
    private string? _editingComparisonOriginalQuant;

    private async void RefreshComparisonButton_Click(object sender, RoutedEventArgs e)
    {
        await RefreshComparisonAsync(preserveSelection: true);
    }

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

    private void ComparisonEditableTextBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is not TextBox { DataContext: ComparisonGridRow row })
        {
            return;
        }

        ComparisonGrid.SelectedItem = row;

        if (_editingComparisonGroupKey is not null)
        {
            return;
        }

        _editingComparisonGroupKey = row.GroupKey;
        _editingComparisonOriginalModelFamily = row.ModelFamily;
        _editingComparisonOriginalQuant = row.Quant;
    }

    private void ComparisonEditableTextBox_PreviewMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TextBox textBox)
        {
            return;
        }

        e.Handled = true;
        textBox.Focus();
        textBox.SelectAll();
    }

    private void ComparisonEditableTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox textBox)
        {
            return;
        }

        if (e.Key == Key.Enter)
        {
            textBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            ComparisonGrid.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            CancelComparisonTextBoxEdit(textBox);
            e.Handled = true;
        }
    }

    private void ComparisonEditableTextBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is not TextBox { DataContext: ComparisonGridRow row } textBox
            || _editingComparisonGroupKey is null
            || _editingComparisonOriginalModelFamily is null
            || _editingComparisonOriginalQuant is null)
        {
            return;
        }

        textBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();

        var originalGroupKey = _editingComparisonGroupKey;
        var originalModelFamily = _editingComparisonOriginalModelFamily;
        var originalQuant = _editingComparisonOriginalQuant;
        ClearComparisonEditState();

        CommitComparisonIdentityEdit(row, originalGroupKey, originalModelFamily, originalQuant);
    }

    private void CancelComparisonTextBoxEdit(TextBox textBox)
    {
        if (textBox.DataContext is ComparisonGridRow row
            && _editingComparisonOriginalModelFamily is not null
            && _editingComparisonOriginalQuant is not null)
        {
            row.ModelFamily = _editingComparisonOriginalModelFamily;
            row.Quant = _editingComparisonOriginalQuant;

            var property = textBox.GetBindingExpression(TextBox.TextProperty)?.ParentBinding.Path.Path;
            textBox.Text = string.Equals(property, nameof(ComparisonGridRow.Quant), StringComparison.Ordinal)
                ? _editingComparisonOriginalQuant
                : _editingComparisonOriginalModelFamily;
        }

        ClearComparisonEditState();
        ComparisonGrid.Focus();
    }

    private void CommitComparisonIdentityEdit(
        ComparisonGridRow row,
        string originalGroupKey,
        string originalModelFamily,
        string originalQuant)
    {
        var newModelFamily = (row.ModelFamily ?? string.Empty).Trim();
        var newQuant = (row.Quant ?? string.Empty).Trim();

        if (string.IsNullOrWhiteSpace(newModelFamily) || string.IsNullOrWhiteSpace(newQuant))
        {
            MessageBox.Show(this,
                "Modell und Quant dürfen nicht leer sein.",
                "Archiv-Bearbeitung",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            QueueComparisonRefresh(preserveSelection: true);
            return;
        }

        if (string.Equals(newModelFamily, originalModelFamily, StringComparison.Ordinal)
            && string.Equals(newQuant, originalQuant, StringComparison.Ordinal))
        {
            QueueComparisonRefresh(preserveSelection: true);
            return;
        }

        try
        {
            var group = _comparisonGroups.FirstOrDefault(g =>
                string.Equals(g.GroupKey, originalGroupKey, StringComparison.OrdinalIgnoreCase));
            if (group is null)
            {
                throw new InvalidOperationException("Die Vergleichsgruppe wurde nicht mehr im Archiv gefunden. Bitte Archiv neu laden.");
            }

            var updatedPaths = new ArchiveStore(ArchiveRoot).UpdateIdentity(group.Records, newModelFamily, newQuant);
            AppendLog($"Archiv bearbeitet: {originalModelFamily} · {originalQuant} → {newModelFamily} · {newQuant} ({updatedPaths.Count} Scorecard(s)).");
            QueueComparisonRefresh(preserveSelection: true);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                "Archiv-Scorecard konnte nicht aktualisiert werden:\n\n" + ex.Message,
                "Archiv-Bearbeitung fehlgeschlagen",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            AppendLog("FEHLER beim Bearbeiten des Archivs: " + ex.Message);
            QueueComparisonRefresh(preserveSelection: true);
        }
    }

    private void ClearComparisonEditState()
    {
        _editingComparisonGroupKey = null;
        _editingComparisonOriginalModelFamily = null;
        _editingComparisonOriginalQuant = null;
    }

    private void QueueComparisonRefresh(bool preserveSelection)
    {
        _ = RefreshComparisonAsync(preserveSelection);
    }

    private async Task RefreshComparisonAsync(bool preserveSelection)
    {
        var loadVersion = Interlocked.Increment(ref _comparisonLoadVersion);
        var previous = preserveSelection ? (FamilyFilterComboBox.SelectedItem as string) : null;
        var archiveRoot = ArchiveRoot;
        var stopwatch = Stopwatch.StartNew();

        try
        {
            RefreshComparisonButton.IsEnabled = false;
            ComparisonStatusTextBlock.Text = "Archiv wird im Hintergrund geladen...";

            var groups = await Task.Run(() => new ArchiveStore(archiveRoot).LoadGroups());
            if (loadVersion != _comparisonLoadVersion)
            {
                return;
            }

            _comparisonGroups = groups;

            var families = new List<string> { AllFamiliesLabel };
            families.AddRange(_comparisonGroups
                .Select(g => g.ModelFamily)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase));

            FamilyFilterComboBox.ItemsSource = families;
            FamilyFilterComboBox.SelectedItem = previous is not null && families.Contains(previous, StringComparer.OrdinalIgnoreCase)
                ? previous
                : AllFamiliesLabel;

            RebuildComparisonGrid();
            AppendLog($"Archiv geladen: {_comparisonGroups.Sum(g => g.Records.Count)} Scorecard(s) in {_comparisonGroups.Count} Gruppe(n) ({stopwatch.ElapsedMilliseconds} ms).");
        }
        catch (Exception ex)
        {
            if (loadVersion != _comparisonLoadVersion)
            {
                return;
            }

            ComparisonStatusTextBlock.Text = "Archiv konnte nicht geladen werden: " + ex.Message;
            AppendLog("FEHLER beim Laden des Archivs: " + ex.Message);
        }
        finally
        {
            if (loadVersion == _comparisonLoadVersion)
            {
                RefreshComparisonButton.IsEnabled = true;
            }
        }
    }

    private void RebuildComparisonGrid()
    {
        var report = BuildCurrentComparison();
        ComparisonGrid.ItemsSource = report.Series.Select(ComparisonGridRow.FromSeries).ToList();
        OpenComparisonButton.IsEnabled = !report.IsEmpty;

        if (report.IsEmpty)
        {
            ComparisonStatusTextBlock.Text = _comparisonGroups.Count == 0
                ? "Noch keine archivierten Runs. Starte einen Benchmark — danach erscheinen die Ergebnisse hier."
                : "Für die gewählte Modellfamilie gibt es keine Runs.";
            return;
        }

        var aggregate = SelectedAggregate switch
        {
            ComparisonAggregate.Best => "Bester Run",
            ComparisonAggregate.Median => "Median",
            _ => "Durchschnitt"
        };
        ComparisonStatusTextBlock.Text =
            $"{report.Series.Count} Modell/Quant-Gruppe(n), {report.VulnerabilityAxis.Count} Schwachstellen · Wertung: {aggregate}, Run-Sicht: {SelectedRunView}, Metrik: {SelectedMetric}. " +
            "HTML enthält clientseitige Filter, Heatmap, Severity-, Run1/Run2-, Stabilitäts- und Qualitätsdiagnosen. Modell/Quant per Doppelklick direkt bearbeiten.";
    }

    private ComparisonReport BuildCurrentComparison()
    {
        var benchmarkId = _comparisonGroups
            .SelectMany(g => g.Records)
            .Select(r => r.BenchmarkId)
            .FirstOrDefault() ?? "supercalc";

        var family = FamilyFilterComboBox.SelectedItem as string;
        var familyFilter = string.Equals(family, AllFamiliesLabel, StringComparison.Ordinal) ? null : family;

        var metadata = VulnerabilityMetadataIndex.Load(Path.Combine(_repositoryRoot, "benchmarks", "supercalc-v3", "ground_truth.json"));
        return ComparisonReport.Build(_comparisonGroups, benchmarkId, SelectedAggregate, familyFilter, metadata, SelectedRunView, SelectedMetric);
    }

    private ComparisonAggregate SelectedAggregate =>
        ((AggregateComboBox.SelectedItem as ComboBoxItem)?.Content as string) switch
        {
            "Bester Run" => ComparisonAggregate.Best,
            "Median" => ComparisonAggregate.Median,
            _ => ComparisonAggregate.Average
        };

    private ComparisonRunView SelectedRunView =>
        ((RunViewComboBox.SelectedItem as ComboBoxItem)?.Content as string) switch
        {
            "Run 1" => ComparisonRunView.Run1,
            "Run 2" => ComparisonRunView.Run2,
            "Delta" => ComparisonRunView.Delta,
            _ => ComparisonRunView.Primary
        };

    private ComparisonMetric SelectedMetric =>
        ((MetricComboBox.SelectedItem as ComboBoxItem)?.Content as string) switch
        {
            "Critical Recall" => ComparisonMetric.CriticalRecall,
            "F1" => ComparisonMetric.F1,
            "Stability" => ComparisonMetric.Stability,
            "Run2-Delta" => ComparisonMetric.Run2Delta,
            "Evidence Fidelity" => ComparisonMetric.EvidenceFidelity,
            "Hallucination" => ComparisonMetric.HallucinationRate,
            "Accountability" => ComparisonMetric.Accountability,
            "Tokeneffizienz" => ComparisonMetric.TokenEfficiency,
            _ => ComparisonMetric.Score
        };

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

    private sealed class ComparisonGridRow
    {
        public string GroupKey { get; init; } = string.Empty;
        public string ModelFamily { get; set; } = string.Empty;
        public string Quant { get; set; } = string.Empty;
        public int RunCount { get; init; }
        public double ScorePercent { get; init; }
        public double CriticalRecall { get; init; }
        public double Stability { get; init; }
        public double EvidenceFidelity { get; init; }
        public double LocationAccuracy { get; init; }
        public double HallucinationRate { get; init; }
        public double AccountabilityScore { get; init; }
        public double Run2Delta { get; init; }
        public double? ReasoningTokens { get; init; }
        public double? OutputTokens { get; init; }
        public double? CompletionTokens { get; init; }
        public double? ScorePer1KTokens { get; init; }
        public double ScoreMedian { get; init; }
        public double ScoreStdDev { get; init; }
        public double ScoreMin { get; init; }
        public double ScoreMax { get; init; }
        public double Precision { get; init; }
        public double Recall { get; init; }
        public double F1 { get; init; }
        public int FullTruePositives { get; init; }
        public int PartialTruePositives { get; init; }
        public int FalsePositives { get; init; }
        public int Missed { get; init; }

        public static ComparisonGridRow FromSeries(ComparisonSeries series) => new()
        {
            GroupKey = series.GroupKey,
            ModelFamily = series.ModelFamily,
            Quant = series.Quant,
            RunCount = series.RunCount,
            ScorePercent = series.ScorePercent,
            CriticalRecall = series.CriticalRecall,
            Stability = series.VulnerabilityStability,
            EvidenceFidelity = series.EvidenceFidelity,
            LocationAccuracy = series.LocationAccuracy,
            HallucinationRate = series.HallucinationRate,
            AccountabilityScore = series.AccountabilityScore,
            Run2Delta = series.Run2ScoreDelta,
            ReasoningTokens = series.ReasoningTokens,
            OutputTokens = series.OutputTokens,
            CompletionTokens = series.CompletionTokens,
            ScorePer1KTokens = series.ScorePer1KTokens,
            ScoreMedian = series.ScoreMedian,
            ScoreStdDev = series.ScoreStdDev,
            ScoreMin = series.ScoreMin,
            ScoreMax = series.ScoreMax,
            Precision = series.Precision,
            Recall = series.Recall,
            F1 = series.F1,
            FullTruePositives = series.FullTruePositives,
            PartialTruePositives = series.PartialTruePositives,
            FalsePositives = series.FalsePositives,
            Missed = series.Missed
        };
    }

    private void ResetResultUi(bool clearLog = true)
    {
        _lastResult = null;
        _activeLivePanel = null;
        _liveReasoningBox = null;
        _liveContentBox = null;
        _liveStatusBlock = null;
        _liveReasoningChars = 0;
        _liveContentChars = 0;
        _activeRunNumber = 0;
        SetManualAbortButtons(run1Enabled: false, run2Enabled: false);
        Run1ScoreTextBlock.Text = "Läuft...";
        Run1DetailsTextBlock.Text = string.Empty;
        Run2ScoreTextBlock.Text = "Wartet auf Run 1";
        Run2DetailsTextBlock.Text = string.Empty;
        Run3ScoreTextBlock.Text = "Wartet auf Run 2";
        Run3DetailsTextBlock.Text = string.Empty;
        Run3AuditSummaryTextBlock.Text = "Run 3 läuft automatisch nach Run 2. Danach erscheinen hier Ehrlichkeits-/Accountability-Metriken.";
        ComparisonTextBlock.Text = "Nach dem Benchmark sichtbar";
        Run1MatrixGrid.ItemsSource = null;
        Run2MatrixGrid.ItemsSource = null;
        Run1FindingsGrid.ItemsSource = null;
        Run2FindingsGrid.ItemsSource = null;
        Run3AuditGrid.ItemsSource = null;
        ShowRawOutputPlaceholder(Run1RawPanel, "Run 1 läuft bzw. wartet auf Server-Antwort...");
        ShowRawOutputPlaceholder(Run2RawPanel, "Run 2 wartet auf Run 1...");
        ShowRawOutputPlaceholder(Run3RawPanel, "Run 3 wartet auf Run 2...");
        if (clearLog)
        {
            ProgressLogTextBox.Clear();
        }
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
                      $"Finish: {artifacts.FinishReason} | Output: {FormatTokens(artifacts.ResponseTokens)} | Thinking: {FormatTokens(artifacts.ReasoningTokens)} | Gesamt: {FormatTokens(artifacts.CompletionTokens)}\n" +
                      $"Denken-vs-Sagen: {FormatReasoningDisclosure(artifacts.ReasoningDisclosure)}";

        if (artifacts.ManuallyStopped)
        {
            details += "\nManuell gestoppt: bisheriger Output/Thinking wurde analysiert und gespeichert.";
        }

        if (!string.IsNullOrWhiteSpace(artifacts.Parse.Warning))
        {
            details += $"\nParse: {artifacts.Parse.Warning}";
        }

        return details;
    }

    private static string FormatTruthAuditDetails(BenchmarkRunArtifacts artifacts)
    {
        var audit = artifacts.TruthAudit;
        if (audit is null)
        {
            return $"Truth-Audit ohne auswertbare Audit-Daten. Finish: {artifacts.FinishReason} | Output: {FormatTokens(artifacts.ResponseTokens)} | Thinking: {FormatTokens(artifacts.ReasoningTokens)} | Gesamt: {FormatTokens(artifacts.CompletionTokens)}";
        }

        var details = $"Auditiert: {audit.AuditedRunName} ({audit.AuditedRunScorePercent:0.##}/100, {audit.AuditedRunScoreProfile})\n" +
                      $"Accountability: {audit.AccountabilityScore:0.##}/100 | Truth-Accuracy: {audit.TruthAuditAccuracy:P1}\n" +
                      $"Miss-Admission: {audit.MissAdmissionRate:P1} | FP-Admission: {audit.FalsePositiveAdmissionRate:P1} | Overclaim: {audit.OverclaimRate:P1}\n" +
                      $"Quote-Fidelity: {audit.QuoteFidelity:P1} | Evidence-Laundering: {audit.EvidenceLaunderingCount} | Widersprüche: {audit.ContradictionCount}\n" +
                      $"Tatsächlich verpasst: {audit.ActualMissedCount} | tatsächliche False Positives: {audit.ActualFalsePositiveCount}\n" +
                      $"Finish: {artifacts.FinishReason} | Output: {FormatTokens(artifacts.ResponseTokens)} | Thinking: {FormatTokens(artifacts.ReasoningTokens)} | Gesamt: {FormatTokens(artifacts.CompletionTokens)}\n" +
                      "Non-blind: Ground Truth ist in Run 3 absichtlich sichtbar; dieser Run verändert den Blind/Self-Validation-Score nicht.";

        if (!string.IsNullOrWhiteSpace(audit.SelectionReason))
        {
            details += $"\nAuswahl: {audit.SelectionReason}";
        }

        if (!string.IsNullOrWhiteSpace(audit.Summary))
        {
            details += $"\nSummary: {audit.Summary}";
        }

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

        var disclosure = artifacts.ReasoningDisclosure;
        if (disclosure.HasVisibleReasoning && disclosure.ReasoningOnlyTruePositiveCount > 0)
        {
            AppendLog($"INFO {artifacts.RunName}: Denken-vs-Sagen-Lücke — {disclosure.ReasoningOnlyTruePositiveCount} TP(s) nur im Thinking ({string.Join(", ", disclosure.ReasoningOnlyTruePositiveIds)}).");
        }
    }

    private static void PopulatePromptPreviewPanel(StackPanel panel, string title, string prompt, string requestJson)
    {
        panel.Children.Clear();

        var titleText = new TextBlock
        {
            Text = title,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap
        };
        titleText.SetResourceReference(TextBlock.ForegroundProperty, InfoTextBrushKey);

        var titleBorder = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8),
            Margin = new Thickness(0, 0, 0, 8),
            Child = titleText
        };
        titleBorder.SetResourceReference(Border.BorderBrushProperty, InfoTextBrushKey);
        titleBorder.SetResourceReference(Border.BackgroundProperty, InfoBackgroundBrushKey);
        panel.Children.Add(titleBorder);

        panel.Children.Add(CreateTextExpander(
            "Erste Request-JSON (System + User + Parameter)",
            requestJson,
            RequestTextBrushKey,
            FontStyles.Normal,
            isExpanded: true,
            backgroundBrushKey: RequestBackgroundBrushKey));
        panel.Children.Add(CreateTextExpander(
            $"User-Prompt ({prompt.Length:N0} chars)",
            prompt,
            TextPrimaryBrushKey,
            FontStyles.Normal,
            isExpanded: true,
            backgroundBrushKey: CodeBackgroundBrushKey));
    }

    private static void PopulateRawOutputPanel(StackPanel panel, BenchmarkRunArtifacts artifacts)
    {
        panel.Children.Clear();
        panel.Children.Add(CreateDiagnosticsBlock(artifacts));
        panel.Children.Add(CreateTextExpander(
            "Gesendeter Request JSON (System + User + Parameter)",
            artifacts.RequestJson,
            RequestTextBrushKey,
            FontStyles.Normal,
            isExpanded: false,
            backgroundBrushKey: RequestBackgroundBrushKey));
        panel.Children.Add(CreateTextExpander(
            "User-Prompt aus dem Programm",
            artifacts.Prompt,
            TextPrimaryBrushKey,
            FontStyles.Normal,
            isExpanded: false,
            backgroundBrushKey: CodeBackgroundBrushKey));
        panel.Children.Add(CreateTextExpander(
            $"assistant message.content — OUTPUT ({FormatTokens(artifacts.ResponseTokens)})",
            artifacts.Response,
            OutputTextBrushKey,
            FontStyles.Normal,
            isExpanded: true,
            backgroundBrushKey: OutputBackgroundBrushKey));
        panel.Children.Add(CreateTextExpander(
            $"assistant message.reasoning_content — THINKING ({FormatTokens(artifacts.ReasoningTokens)})",
            artifacts.ReasoningContent,
            ReasoningTextBrushKey,
            FontStyles.Italic,
            isExpanded: false,
            backgroundBrushKey: ReasoningBackgroundBrushKey));
        panel.Children.Add(CreateTextExpander(
            $"Raw API response, unverändert ({artifacts.RawResponse.Length:N0} chars)",
            artifacts.RawResponse,
            TextPrimaryBrushKey,
            FontStyles.Normal,
            isExpanded: false,
            backgroundBrushKey: CodeBackgroundBrushKey));
    }

    private static void ShowRawOutputPlaceholder(StackPanel panel, string message)
    {
        panel.Children.Clear();
        var placeholder = new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(4)
        };
        placeholder.SetResourceReference(TextBlock.ForegroundProperty, TextSecondaryBrushKey);
        panel.Children.Add(placeholder);
    }

    private static Border CreateDiagnosticsBlock(BenchmarkRunArtifacts artifacts)
    {
        var responseLoop = OutputLoopDetector.Analyze(artifacts.Response);
        var reasoningLoop = OutputLoopDetector.Analyze(artifacts.ReasoningContent);
        var hasWarning = artifacts.ManuallyStopped
            || artifacts.LoopDetected
            || responseLoop.HasSuspectedLoop
            || reasoningLoop.HasSuspectedLoop
            || (string.IsNullOrWhiteSpace(artifacts.Response) && !string.IsNullOrWhiteSpace(artifacts.ReasoningContent));

        var builder = new StringBuilder();
        builder.AppendLine($"Finish: {EmptyFallback(artifacts.FinishReason)} | Output: {FormatTokens(artifacts.ResponseTokens)} | Thinking: {FormatTokens(artifacts.ReasoningTokens)} | Gesamt: {FormatTokens(artifacts.CompletionTokens)}");
        builder.AppendLine("Tokenquelle: Thinking/Output = Modell-/tokenize; Gesamt = llama.cpp usage (inkl. unsichtbarer Steuer-/EOS-Tokens).");
        builder.AppendLine($"Manueller Stop: {artifacts.ManuallyStopped}");
        builder.AppendLine($"Loop-Abbruch: {artifacts.LoopDetected} {artifacts.LoopDiagnosticsSummary}");
        builder.AppendLine($"response_format: {artifacts.UsedResponseFormat} | retry ohne response_format: {artifacts.RetriedWithoutResponseFormat} | thinking-disable hint: {artifacts.UsedThinkingControl}");
        if (artifacts.TruthAudit is not null)
        {
            builder.AppendLine($"Truth-Audit: Accountability {artifacts.TruthAudit.AccountabilityScore:0.##}/100 | Accuracy {artifacts.TruthAudit.TruthAuditAccuracy:P1} | Overclaim {artifacts.TruthAudit.OverclaimRate:P1}");
            builder.AppendLine("Run 3 ist non-blind: Ground Truth ist absichtlich sichtbar und verändert den Detection-Score nicht.");
        }

        builder.AppendLine($"Denken-vs-Sagen: {FormatReasoningDisclosure(artifacts.ReasoningDisclosure)}");
        builder.AppendLine($"Loop-Check Output: {responseLoop.Summary}");
        builder.AppendLine($"Loop-Check Thinking: {reasoningLoop.Summary}");

        if (artifacts.LoopDetected)
        {
            builder.AppendLine("WARNUNG: Die Streaming-Anfrage wurde wegen eines wahrscheinlichen Loops in der finalen Antwort vorzeitig geschlossen. Thinking wird nur diagnostiziert, nicht live abgebrochen.");
        }

        if (artifacts.ManuallyStopped)
        {
            builder.AppendLine("HINWEIS: Dieser Run wurde manuell gestoppt; die bis dahin empfangenen Tokens wurden trotzdem geparst, gescored und gespeichert.");
        }

        if (string.IsNullOrWhiteSpace(artifacts.Response) && !string.IsNullOrWhiteSpace(artifacts.ReasoningContent))
        {
            builder.AppendLine("WARNUNG: Server lieferte nur reasoning_content, aber keine finale message.content. Das sieht oft nach Max-Token-Erschöpfung oder endlosem Thinking aus.");
        }

        AppendRepetitionDetails(builder, "Output", responseLoop);
        AppendRepetitionDetails(builder, "Thinking", reasoningLoop);

        var textBlock = new TextBlock
        {
            Text = builder.ToString().TrimEnd(),
            FontFamily = new FontFamily("Segoe UI"),
            TextWrapping = TextWrapping.Wrap
        };
        textBlock.SetResourceReference(TextBlock.ForegroundProperty, hasWarning ? DangerTextBrushKey : TextSecondaryBrushKey);

        var border = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8),
            Margin = new Thickness(0, 0, 0, 8),
            Child = textBlock
        };
        border.SetResourceReference(Border.BorderBrushProperty, hasWarning ? WarningTextBrushKey : BorderBrushKey);
        border.SetResourceReference(Border.BackgroundProperty, hasWarning ? WarningBackgroundBrushKey : SurfaceRaisedBrushKey);
        return border;
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
        string foregroundBrushKey,
        FontStyle fontStyle,
        bool isExpanded,
        string backgroundBrushKey)
    {
        var displayText = string.IsNullOrEmpty(text) ? "<empty>" : text;
        var paragraph = new Paragraph(new Run(displayText))
        {
            Margin = new Thickness(0),
            FontStyle = fontStyle
        };
        paragraph.SetResourceReference(TextElement.ForegroundProperty, foregroundBrushKey);

        var document = new FlowDocument
        {
            PagePadding = new Thickness(8),
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            PageWidth = 20000
        };
        document.SetResourceReference(FlowDocument.BackgroundProperty, backgroundBrushKey);
        document.Blocks.Add(paragraph);

        var richTextBox = new RichTextBox
        {
            IsReadOnly = true,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            MinHeight = 90,
            MaxHeight = 360,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            Document = document
        };
        richTextBox.SetResourceReference(Control.ForegroundProperty, foregroundBrushKey);
        richTextBox.SetResourceReference(Control.BackgroundProperty, backgroundBrushKey);
        richTextBox.SetResourceReference(Control.BorderBrushProperty, BorderBrushKey);

        return new Expander
        {
            Header = header,
            IsExpanded = isExpanded,
            Margin = new Thickness(0, 6, 0, 0),
            Content = richTextBox
        };
    }

    private static string FormatTokens(int? value) => value.HasValue ? $"{value.Value:N0} Tokens" : "n/a";

    private static string EmptyFallback(string value) => string.IsNullOrWhiteSpace(value) ? "<empty>" : value;

    private static string FormatReasoningDisclosure(ReasoningDisclosureDiagnostics diagnostics)
    {
        if (!diagnostics.HasVisibleReasoning)
        {
            return "kein sichtbares Thinking";
        }

        return $"Thinking-TPs {diagnostics.ReasoningTruePositiveCount}, " +
               $"Output-TPs {diagnostics.OutputTruePositiveCount}, " +
               $"nur gedacht {diagnostics.ReasoningOnlyTruePositiveCount}, " +
               $"nur gesagt {diagnostics.OutputOnlyTruePositiveCount}, " +
               $"Coverage {FormatNullablePercent(diagnostics.ReasoningToOutputCoverage)}";
    }

    private static string FormatNullablePercent(double? value) => value is null ? "n/a" : value.Value.ToString("P1", System.Globalization.CultureInfo.InvariantCulture);

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
        UpdateButton.IsEnabled = !busy;
        RefreshModelsButton.IsEnabled = !busy;
        StartBenchmarkButton.IsEnabled = !busy;
        ServerUrlTextBox.IsEnabled = !busy;
        ModelComboBox.IsEnabled = !busy;
        MaxTokensTextBox.IsEnabled = !busy;
        TimeoutTextBox.IsEnabled = !busy;
        SeedTextBox.IsEnabled = !busy;
        RepeatCountTextBox.IsEnabled = !busy;
        OutputDirectoryTextBox.IsEnabled = !busy;
        SkipResponseFormatCheckBox.IsEnabled = !busy;
        DisableThinkingCheckBox.IsEnabled = !busy;
        PreviewPromptButton.IsEnabled = !busy;
        CancelBenchmarkButton.IsEnabled = benchmarkRunning;
        if (!benchmarkRunning)
        {
            SoftStopButton.IsEnabled = false;
            SetManualAbortButtons(run1Enabled: false, run2Enabled: false);
        }
    }

    private void SetManualAbortButtons(bool run1Enabled, bool run2Enabled, bool run3Enabled = false)
    {
        AbortRun1Button.IsEnabled = run1Enabled && _run1ManualStop is { IsCancellationRequested: false };
        AbortRun2Button.IsEnabled = run2Enabled && _run2ManualStop is { IsCancellationRequested: false };
        AbortRun3Button.IsEnabled = run3Enabled && _run3ManualStop is { IsCancellationRequested: false };
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

    // ---- Window placement persistence ---------------------------------------

    private static string WindowPlacementFilePath =>
        Path.Combine(GetLocalAppDataRoot(), "SuperCalcBenchmark", "window-placement.json");

    private void RestoreWindowPlacement()
    {
        try
        {
            var path = WindowPlacementFilePath;
            if (!File.Exists(path))
            {
                return;
            }

            var placement = JsonSerializer.Deserialize<WindowPlacement>(File.ReadAllText(path));
            if (placement is null
                || !double.IsFinite(placement.Left) || !double.IsFinite(placement.Top)
                || !double.IsFinite(placement.Width) || !double.IsFinite(placement.Height)
                || placement.Width < 200 || placement.Height < 200)
            {
                return;
            }

            // Only restore if the saved bounds are still at least partially on a screen
            // (monitor removed / resolution changed since the last session).
            const double minVisible = 80;
            var virtualLeft = SystemParameters.VirtualScreenLeft;
            var virtualTop = SystemParameters.VirtualScreenTop;
            var virtualRight = virtualLeft + SystemParameters.VirtualScreenWidth;
            var virtualBottom = virtualTop + SystemParameters.VirtualScreenHeight;
            if (placement.Left > virtualRight - minVisible
                || placement.Top > virtualBottom - minVisible
                || placement.Left + placement.Width < virtualLeft + minVisible
                || placement.Top + placement.Height < virtualTop + minVisible)
            {
                return;
            }

            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = placement.Left;
            Top = placement.Top;
            Width = placement.Width;
            Height = placement.Height;
            if (placement.IsMaximized)
            {
                WindowState = WindowState.Maximized;
            }
        }
        catch
        {
            // A corrupt placement file must never block app startup; the default
            // size/CenterScreen from XAML is used instead.
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        SaveWindowPlacement();
        base.OnClosing(e);
    }

    private void SaveWindowPlacement()
    {
        try
        {
            // When maximized/minimized, RestoreBounds carries the last normal bounds,
            // so un-maximizing after a restart lands on the previous window size.
            var bounds = WindowState == WindowState.Normal
                ? new Rect(Left, Top, Width, Height)
                : RestoreBounds;
            if (bounds.IsEmpty || !double.IsFinite(bounds.Width) || !double.IsFinite(bounds.Height))
            {
                return;
            }

            var placement = new WindowPlacement(bounds.Left, bounds.Top, bounds.Width, bounds.Height, WindowState == WindowState.Maximized);
            var path = WindowPlacementFilePath;
            CreateParentDirectory(path);
            File.WriteAllText(path, JsonSerializer.Serialize(placement));
        }
        catch
        {
            // Best effort: failing to persist the placement must never block closing.
        }
    }

    private sealed record WindowPlacement(double Left, double Top, double Width, double Height, bool IsMaximized);

    private SafeUpdateResult RunSafeRepositoryUpdate()
    {
        var rootResult = RunGit(_repositoryRoot, "rev-parse", "--show-toplevel");
        if (rootResult.ExitCode != 0)
        {
            throw new InvalidOperationException("Update ist nur in einem Git-Checkout möglich.\n\n" + CombineProcessOutput(rootResult));
        }

        var repositoryRoot = Path.GetFullPath(rootResult.StandardOutput.Trim());
        var headBefore = RunGitOrThrow(repositoryRoot, "rev-parse", "--short", "HEAD").StandardOutput.Trim();
        var localDataFiles = FindLocalBenchmarkDataFiles(repositoryRoot);
        var backup = BackupLocalBenchmarkData(repositoryRoot, localDataFiles);

        var pullResult = RunGit(repositoryRoot, "pull", "--ff-only");
        if (pullResult.ExitCode != 0)
        {
            var backupHint = backup.RelativeFiles.Count > 0
                ? $"\n\nLokale Benchmarkdaten wurden vorher gesichert unter:\n{backup.BackupRoot}"
                : string.Empty;

            throw new InvalidOperationException("git pull --ff-only ist fehlgeschlagen. Es wurde kein reset/clean ausgeführt." +
                                                backupHint + "\n\n" + CombineProcessOutput(pullResult));
        }

        BenchmarkDataRestoreResult restore;
        try
        {
            restore = RestoreLocalBenchmarkData(repositoryRoot, backup);
        }
        catch (Exception ex)
        {
            var backupHint = backup.RelativeFiles.Count > 0 ? $"\nBackup: {backup.BackupRoot}" : string.Empty;
            throw new InvalidOperationException("Update wurde gezogen, aber lokale Benchmarkdaten konnten nicht vollständig zurückgeschrieben werden." +
                                                backupHint + "\n\n" + ex.Message, ex);
        }

        var headAfter = RunGitOrThrow(repositoryRoot, "rev-parse", "--short", "HEAD").StandardOutput.Trim();
        return new SafeUpdateResult(repositoryRoot, headBefore, headAfter, pullResult.StandardOutput, pullResult.StandardError, backup, restore);
    }

    private static IReadOnlyList<string> FindLocalBenchmarkDataFiles(string repositoryRoot)
    {
        var statusArguments = new List<string>
        {
            "status",
            "--porcelain=v1",
            "-z",
            "--ignored=matching",
            "--untracked-files=all",
            "--",
        };
        statusArguments.AddRange(ProtectedBenchmarkDataDirectories);

        var status = RunGitOrThrow(repositoryRoot, statusArguments.ToArray());
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entries = status.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries);

        for (var i = 0; i < entries.Length; i++)
        {
            var entry = entries[i];
            if (entry.Length < 4)
            {
                continue;
            }

            var code = entry[..2];
            var relativePath = entry[3..];
            AddLocalDataPath(repositoryRoot, relativePath, files);

            if ((code[0] == 'R' || code[0] == 'C') && i + 1 < entries.Length)
            {
                AddLocalDataPath(repositoryRoot, entries[++i], files);
            }
        }

        return files.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static void AddLocalDataPath(string repositoryRoot, string relativePath, HashSet<string> files)
    {
        var normalized = NormalizeGitRelativePath(relativePath);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return;
        }

        var fullPath = Path.Combine(repositoryRoot, normalized);
        if (Directory.Exists(fullPath))
        {
            foreach (var file in Directory.EnumerateFiles(fullPath, "*", SearchOption.AllDirectories))
            {
                files.Add(NormalizeRelativePath(Path.GetRelativePath(repositoryRoot, file)));
            }
        }
        else if (File.Exists(fullPath))
        {
            files.Add(NormalizeRelativePath(Path.GetRelativePath(repositoryRoot, fullPath)));
        }
    }

    private static BenchmarkDataBackup BackupLocalBenchmarkData(string repositoryRoot, IReadOnlyList<string> relativeFiles)
    {
        if (relativeFiles.Count == 0)
        {
            return new BenchmarkDataBackup(string.Empty, []);
        }

        var backupRoot = Path.Combine(
            GetLocalAppDataRoot(),
            "SuperCalcBenchmark",
            "UpdateBackups",
            DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"));
        var copiedFiles = new List<string>();

        foreach (var relativeFile in relativeFiles)
        {
            var source = Path.Combine(repositoryRoot, relativeFile);
            if (!File.Exists(source))
            {
                continue;
            }

            var destination = Path.Combine(backupRoot, relativeFile);
            CreateParentDirectory(destination);
            File.Copy(source, destination, overwrite: false);
            copiedFiles.Add(relativeFile);
        }

        return copiedFiles.Count == 0
            ? new BenchmarkDataBackup(string.Empty, [])
            : new BenchmarkDataBackup(backupRoot, copiedFiles);
    }

    private static BenchmarkDataRestoreResult RestoreLocalBenchmarkData(string repositoryRoot, BenchmarkDataBackup backup)
    {
        if (backup.RelativeFiles.Count == 0 || string.IsNullOrWhiteSpace(backup.BackupRoot))
        {
            return new BenchmarkDataRestoreResult(0, 0, []);
        }

        var restoredMissingFiles = 0;
        var unchangedFiles = 0;
        var conflictCopies = new List<string>();
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");

        foreach (var relativeFile in backup.RelativeFiles)
        {
            var backupFile = Path.Combine(backup.BackupRoot, relativeFile);
            if (!File.Exists(backupFile))
            {
                continue;
            }

            var target = Path.Combine(repositoryRoot, relativeFile);
            CreateParentDirectory(target);

            if (!File.Exists(target))
            {
                File.Copy(backupFile, target, overwrite: false);
                restoredMissingFiles++;
                continue;
            }

            if (FilesHaveSameContent(backupFile, target))
            {
                unchangedFiles++;
                continue;
            }

            var conflictRelativePath = BuildConflictCopyRelativePath(relativeFile, stamp);
            var conflictPath = EnsureUniqueFilePath(Path.Combine(repositoryRoot, conflictRelativePath));
            CreateParentDirectory(conflictPath);
            File.Copy(backupFile, conflictPath, overwrite: false);
            conflictCopies.Add(NormalizeRelativePath(Path.GetRelativePath(repositoryRoot, conflictPath)));
        }

        return new BenchmarkDataRestoreResult(restoredMissingFiles, unchangedFiles, conflictCopies);
    }

    private static ProcessRunResult RunGitOrThrow(string workingDirectory, params string[] arguments)
    {
        var result = RunGit(workingDirectory, arguments);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} fehlgeschlagen:\n\n" + CombineProcessOutput(result));
        }

        return result;
    }

    private static ProcessRunResult RunGit(string workingDirectory, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";

        try
        {
            using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("git konnte nicht gestartet werden.");
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit((int)TimeSpan.FromMinutes(10).TotalMilliseconds))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // Best-effort cleanup after a timeout.
                }

                throw new TimeoutException($"git {string.Join(' ', arguments)} hat länger als 10 Minuten gedauert und wurde beendet.");
            }

            Task.WaitAll(outputTask, errorTask);
            return new ProcessRunResult(process.ExitCode, outputTask.Result, errorTask.Result);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new InvalidOperationException("git konnte nicht gestartet werden. Ist Git installiert und im PATH?", ex);
        }
    }

    private static string CombineProcessOutput(ProcessRunResult result)
    {
        var combined = string.Join(Environment.NewLine, result.StandardOutput.Trim(), result.StandardError.Trim()).Trim();
        return string.IsNullOrWhiteSpace(combined) ? $"Exit code {result.ExitCode}" : combined;
    }

    private static IEnumerable<string> SplitLogLines(string text) =>
        text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string NormalizeGitRelativePath(string relativePath) =>
        relativePath.Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static string NormalizeRelativePath(string relativePath) =>
        relativePath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);

    private static string BuildConflictCopyRelativePath(string relativePath, string stamp)
    {
        var directory = Path.GetDirectoryName(relativePath);
        var fileName = Path.GetFileNameWithoutExtension(relativePath);
        var extension = Path.GetExtension(relativePath);
        var conflictFileName = $"{fileName}.local-update-{stamp}{extension}";
        return string.IsNullOrEmpty(directory) ? conflictFileName : Path.Combine(directory, conflictFileName);
    }

    private static string EnsureUniqueFilePath(string path)
    {
        if (!File.Exists(path))
        {
            return path;
        }

        var directory = Path.GetDirectoryName(path) ?? string.Empty;
        var fileName = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);

        for (var i = 2; ; i++)
        {
            var candidate = Path.Combine(directory, $"{fileName}-{i}{extension}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }
    }

    private static bool FilesHaveSameContent(string leftPath, string rightPath)
    {
        var leftInfo = new FileInfo(leftPath);
        var rightInfo = new FileInfo(rightPath);
        if (!leftInfo.Exists || !rightInfo.Exists || leftInfo.Length != rightInfo.Length)
        {
            return false;
        }

        using var left = File.OpenRead(leftPath);
        using var right = File.OpenRead(rightPath);
        var leftBuffer = new byte[81920];
        var rightBuffer = new byte[81920];

        while (true)
        {
            var leftRead = left.Read(leftBuffer, 0, leftBuffer.Length);
            var rightRead = right.Read(rightBuffer, 0, rightBuffer.Length);

            if (leftRead != rightRead)
            {
                return false;
            }

            if (leftRead == 0)
            {
                return true;
            }

            if (!leftBuffer.AsSpan(0, leftRead).SequenceEqual(rightBuffer.AsSpan(0, rightRead)))
            {
                return false;
            }
        }
    }

    private static void CreateParentDirectory(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    private static string GetLocalAppDataRoot()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return string.IsNullOrWhiteSpace(localAppData) ? Path.GetTempPath() : localAppData;
    }

    private sealed record SafeUpdateResult(
        string RepositoryRoot,
        string HeadBefore,
        string HeadAfter,
        string PullOutput,
        string PullError,
        BenchmarkDataBackup Backup,
        BenchmarkDataRestoreResult Restore)
    {
        public bool HeadChanged => !string.Equals(HeadBefore, HeadAfter, StringComparison.OrdinalIgnoreCase);
    }

    private sealed record BenchmarkDataBackup(string BackupRoot, IReadOnlyList<string> RelativeFiles);

    private sealed record BenchmarkDataRestoreResult(int RestoredMissingFiles, int UnchangedFiles, IReadOnlyList<string> ConflictCopies);

    private sealed record ProcessRunResult(int ExitCode, string StandardOutput, string StandardError);

    private static string FindRepositoryRoot()
    {
        var candidates = new List<DirectoryInfo>();
        AddRepositoryRootCandidate(candidates, Environment.GetEnvironmentVariable("SUPERCALC_REPOSITORY_ROOT"));
        AddRepositoryRootCandidate(candidates, Environment.CurrentDirectory);
        AddRepositoryRootCandidate(candidates, AppContext.BaseDirectory);

        var repositoryRoot = candidates.FirstOrDefault(IsRepositoryRoot);
        if (repositoryRoot is not null)
        {
            return repositoryRoot.FullName;
        }

        return candidates.FirstOrDefault()?.FullName ?? Environment.CurrentDirectory;
    }

    private static void AddRepositoryRootCandidate(List<DirectoryInfo> candidates, string? start)
    {
        if (string.IsNullOrWhiteSpace(start))
        {
            return;
        }

        var directory = new DirectoryInfo(start);
        while (directory is not null)
        {
            if (IsBenchmarkAssetRoot(directory) &&
                !candidates.Any(candidate => string.Equals(candidate.FullName, directory.FullName, StringComparison.OrdinalIgnoreCase)))
            {
                candidates.Add(directory);
            }

            directory = directory.Parent;
        }
    }

    private static bool IsBenchmarkAssetRoot(DirectoryInfo directory) =>
        File.Exists(Path.Combine(directory.FullName, "enhanced_calc.cpp")) &&
        File.Exists(Path.Combine(directory.FullName, "benchmarks", "supercalc-v3", "ground_truth.json"));

    private static bool IsRepositoryRoot(DirectoryInfo directory) =>
        File.Exists(Path.Combine(directory.FullName, "SuperCalcBenchmark.slnx")) ||
        Directory.Exists(Path.Combine(directory.FullName, ".git"));
}
