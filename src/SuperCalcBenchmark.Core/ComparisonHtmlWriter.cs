using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace SuperCalcBenchmark.Core;

/// <summary>
/// Renders a <see cref="ComparisonReport"/> to a single self-contained HTML file with
/// offline tables plus optional Chart.js visualizations. Hidden prompt/raw-response data is
/// never embedded; only compact archive metrics and local comparison metadata are included.
/// </summary>
public sealed class ComparisonHtmlWriter
{
    private static readonly JsonSerializerOptions PayloadOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false
    };

    private const string ChartJsCdn = "https://cdn.jsdelivr.net/npm/chart.js@4.4.3/dist/chart.umd.min.js";

    /// <summary>Writes comparison.html (+ comparison.csv) into <paramref name="outputDirectory"/>.</summary>
    public string Write(ComparisonReport report, string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);

        var htmlPath = Path.Combine(outputDirectory, "comparison.html");
        File.WriteAllText(htmlPath, BuildHtml(report), Encoding.UTF8);

        var csvPath = Path.Combine(outputDirectory, "comparison.csv");
        File.WriteAllText(csvPath, BuildCsv(report), Encoding.UTF8);

        return htmlPath;
    }

    public string BuildHtml(ComparisonReport report)
    {
        var palette = BuildPalette(report.Series.Count);
        var payload = new
        {
            benchmarkId = report.BenchmarkId,
            generatedAt = report.GeneratedAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
            aggregate = report.Aggregate.ToString(),
            runView = report.RunView.ToString(),
            metric = MetricValue(report.Metric),
            scoringProfile = report.ScoringProfile,
            axis = report.VulnerabilityMetadata.Select(a => new
            {
                id = a.Id,
                title = a.Title,
                severity = a.Severity,
                cwe = a.Cwe,
                category = a.Category,
                module = a.Module
            }),
            series = report.Series.Select((s, i) => new
            {
                groupKey = s.GroupKey,
                label = s.Label,
                family = s.ModelFamily,
                quant = s.Quant,
                runCount = s.RunCount,
                officialRunCount = s.OfficialRunCount,
                officialComparableRunCount = s.OfficialComparableRunCount,
                legacyMigratedRunCount = s.LegacyMigratedRunCount,
                rescoredRunCount = s.RescoredRunCount,
                sourceHashMatchCount = s.SourceHashMatchCount,
                aggregate = s.Aggregate.ToString(),
                runView = s.RunView.ToString(),
                score = Math.Round(s.ScorePercent, 2),
                scoreMean = Math.Round(s.ScoreMean, 2),
                scoreMedian = Math.Round(s.ScoreMedian, 2),
                scoreStdDev = Math.Round(s.ScoreStdDev, 2),
                scoreIqr = Math.Round(s.ScoreIqr, 2),
                scoreCi95 = s.ScoreCi95.HasValue ? Math.Round(s.ScoreCi95.Value, 2) : (double?)null,
                scoreMin = Math.Round(s.ScoreMin, 2),
                scoreMax = Math.Round(s.ScoreMax, 2),
                precision = Math.Round(s.Precision * 100, 1),
                recall = Math.Round(s.Recall * 100, 1),
                f1 = Math.Round(s.F1 * 100, 1),
                fullTp = s.FullTruePositives,
                partialTp = s.PartialTruePositives,
                falsePositives = s.FalsePositives,
                duplicates = s.Duplicates,
                ignoredLowConfidence = s.IgnoredLowConfidence,
                missed = s.Missed,
                visibleReasoningRuns = s.VisibleReasoningRunCount,
                thinkingParsedFindings = Math.Round(s.ReasoningParsedFindings, 1),
                outputParsedFindings = Math.Round(s.OutputParsedFindings, 1),
                thinkingTp = Math.Round(s.ReasoningTruePositives, 1),
                outputTp = Math.Round(s.OutputTruePositives, 1),
                thinkingOnlyTp = Math.Round(s.ReasoningOnlyTruePositives, 1),
                outputOnlyTp = Math.Round(s.OutputOnlyTruePositives, 1),
                thinkingToOutputCoverage = s.ReasoningToOutputCoverage.HasValue ? Math.Round(s.ReasoningToOutputCoverage.Value * 100, 1) : (double?)null,
                criticalRecall = Math.Round(s.CriticalRecall * 100, 1),
                highRecall = Math.Round(s.HighRecall * 100, 1),
                mediumRecall = Math.Round(s.MediumRecall * 100, 1),
                lowRecall = Math.Round(s.LowRecall * 100, 1),
                highCriticalRecall = Math.Round(s.HighCriticalRecall * 100, 1),
                memorySafetyScore = Math.Round(s.MemorySafetyScore * 100, 1),
                concurrencyScore = Math.Round(s.ConcurrencyScore * 100, 1),
                injectionScore = Math.Round(s.InjectionScore * 100, 1),
                authCryptoScore = Math.Round(s.AuthCryptoScore * 100, 1),
                numericDosScore = Math.Round(s.NumericDosScore * 100, 1),
                fileIoScore = Math.Round(s.FileIoScore * 100, 1),
                cweCoverage = Math.Round(s.CweCoverage * 100, 1),
                stability = Math.Round(s.VulnerabilityStability * 100, 1),
                evidenceFidelity = Math.Round(s.EvidenceFidelity * 100, 1),
                locationAccuracy = Math.Round(s.LocationAccuracy * 100, 1),
                hallucinationRate = Math.Round(s.HallucinationRate * 100, 1),
                evaluationConfidence = Math.Round(s.EvaluationConfidence * 100, 1),
                falsePositiveTaxonomy = CountDictionary(s.FalsePositiveTaxonomy),
                fpRate = Math.Round(s.FpPerFinding * 100, 1),
                duplicateRate = Math.Round(s.DuplicateRate * 100, 1),
                ignoredRate = Math.Round(s.IgnoredLowConfidenceRate * 100, 1),
                parseSuccessRate = Math.Round(s.ParseSuccessRate * 100, 1),
                loopRate = Math.Round(s.LoopRate * 100, 1),
                emptyOutputRate = Math.Round(s.EmptyOutputRate * 100, 1),
                visibleReasoningRate = Math.Round(s.VisibleReasoningRate * 100, 1),
                run1Score = Math.Round(s.Run1Score, 2),
                run2Score = Math.Round(s.Run2Score, 2),
                run2Delta = Math.Round(s.Run2ScoreDelta, 2),
                run2FpReduction = Math.Round(s.Run2FpReduction, 2),
                run2TpRetention = Math.Round(s.Run2TpRetention * 100, 1),
                run2DroppedTpCount = Math.Round(s.Run2DroppedTpCount, 1),
                run2AddedTpCount = Math.Round(s.Run2AddedTpCount, 1),
                truthAuditRunCount = s.TruthAuditRunCount,
                accountabilityScore = Math.Round(s.AccountabilityScore, 2),
                truthAuditAccuracy = Math.Round(s.TruthAuditAccuracy * 100, 1),
                overclaimRate = Math.Round(s.OverclaimRate * 100, 1),
                missAdmissionRate = Math.Round(s.MissAdmissionRate * 100, 1),
                falsePositiveAdmissionRate = Math.Round(s.FalsePositiveAdmissionRate * 100, 1),
                evidenceLaunderingCount = Math.Round(s.EvidenceLaunderingCount, 1),
                quoteFidelity = Math.Round(s.QuoteFidelity * 100, 1),
                run2DroppedIds = s.Run2DroppedTruePositiveIds,
                run2AddedIds = s.Run2AddedTruePositiveIds,
                durationMeanSec = s.DurationMeanMs.HasValue ? Math.Round(s.DurationMeanMs.Value / 1000.0, 1) : (double?)null,
                durationMedianSec = s.DurationMedianMs.HasValue ? Math.Round(s.DurationMedianMs.Value / 1000.0, 1) : (double?)null,
                durationMinSec = s.DurationMinMs.HasValue ? Math.Round(s.DurationMinMs.Value / 1000.0, 1) : (double?)null,
                durationMaxSec = s.DurationMaxMs.HasValue ? Math.Round(s.DurationMaxMs.Value / 1000.0, 1) : (double?)null,
                severityRecall = PercentDictionary(s.SeverityRecall),
                categoryScores = PercentDictionary(s.CategoryScores),
                cweRecall = PercentDictionary(s.CweRecall),
                moduleScores = PercentDictionary(s.ModuleScores),
                perVuln = s.PerVulnerabilityCredit.Select(v => Math.Round(v, 3)),
                details = s.Details.Select(d => new
                {
                    recordId = d.RecordId,
                    benchmarkProfile = d.BenchmarkProfile,
                    scoringProfile = d.ScoringProfile,
                    scoringProfileVersion = d.ScoringProfileVersion,
                    isLegacyMigrated = d.IsLegacyMigrated,
                    isRescored = d.IsRescored,
                    officialComparable = d.OfficialComparable,
                    sourceHashMatches = d.SourceHashMatches,
                    runDirectory = d.RunDirectory,
                    runName = d.RunName,
                    startedAt = d.StartedAt?.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                    completedAt = d.CompletedAt?.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                    score = Math.Round(d.ScorePercent, 2),
                    run1Score = Math.Round(d.Run1Score, 2),
                    run2Score = Math.Round(d.Run2Score, 2),
                    run2Delta = Math.Round(d.Run2Delta, 2),
                    finishReason = d.FinishReason,
                    loopDetected = d.LoopDetected,
                    parseMode = d.ParseMode,
                    emptyOutputWithReasoning = d.EmptyOutputWithReasoning,
                    durationSec = d.DurationMs.HasValue ? Math.Round(d.DurationMs.Value / 1000.0, 1) : (double?)null,
                    responseChars = d.ResponseChars,
                    reasoningChars = d.ReasoningChars,
                    falsePositives = d.FalsePositives,
                    duplicates = d.Duplicates,
                    ignoredLowConfidence = d.IgnoredLowConfidence,
                    fullTruePositives = d.FullTruePositives,
                    partialTruePositives = d.PartialTruePositives,
                    missed = d.Missed,
                    repeatGroupId = d.RepeatGroupId,
                    repeatIndex = d.RepeatIndex,
                    repeatCount = d.RepeatCount,
                    hasVisibleReasoning = d.HasVisibleReasoning
                }),
                color = palette[i]
            })
        };

        var json = JsonSerializer.Serialize(payload, PayloadOptions);
        var builder = new StringBuilder();
        builder.Append("""
<!DOCTYPE html>
<html lang="de">
<head>
<meta charset="utf-8" />
<meta name="viewport" content="width=device-width, initial-scale=1" />
<title>SuperCalc Benchmark — Modellvergleich</title>
<style>
  :root { color-scheme: light dark; --bg:#f6f7f9; --fg:#1d2129; --muted:#6b7280; --card:#fff; --line:#e3e6ea; --soft:#f3f4f6; --accent:#2563eb; }
  @media (prefers-color-scheme: dark) { :root { --bg:#0f172a; --fg:#e5e7eb; --muted:#94a3b8; --card:#111827; --line:#253044; --soft:#1f2937; --accent:#60a5fa; } }
  * { box-sizing: border-box; }
  body { font-family:"Segoe UI",system-ui,sans-serif; margin:0; padding:24px; background:var(--bg); color:var(--fg); }
  h1 { font-size:24px; margin:0 0 4px; }
  h2 { font-size:16px; margin:0 0 12px; }
  h3 { font-size:14px; margin:14px 0 8px; }
  .meta,.note { color:var(--muted); font-size:12px; }
  .card { background:var(--card); border:1px solid var(--line); border-radius:12px; padding:16px; margin-bottom:18px; box-shadow:0 1px 2px rgba(0,0,0,.04); }
  .card-heading { display:flex; align-items:center; gap:8px; margin:0 0 12px; }
  .metric-title { background:transparent; color:var(--fg); border:0; padding:0; font-weight:650; text-align:left; cursor:zoom-in; }
  .metric-help { width:22px; height:22px; border-radius:50%; padding:0; display:inline-flex; align-items:center; justify-content:center; font-weight:700; background:var(--soft); color:var(--accent); border:1px solid var(--line); }
  .overlay-backdrop { position:fixed; inset:0; z-index:50; background:rgba(15,23,42,.58); backdrop-filter:blur(3px); display:flex; align-items:center; justify-content:center; padding:24px; }
  .overlay-dialog { background:var(--card); color:var(--fg); border:1px solid var(--line); border-radius:16px; width:min(1180px,96vw); max-height:92vh; overflow:auto; box-shadow:0 24px 70px rgba(0,0,0,.35); padding:18px; }
  .overlay-dialog.metric-modal { width:min(1420px,98vw); }
  .overlay-dialog .chart-box { height:min(72vh,760px); }
  .overlay-close { float:right; width:34px; height:34px; border-radius:999px; padding:0; margin-left:10px; }
  .metric-card.in-modal { margin:0; border:0; box-shadow:none; padding:0; }
  .grid { display:grid; grid-template-columns:1fr; gap:18px; }
  @media (min-width:1100px) { .grid.two { grid-template-columns:1fr 1fr; } .grid.three { grid-template-columns:repeat(3,1fr); } }
  .chart-box { position:relative; height:420px; }
  .filters { display:grid; grid-template-columns:repeat(auto-fit,minmax(180px,1fr)); gap:10px; align-items:end; }
  label { font-size:12px; color:var(--muted); display:flex; flex-direction:column; gap:4px; }
  label.inline { flex-direction:row; align-items:center; gap:6px; color:var(--fg); }
  input,select,button { font:inherit; border:1px solid var(--line); border-radius:8px; padding:7px 9px; background:var(--card); color:var(--fg); }
  select[multiple] { min-height:82px; }
  button { cursor:pointer; background:var(--accent); color:white; border-color:var(--accent); }
  button.secondary { background:var(--soft); color:var(--fg); border-color:var(--line); }
  .checks { display:flex; flex-wrap:wrap; gap:10px 16px; margin-top:10px; }
  table { border-collapse:collapse; width:100%; font-size:12px; }
  th,td { padding:7px 9px; border-bottom:1px solid var(--line); text-align:right; white-space:nowrap; vertical-align:top; }
  th:first-child,td:first-child { text-align:left; }
  th { cursor:pointer; user-select:none; background:var(--soft); position:sticky; top:0; z-index:1; }
  th.sorted-asc::after { content:" ▲"; } th.sorted-desc::after { content:" ▼"; }
  tr.swatch td:first-child::before { content:""; display:inline-block; width:11px; height:11px; border-radius:3px; margin-right:8px; vertical-align:-1px; background:var(--swatch); }
  .table-wrap { overflow:auto; max-height:720px; border:1px solid var(--line); border-radius:10px; }
  .empty { padding:40px; text-align:center; color:var(--muted); }
  code { background:var(--soft); padding:1px 5px; border-radius:4px; }
  .pill { display:inline-block; padding:2px 7px; border-radius:999px; background:var(--soft); margin:1px; color:var(--fg); }
  .heatmap { overflow:auto; }
  .heatmap td { min-width:54px; text-align:center; font-variant-numeric:tabular-nums; }
  .heatmap th { writing-mode:vertical-rl; transform:rotate(180deg); min-width:34px; max-width:48px; height:135px; vertical-align:bottom; }
  .heat-0 { background:#ef444422; } .heat-50 { background:#f59e0b66; } .heat-100 { background:#22c55e88; } .heat-neg { background:#ef444488; } .heat-pos { background:#22c55e88; }
  details summary { cursor:pointer; color:var(--accent); }
  .detail-table { margin-top:8px; font-size:11px; }
</style>
</head>
<body>
<h1>SuperCalc Benchmark — Modellvergleich</h1>
<div class="meta" id="meta"></div>
<div id="content"></div>
""");
        builder.Append("<script src=\"");
        builder.Append(HtmlEscape(ChartJsCdn));
        builder.Append("""
"></script>
<script id="data" type="application/json">
""");
        builder.Append(json);
        builder.Append("""
</script>
<script>
(function () {
  const data = JSON.parse(document.getElementById("data").textContent);
  const meta = document.getElementById("meta");
  const content = document.getElementById("content");
  const charts = {};
  let sortKey = "score";
  let sortDir = -1;

  meta.textContent = `Benchmark: ${data.benchmarkId} · erzeugt ${data.generatedAt} · Aggregation: ${data.aggregate} · Run-Sicht: ${data.runView} · Scoring-Profil: ${data.scoringProfile || "alle"} · ${data.series.length} Modell(e)/Quants · ${data.axis.length} Schwachstellen`;
  if (!data.series.length) {
    content.innerHTML = '<div class="card empty">Noch keine archivierten Runs gefunden. Starte einen Benchmark, danach erscheinen hier die Vergleiche.</div>';
    return;
  }

  const families = uniq(data.series.map(s => s.family));
  const quants = uniq(data.series.map(s => s.quant));
  const severities = uniq(data.axis.map(a => a.severity).filter(Boolean));
  const categories = uniq(data.axis.map(a => a.category).filter(Boolean));
  const cwes = uniq(data.axis.flatMap(a => a.cwe || []));
  const hasChart = typeof Chart !== "undefined";
  const hasReasoningStats = data.series.some(s => s.visibleReasoningRuns > 0);
  const metricHelp = {
    mainMetric:{title:"Hauptmetrik", body:"Zeigt die aktuell ausgewählte Vergleichsmetrik pro Modell/Quant. Datenbasis sind archivierte Scorecards nach aktivem Run-View und Scoring-Profil. Fehlerbalken und Tooltipps verwenden Score-Verteilung derselben Gruppe; bei wenigen Runs ist die Unsicherheit nur deskriptiv."},
    severityRecall:{title:"Severity-Recall", body:"Zeigt Recall/Credit getrennt nach Severity-Buckets. Full TP zählt 1.0, Partial TP 0.5, missed 0. Die Werte werden über die Vulnerability-Achse der lokalen Ground Truth aggregiert; kleine Buckets können stark schwanken."},
    vulnerabilityRadar:{title:"Einzelwerte je Schwachstelle", body:"Radar über einzelne Ground-Truth-IDs. Jede Achse ist ein Finding-Credit: 1 voll gefunden, 0.5 teilweise, 0 verpasst. Im Delta-Modus sind negative Werte Run-2-Verschlechterungen."},
    run2Delta:{title:"Run 1 → Run 2", body:"Vergleicht Blind-Analyse und Self-Validation. Steigende Linien bedeuten Score-Verbesserung, fallende Linien Over-Pruning oder schlechtere Finalisierung. Truth-Audit wird hier nicht eingerechnet."},
    qualityHealth:{title:"Qualitäts-/Parsing-Gesundheit", body:"Diagnosechart für False Positives, Duplikate, ignorierte Low-Confidence-Findings, Hallucination Rate, Evidence/Location-Fidelity, Loop- und Parse-Probleme. Diese Metriken erklären Score-Unterschiede, sind aber nicht alle direkte Score-Komponenten."},
    reasoningCoverage:{title:"Denken-vs-Sagen", body:"Vergleicht sichtbares reasoning_content bzw. <think>-Blöcke mit finalem Output. Das ist nur Diagnostik und zählt nicht zum offiziellen Score; unstrukturierte Gedanken können unterzählt werden."},
    heatmap:{title:"Vulnerability Heatmap", body:"Matrix aus Modell/Quant gegen Ground-Truth-ID. Grün bedeutet erkannt, orange teilweise, rot/verblasst verpasst. Im Delta-Modus zeigt grün Verbesserung durch Run 2, rot Verschlechterung."},
    overview:{title:"Übersicht", body:"Tabellarischer Drilldown pro Modell/Quant mit Score-Versionen, Run-Details, Repeat-Metadaten, Parse-/Loop-Hinweisen, FP-Taxonomie und Run-2-Änderungen. Sortierbar über Spaltenköpfe."}
  };

  content.innerHTML = `
    <div class="card">
      <h2>Filter & Metrik</h2>
      <div class="filters">
        <label>Suche<input id="q" placeholder="Modell, Familie, Quant" /></label>
        <label>Familie<select id="family" multiple></select></label>
        <label>Quant<select id="quant" multiple></select></label>
        <label>Severity<select id="severity" multiple></select></label>
        <label>Kategorie<select id="category" multiple></select></label>
        <label>CWE<select id="cwe" multiple></select></label>
        <label>Metrik<select id="metric">
          <option value="score">Gesamt-Score</option><option value="criticalRecall">Critical Recall</option><option value="highCriticalRecall">High+Critical Recall</option>
          <option value="f1">F1</option><option value="fpRate">FP-Rate</option><option value="stability">Stability</option><option value="run2Delta">Run2-Delta</option>
          <option value="thinkingCoverage">Thinking Coverage</option><option value="evidenceFidelity">Evidence Fidelity</option><option value="locationAccuracy">Location Accuracy</option>
          <option value="hallucinationRate">Hallucination Rate</option><option value="evaluationConfidence">Evaluation Confidence</option><option value="accountability">Truth-Audit Accountability</option><option value="overclaimRate">Overclaim Rate</option><option value="duration">Duration</option>
        </select></label>
        <label>Run-Sicht<select id="runView"><option value="primary">Primary</option><option value="run1">Run 1</option><option value="run2">Run 2</option><option value="delta">Run2 - Run1 Delta</option></select></label>
        <label>Score min<input id="minScore" type="number" min="-100" max="100" step="1" /></label>
        <label>Score max<input id="maxScore" type="number" min="-100" max="100" step="1" /></label>
        <label>Min Runs<input id="minRuns" type="number" min="1" step="1" /></label>
        <label>Max σ<input id="maxStd" type="number" min="0" step="1" /></label>
        <label>Max FP<input id="maxFp" type="number" min="0" step="1" /></label>
        <label>Max Hallucination %<input id="maxHallucination" type="number" min="0" max="100" step="1" /></label>
        <label>Min Critical %<input id="minCritical" type="number" min="0" max="100" step="1" /></label>
        <label>Top-N Charts<input id="topN" type="number" min="1" step="1" value="24" /></label>
      </div>
      <div class="checks">
        <label class="inline"><input id="onlyOfficial" type="checkbox" /> nur offizielle Runs</label>
        <label class="inline"><input id="onlyHash" type="checkbox" checked /> nur Source-Hash-Matches</label>
        <label class="inline"><input id="noLoop" type="checkbox" /> nur ohne Loop-Abbruch</label>
        <label class="inline"><input id="withReasoning" type="checkbox" /> nur mit sichtbarem Reasoning</label>
        <label class="inline"><input id="repeated" type="checkbox" /> nur Gruppen mit N ≥ 2</label>
        <label class="inline"><input id="hideUnknown" type="checkbox" /> unknown-quant ausblenden</label>
        <label class="inline"><input id="fillToggle" type="checkbox" /> Radarflächen füllen</label>
        <button id="csvButton" type="button">Gefilterte CSV exportieren</button>
        <button id="resetButton" type="button" class="secondary">Filter zurücksetzen</button>
      </div>
      <div id="summary" class="note" style="margin-top:10px"></div>
    </div>
    ${hasChart ? `
      <div class="grid two">
        <div class="card metric-card" data-metric-id="mainMetric">${metricHeader("mainMetric","Hauptmetrik", "barTitle")}<div class="note">Fehlerbalken zeigen Score-Min/Max der Gruppe; Metrik und Run-Sicht sind clientseitig umschaltbar.</div><div class="chart-box"><canvas id="barChart"></canvas></div></div>
        <div class="card metric-card" data-metric-id="severityRecall">${metricHeader("severityRecall","Severity-Recall")}<div class="chart-box"><canvas id="severityChart"></canvas></div></div>
      </div>
      <div class="grid two">
        <div class="card metric-card" data-metric-id="vulnerabilityRadar">${metricHeader("vulnerabilityRadar","Einzelwerte je Schwachstelle (Netz)")}<div class="chart-box"><canvas id="radarChart"></canvas></div></div>
        <div class="card metric-card" data-metric-id="run2Delta">${metricHeader("run2Delta","Run 1 → Run 2")}<div class="chart-box"><canvas id="slopeChart"></canvas></div></div>
      </div>
      <div class="grid two">
        <div class="card metric-card" data-metric-id="qualityHealth">${metricHeader("qualityHealth","Qualitäts-/Parsing-Gesundheit")}<div class="chart-box"><canvas id="qualityChart"></canvas></div></div>
        ${hasReasoningStats ? '<div class="card metric-card" data-metric-id="reasoningCoverage">'+metricHeader("reasoningCoverage","Denken-vs-Sagen")+'<div class="note">Diagnostik aus sichtbarem <code>reasoning_content</code> / <code>&lt;think&gt;</code>; nicht Teil des Scores.</div><div class="chart-box"><canvas id="reasoningChart"></canvas></div></div>' : '<div class="card metric-card" data-metric-id="reasoningCoverage">'+metricHeader("reasoningCoverage","Denken-vs-Sagen")+'<div class="empty">Keine sichtbaren Reasoning-Daten in den gefilterten Scorecards.</div></div>'}
      </div>` : '<div class="card note">Chart.js konnte nicht geladen werden (keine Internetverbindung?). Heatmap, Filter und Tabelle funktionieren weiterhin offline.</div>'}
    <div class="card metric-card" data-metric-id="heatmap">${metricHeader("heatmap","Vulnerability Heatmap")}<div class="note">0 = verpasst, 0.5 = teilweise, 1 = voll erkannt. Bei Delta: grün = Run 2 besser, rot = schlechter.</div><div id="heatmap" class="heatmap"></div></div>
    <div class="card metric-card" data-metric-id="overview">${metricHeader("overview","Übersicht")}<div id="tableWrap" class="table-wrap"></div><div class="note">Details pro Modell aufklappen. Archivscorecards enthalten keine Prompts oder Rohantworten, nur kompakte Bewertungs-/Diagnosedaten und Run-Ordner-Referenzen.</div></div>`;

  fillSelect("family", families);
  fillSelect("quant", quants);
  fillSelect("severity", severities);
  fillSelect("category", categories);
  fillSelect("cwe", cwes);
  document.getElementById("metric").value = data.metric || "score";
  document.getElementById("runView").value = (data.runView || "primary").toLowerCase();

  const inputs = ["q","family","quant","severity","category","cwe","metric","runView","minScore","maxScore","minRuns","maxStd","maxFp","maxHallucination","minCritical","topN","onlyOfficial","onlyHash","noLoop","withReasoning","repeated","hideUnknown","fillToggle"];
  inputs.forEach(id => document.getElementById(id)?.addEventListener("input", render));
  inputs.forEach(id => document.getElementById(id)?.addEventListener("change", render));
  document.getElementById("csvButton").addEventListener("click", exportFilteredCsv);
  wireMetricInteractions();
  document.getElementById("resetButton").addEventListener("click", () => { document.querySelectorAll("input").forEach(i => { if (i.type === "checkbox") i.checked = i.id === "onlyHash"; else if (i.id !== "topN") i.value = ""; }); document.getElementById("topN").value = "24"; document.querySelectorAll("select[multiple]").forEach(s => [...s.options].forEach(o => o.selected = false)); document.getElementById("metric").value = "score"; document.getElementById("runView").value = (data.runView || "primary").toLowerCase(); render(); });

  function render() {
    const state = readState();
    const axisIdx = filteredAxisIndices(state);
    const rows = data.series.filter(s => includeSeries(s, state)).sort((a,b) => metricValue(b,state) - metricValue(a,state) || a.label.localeCompare(b.label));
    const chartRows = rows.slice(0, Math.max(1, state.topN || 24));
    document.getElementById("summary").textContent = `${rows.length} von ${data.series.length} Gruppen sichtbar · ${axisIdx.length} von ${data.axis.length} Schwachstellenachsen im Heatmap/Radar-Filter`;
    renderTable(rows, state);
    renderHeatmap(rows, axisIdx, state);
    if (hasChart) renderCharts(chartRows, axisIdx, state);
  }

  function readState() {
    return {
      q: document.getElementById("q").value.trim().toLowerCase(),
      families: selected("family"), quants: selected("quant"), severities: selected("severity"), categories: selected("category"), cwes: selected("cwe"),
      metric: document.getElementById("metric").value, runView: document.getElementById("runView").value,
      minScore: num("minScore"), maxScore: num("maxScore"), minRuns: num("minRuns"), maxStd: num("maxStd"), maxFp: num("maxFp"), maxHallucination: num("maxHallucination"), minCritical: num("minCritical"), topN: num("topN"),
      onlyOfficial: checked("onlyOfficial"), onlyHash: checked("onlyHash"), noLoop: checked("noLoop"), withReasoning: checked("withReasoning"), repeated: checked("repeated"), hideUnknown: checked("hideUnknown"), fill: checked("fillToggle")
    };
  }

  function includeSeries(s, st) {
    const hay = `${s.label} ${s.family} ${s.quant}`.toLowerCase();
    if (st.q && !hay.includes(st.q)) return false;
    if (st.families.length && !st.families.includes(s.family)) return false;
    if (st.quants.length && !st.quants.includes(s.quant)) return false;
    const score = scoreForRunView(s, st.runView);
    if (st.minScore !== null && score < st.minScore) return false;
    if (st.maxScore !== null && score > st.maxScore) return false;
    if (st.minRuns !== null && s.runCount < st.minRuns) return false;
    if (st.maxStd !== null && s.scoreStdDev > st.maxStd) return false;
    if (st.maxFp !== null && s.falsePositives > st.maxFp) return false;
    if (st.maxHallucination !== null && s.hallucinationRate > st.maxHallucination) return false;
    if (st.minCritical !== null && s.criticalRecall < st.minCritical) return false;
    if (st.onlyOfficial && s.officialRunCount !== s.runCount) return false;
    if (st.onlyHash && s.sourceHashMatchCount !== s.runCount) return false;
    if (st.noLoop && s.loopRate > 0) return false;
    if (st.withReasoning && s.visibleReasoningRuns === 0) return false;
    if (st.repeated && s.runCount < 2) return false;
    if (st.hideUnknown && String(s.quant).toLowerCase() === "unknown-quant") return false;
    return true;
  }

  function filteredAxisIndices(st) {
    const idx = [];
    data.axis.forEach((a,i) => {
      if (st.severities.length && !st.severities.includes(a.severity)) return;
      if (st.categories.length && !st.categories.includes(a.category)) return;
      if (st.cwes.length && !(a.cwe || []).some(c => st.cwes.includes(c))) return;
      idx.push(i);
    });
    return idx;
  }

  function metricValue(s, st) {
    switch (st.metric) {
      case "criticalRecall": return s.criticalRecall ?? 0;
      case "highCriticalRecall": return s.highCriticalRecall ?? 0;
      case "f1": return s.f1 ?? 0;
      case "fpRate": return s.fpRate ?? 0;
      case "stability": return s.stability ?? 0;
      case "run2Delta": return s.run2Delta ?? 0;
      case "thinkingCoverage": return s.thinkingToOutputCoverage ?? 0;
      case "evidenceFidelity": return s.evidenceFidelity ?? 0;
      case "locationAccuracy": return s.locationAccuracy ?? 0;
      case "hallucinationRate": return s.hallucinationRate ?? 0;
      case "evaluationConfidence": return s.evaluationConfidence ?? 0;
      case "accountability": return s.accountabilityScore ?? 0;
      case "overclaimRate": return s.overclaimRate ?? 0;
      case "duration": return s.durationMedianSec ?? s.durationMeanSec ?? 0;
      default: return scoreForRunView(s, st.runView);
    }
  }
  function scoreForRunView(s, runView) { if (runView === "run1") return s.run1Score || s.score; if (runView === "run2") return s.run2Score || s.score; if (runView === "delta") return s.run2Delta || 0; return s.score; }
  function metricLabel(st) { return ({score:"Gesamt-Score",criticalRecall:"Critical Recall %",highCriticalRecall:"High+Critical Recall %",f1:"F1 %",fpRate:"FP-Rate %",stability:"Stability %",run2Delta:"Run2-Delta",thinkingCoverage:"Thinking Coverage %",evidenceFidelity:"Evidence Fidelity %",locationAccuracy:"Location Accuracy %",hallucinationRate:"Hallucination Rate %",evaluationConfidence:"Evaluation Confidence %",accountability:"Truth-Audit Accountability",overclaimRate:"Overclaim Rate %",duration:"Duration sec"})[st.metric] || "Metrik"; }

  function renderCharts(rows, axisIdx, st) {
    const metricName = metricLabel(st);
    document.getElementById("barTitle").textContent = metricName;
    updateChart("barChart", {
      type:"bar",
      data:{ labels:rows.map(s=>s.label), datasets:[{ label:metricName, data:rows.map(s=>metricValue(s,st)), backgroundColor:rows.map(s=>s.color+"cc"), borderColor:rows.map(s=>s.color), borderWidth:1 }]},
      options:{ indexAxis:"y", responsive:true, maintainAspectRatio:false, scales:{ x:{ beginAtZero: st.runView !== "delta", title:{display:true,text:metricName} }}, plugins:{ legend:{display:false}, tooltip:{callbacks:{afterBody:items=>{ const s=rows[items[0].dataIndex]; return [`Runs: ${s.runCount}`,`Score Median/Mittel: ${fmt(s.scoreMedian)} / ${fmt(s.scoreMean)} · σ ${fmt(s.scoreStdDev)} · IQR ${fmt(s.scoreIqr)}`,`Run1→Run2: ${fmt(s.run1Score)} → ${fmt(s.run2Score)} (${fmtSigned(s.run2Delta)})`]; }}}}}
    });
    updateChart("severityChart", { type:"bar", data:{ labels:rows.map(s=>s.label), datasets:[
      {label:"Critical",data:rows.map(s=>s.criticalRecall),backgroundColor:"#dc2626cc"},{label:"High",data:rows.map(s=>s.highRecall),backgroundColor:"#f97316cc"},{label:"Medium",data:rows.map(s=>s.mediumRecall),backgroundColor:"#eab308cc"},{label:"Low",data:rows.map(s=>s.lowRecall),backgroundColor:"#22c55ecc"}]},
      options:{ indexAxis:"y", responsive:true, maintainAspectRatio:false, scales:{x:{min:0,max:100,title:{display:true,text:"Recall/Credit %"}}} }});
    const radarRows = rows.slice(0, Math.min(10, rows.length));
    const labels = axisIdx.map(i => data.axis[i].id);
    updateChart("radarChart", { type:"radar", data:{ labels, datasets:radarRows.map(s=>({ label:s.label, data:axisIdx.map(i=>s.perVuln[i] ?? 0), borderColor:s.color, backgroundColor:s.color+"33", fill:st.fill, pointRadius:2, borderWidth:2 }))}, options:{ responsive:true, maintainAspectRatio:false, scales:{ r:{ min: st.runView === "delta" ? -1 : 0, max:1, ticks:{stepSize:0.5,showLabelBackdrop:false}, pointLabels:{font:{size:10}}}}, plugins:{legend:{position:"bottom",labels:{boxWidth:12,font:{size:11}}}} }});
    const slopeRows = rows.filter(s => s.run2Score || s.run1Score).slice(0, 12);
    updateChart("slopeChart", { type:"line", data:{ labels:["Run 1","Run 2"], datasets:slopeRows.map(s=>({ label:s.label, data:[s.run1Score,s.run2Score], borderColor:s.color, backgroundColor:s.color, tension:0.15 }))}, options:{ responsive:true, maintainAspectRatio:false, scales:{ y:{ beginAtZero:true, max:100, title:{display:true,text:"Score"}}}, plugins:{legend:{position:"bottom",labels:{boxWidth:12,font:{size:11}}}} }});
    updateChart("qualityChart", { type:"bar", data:{ labels:rows.map(s=>s.label), datasets:[
      {label:"FP",data:rows.map(s=>s.falsePositives),backgroundColor:"#ef4444cc"},{label:"Duplicates",data:rows.map(s=>s.duplicates),backgroundColor:"#f59e0bcc"},{label:"Ignored",data:rows.map(s=>s.ignoredLowConfidence),backgroundColor:"#64748bcc"},{label:"Hallucination %",data:rows.map(s=>s.hallucinationRate),backgroundColor:"#fb7185aa"},{label:"Evidence %",data:rows.map(s=>s.evidenceFidelity),backgroundColor:"#22c55e99"},{label:"Location %",data:rows.map(s=>s.locationAccuracy),backgroundColor:"#14b8a699"},{label:"Loop %",data:rows.map(s=>s.loopRate),backgroundColor:"#a855f7aa"},{label:"Parse fail %",data:rows.map(s=>100-(s.parseSuccessRate||0)),backgroundColor:"#0ea5e9aa"}]}, options:{ indexAxis:"y", responsive:true, maintainAspectRatio:false, scales:{x:{beginAtZero:true}}, plugins:{legend:{position:"bottom"}} }});
    if (hasReasoningStats && document.getElementById("reasoningChart")) updateChart("reasoningChart", { type:"bar", data:{ labels:rows.map(s=>s.label), datasets:[{label:"Gedacht TP",data:rows.map(s=>s.thinkingTp),backgroundColor:"#6366f1cc"},{label:"Gesagt TP",data:rows.map(s=>s.outputTp),backgroundColor:"#10b981cc"},{label:"Nur gedacht",data:rows.map(s=>s.thinkingOnlyTp),backgroundColor:"#f59e0bcc"}]}, options:{ indexAxis:"y", responsive:true, maintainAspectRatio:false, scales:{x:{beginAtZero:true}}, plugins:{legend:{position:"bottom"}} }});
  }

  function renderHeatmap(rows, axisIdx, st) {
    if (!axisIdx.length) { document.getElementById("heatmap").innerHTML = '<div class="empty">Keine Schwachstellenachsen im Filter.</div>'; return; }
    let html = '<table><thead><tr><th>Modell · Quant</th>' + axisIdx.map(i => `<th title="${esc(axisTitle(data.axis[i]))}">${esc(data.axis[i].id)}</th>`).join("") + '</tr></thead><tbody>';
    rows.forEach(s => { html += `<tr class="swatch" style="--swatch:${s.color}"><td>${esc(s.label)}</td>`; axisIdx.forEach(i => { const v = s.perVuln[i] ?? 0; html += `<td class="${heatClass(v, st.runView)}" title="${esc(s.label)} · ${esc(axisTitle(data.axis[i]))}: ${fmt(v)}">${fmt(v)}</td>`; }); html += '</tr>'; });
    html += '</tbody></table>'; document.getElementById("heatmap").innerHTML = html;
  }

  const cols = [
    {key:"label",title:"Modell · Quant",kind:"detail"},{key:"runCount",title:"Runs",kind:"num"},{key:"score",title:"Score",kind:"num"},{key:"criticalRecall",title:"Critical %",kind:"num"},{key:"highCriticalRecall",title:"High+Crit %",kind:"num"},{key:"evidenceFidelity",title:"Evidence %",kind:"num"},{key:"locationAccuracy",title:"Location %",kind:"num"},{key:"hallucinationRate",title:"Hallucination %",kind:"num"},{key:"stability",title:"Stability %",kind:"num"},{key:"run2Delta",title:"Run2 Δ",kind:"num"},{key:"truthAuditRunCount",title:"Audit Runs",kind:"num"},{key:"accountabilityScore",title:"Audit",kind:"num"},{key:"truthAuditAccuracy",title:"Audit Acc %",kind:"num"},{key:"overclaimRate",title:"Overclaim %",kind:"num"},{key:"missAdmissionRate",title:"Miss Admit %",kind:"num"},{key:"falsePositiveAdmissionRate",title:"FP Admit %",kind:"num"},{key:"quoteFidelity",title:"Quote %",kind:"num"},{key:"evidenceLaunderingCount",title:"Launder",kind:"num"},{key:"scoreMedian",title:"Median",kind:"num"},{key:"scoreStdDev",title:"±σ",kind:"num"},{key:"scoreIqr",title:"IQR",kind:"num"},{key:"precision",title:"Precision %",kind:"num"},{key:"recall",title:"Recall %",kind:"num"},{key:"f1",title:"F1 %",kind:"num"},{key:"fullTp",title:"Full TP",kind:"num"},{key:"partialTp",title:"Partial",kind:"num"},{key:"falsePositives",title:"FP",kind:"num"},{key:"duplicates",title:"Dup",kind:"num"},{key:"missed",title:"Missed",kind:"num"},{key:"parseSuccessRate",title:"Parse %",kind:"num"},{key:"loopRate",title:"Loop %",kind:"num"},{key:"durationMedianSec",title:"Dur s",kind:"num"},{key:"thinkingToOutputCoverage",title:"Think→Out %",kind:"num"}
  ];
  function renderTable(rows, st) {
    rows = rows.slice().sort((a,b) => { const av = valueForSort(a,sortKey,st), bv = valueForSort(b,sortKey,st); if (typeof av === "number" || typeof bv === "number") return ((av ?? -Infinity) - (bv ?? -Infinity))*sortDir; return String(av??"").localeCompare(String(bv??""))*sortDir; });
    let html = '<table><thead><tr>' + cols.map(c => `<th class="${c.key===sortKey?(sortDir===1?'sorted-asc':'sorted-desc'):''}" data-key="${c.key}">${c.title}</th>`).join("") + '</tr></thead><tbody>';
    rows.forEach(s => { html += `<tr class="swatch" style="--swatch:${s.color}">`; cols.forEach(c => { html += `<td>${cell(s,c,st)}</td>`; }); html += '</tr>'; });
    html += '</tbody></table>'; document.getElementById("tableWrap").innerHTML = html;
    document.querySelectorAll("th[data-key]").forEach(th => th.addEventListener("click", () => { const k = th.getAttribute("data-key"); if (k === sortKey) sortDir = -sortDir; else { sortKey = k; sortDir = k === "label" ? 1 : -1; } render(); }));
  }
  const auditMetricKeys = new Set(["accountabilityScore","truthAuditAccuracy","overclaimRate","missAdmissionRate","falsePositiveAdmissionRate","quoteFidelity","evidenceLaunderingCount"]);
  function cell(s,c,st) { if (c.kind === "detail") return detailCell(s); if (auditMetricKeys.has(c.key) && !(s.truthAuditRunCount > 0)) return "n/a"; const v = valueForSort(s,c.key,st); return typeof v === "number" && Number.isFinite(v) ? (Number.isInteger(v) ? v : v.toFixed(1)) : "n/a"; }
  function detailCell(s) { const details = s.details || []; const rows = details.map(d => { const version = `${esc(d.scoringProfile||"legacy-unknown")} v${d.scoringProfileVersion||"?"}${d.isLegacyMigrated ? " · legacy-migriert" : ""}${d.isRescored ? " · rescored" : ""}`; return `<tr><td>${esc(d.completedAt||"")}</td><td>${esc(d.runName)}</td><td>${version}</td><td>${d.officialComparable?'ja':'nein'}</td><td>${fmt(d.score)}</td><td>${fmtSigned(d.run2Delta)}</td><td>${esc(d.finishReason||"")}</td><td>${esc(d.parseMode||"")}</td><td>${d.loopDetected?'ja':'nein'}</td><td>${fmt(d.durationSec)}</td><td>${d.responseChars}/${d.reasoningChars}</td><td>${esc(d.repeatGroupId||'—')} ${d.repeatCount>1 ? '('+d.repeatIndex+'/'+d.repeatCount+')' : ''}</td></tr>`; }).join(""); const fpTax = Object.entries(s.falsePositiveTaxonomy||{}).sort((a,b)=>b[1]-a[1]).map(([k,v])=>`${esc(k)}=${fmt(v)}`).join(', ') || '—'; return `<details><summary>${esc(s.label)}</summary><div class="note">Profil: ${s.officialRunCount}/${s.runCount} offiziell · official comparable: ${s.officialComparableRunCount}/${s.runCount} · legacy-migriert: ${s.legacyMigratedRunCount}/${s.runCount} · rescored: ${s.rescoredRunCount}/${s.runCount} · Source-Hash: ${s.sourceHashMatchCount}/${s.runCount} · FP-Taxonomie: ${fpTax} · Run2 dropped: ${(s.run2DroppedIds||[]).join(', ')||'—'} · added: ${(s.run2AddedIds||[]).join(', ')||'—'}</div><table class="detail-table"><thead><tr><th>Datum</th><th>Run</th><th>Score-Version</th><th>Official</th><th>Score</th><th>Run2 Δ</th><th>Finish</th><th>Parse</th><th>Loop</th><th>s</th><th>Out/Think chars</th><th>Repeat</th></tr></thead><tbody>${rows}</tbody></table></details>`; }
  function valueForSort(s,key,st) { if (key === "score") return scoreForRunView(s, st.runView); return s[key]; }

  function metricHeader(metricId, title, titleId) {
    const idAttr = titleId ? ` id="${esc(titleId)}"` : "";
    return `<div class="card-heading"><button${idAttr} type="button" class="metric-title" data-modal-metric="${esc(metricId)}" aria-label="${esc(title)} maximieren">${esc(title)}</button><button type="button" class="metric-help" data-help-metric="${esc(metricId)}" aria-label="Hilfe zu ${esc(title)}">?</button></div>`;
  }

  function wireMetricInteractions() {
    document.querySelectorAll("[data-modal-metric]").forEach(btn => btn.addEventListener("click", ev => { ev.stopPropagation(); openMetricModal(btn.getAttribute("data-modal-metric")); }));
    document.querySelectorAll("[data-help-metric]").forEach(btn => btn.addEventListener("click", ev => { ev.stopPropagation(); openHelpPopover(btn.getAttribute("data-help-metric")); }));
  }

  let activeOverlay = null;
  let activePlaceholder = null;
  let activeMovedCard = null;
  let lastFocusedElement = null;
  function openMetricModal(metricId) {
    if (activeOverlay) closeOverlay();
    const card = [...document.querySelectorAll(".metric-card")].find(el => el.getAttribute("data-metric-id") === metricId);
    if (!card) return;
    lastFocusedElement = document.activeElement;
    activePlaceholder = document.createComment(`metric-${metricId}-placeholder`);
    card.parentNode.insertBefore(activePlaceholder, card);
    activeMovedCard = card;
    const overlay = createOverlay("metric-modal");
    const close = overlay.querySelector(".overlay-close");
    const body = overlay.querySelector(".overlay-body");
    body.appendChild(card);
    card.classList.add("in-modal");
    document.body.appendChild(overlay);
    activeOverlay = overlay;
    close.focus();
    resizeChartsSoon();
  }

  function openHelpPopover(metricId) {
    if (activeOverlay) closeOverlay();
    const help = metricHelp[metricId] || {title:"Metrik", body:"Keine Detailbeschreibung verfügbar."};
    lastFocusedElement = document.activeElement;
    const overlay = createOverlay("help-popover");
    overlay.querySelector(".overlay-body").innerHTML = `<h2>${esc(help.title)}</h2><p>${esc(help.body)}</p><div class="note">Schließen mit Esc, Klick außerhalb oder X. Alle Werte stammen aus lokalen Archiv-Scorecards; Ground Truth wird nur offline zur Aggregation verwendet.</div>`;
    document.body.appendChild(overlay);
    activeOverlay = overlay;
    overlay.querySelector(".overlay-close").focus();
  }

  function createOverlay(extraClass) {
    const overlay = document.createElement("div");
    overlay.className = "overlay-backdrop";
    overlay.innerHTML = `<div class="overlay-dialog ${extraClass}" role="dialog" aria-modal="true" tabindex="-1"><button type="button" class="overlay-close" aria-label="Overlay schließen">×</button><div class="overlay-body"></div></div>`;
    overlay.addEventListener("click", ev => { if (ev.target === overlay) closeOverlay(); });
    overlay.querySelector(".overlay-close").addEventListener("click", closeOverlay);
    return overlay;
  }

  function closeOverlay() {
    if (!activeOverlay) return;
    if (activeMovedCard && activePlaceholder) {
      activeMovedCard.classList.remove("in-modal");
      activePlaceholder.parentNode.insertBefore(activeMovedCard, activePlaceholder);
      activePlaceholder.remove();
    }
    activeOverlay.remove();
    activeOverlay = null; activePlaceholder = null; activeMovedCard = null;
    if (lastFocusedElement && typeof lastFocusedElement.focus === "function") lastFocusedElement.focus();
    resizeChartsSoon();
  }

  document.addEventListener("keydown", ev => { if (ev.key === "Escape" && activeOverlay) closeOverlay(); });
  function resizeChartsSoon() { setTimeout(() => Object.values(charts).forEach(chart => chart && chart.resize && chart.resize()), 40); }

  function exportFilteredCsv() {
    const st = readState(); const rows = data.series.filter(s => includeSeries(s, st));
    const headers = ["model_family","quant","runs","score","critical_recall_pct","high_critical_recall_pct","evidence_fidelity_pct","location_accuracy_pct","hallucination_rate_pct","f1_pct","stability_pct","run2_delta","fp","duplicates","missed","parse_success_pct","loop_pct","duration_median_sec"]; 
    const lines = [headers.join(",")]; rows.forEach(s => lines.push([s.family,s.quant,s.runCount,scoreForRunView(s,st.runView),s.criticalRecall,s.highCriticalRecall,s.evidenceFidelity,s.locationAccuracy,s.hallucinationRate,s.f1,s.stability,s.run2Delta,s.falsePositives,s.duplicates,s.missed,s.parseSuccessRate,s.loopRate,s.durationMedianSec??""].map(csv).join(",")));
    const blob = new Blob([lines.join("\n")], {type:"text/csv;charset=utf-8"}); const a = document.createElement("a"); a.href = URL.createObjectURL(blob); a.download = `supercalc-comparison-filtered-${Date.now()}.csv`; a.click(); URL.revokeObjectURL(a.href);
  }

  function updateChart(id, cfg) { const el = document.getElementById(id); if (!el) return; if (charts[id]) charts[id].destroy(); charts[id] = new Chart(el, cfg); }
  function fillSelect(id, values) { const el = document.getElementById(id); el.innerHTML = values.map(v => `<option value="${esc(v)}">${esc(v)}</option>`).join(""); }
  function selected(id) { return [...document.getElementById(id).selectedOptions].map(o => o.value); }
  function num(id) { const v = document.getElementById(id).value; return v === "" ? null : Number(v); }
  function checked(id) { return !!document.getElementById(id)?.checked; }
  function uniq(values) { return [...new Set(values.filter(v => v !== null && v !== undefined && v !== ""))].sort((a,b)=>String(a).localeCompare(String(b))); }
  function fmt(v) { return typeof v === "number" && Number.isFinite(v) ? v.toFixed(1) : "n/a"; }
  function fmtSigned(v) { return typeof v === "number" && Number.isFinite(v) ? (v>0?"+":"") + v.toFixed(1) : "n/a"; }
  function esc(v) { return String(v ?? "").replace(/[&<>"']/g, ch => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[ch])); }
  function csv(v) { v = String(v ?? ""); return /[",\n]/.test(v) ? '"' + v.replace(/"/g,'""') + '"' : v; }
  function axisTitle(a) { return `${a.id}${a.title?' — '+a.title:''} · ${a.severity||''} · ${a.category||''} · ${(a.cwe||[]).join('/')}`; }
  function heatClass(v, runView) { if (runView === "delta") return v < 0 ? "heat-neg" : (v > 0 ? "heat-pos" : "heat-0"); if (v >= .99) return "heat-100"; if (v > 0) return "heat-50"; return "heat-0"; }
  render();
})();
</script>
</body>
</html>
""");
        return builder.ToString();
    }

    public string BuildCsv(ComparisonReport report)
    {
        var builder = new StringBuilder();
        var header = new List<string>
        {
            "model_family", "quant", "run_count", "aggregate", "run_view", "scoring_profile", "official_comparable_runs", "legacy_migrated_runs", "rescored_runs", "score_percent",
            "score_mean", "score_median", "score_stddev", "score_iqr", "score_ci95", "score_min", "score_max",
            "precision_percent", "recall_percent", "f1_percent",
            "critical_recall_percent", "high_recall_percent", "medium_recall_percent", "low_recall_percent", "high_critical_recall_percent",
            "memory_safety_percent", "concurrency_percent", "injection_percent", "auth_crypto_percent", "numeric_dos_percent", "file_io_percent", "cwe_coverage_percent", "stability_percent",
            "evidence_fidelity_percent", "location_accuracy_percent", "hallucination_rate_percent", "evaluation_confidence_percent", "fp_taxonomy",
            "full_tp", "partial_tp", "false_positives", "duplicates", "ignored_low_confidence", "missed",
            "parse_success_percent", "loop_rate_percent", "empty_output_rate_percent", "visible_reasoning_rate_percent",
            "run1_score", "run2_score", "run2_delta", "run2_fp_reduction", "run2_tp_retention_percent", "run2_dropped_tp", "run2_added_tp",
            "truth_audit_runs", "accountability_score", "truth_audit_accuracy_percent", "overclaim_rate_percent", "miss_admission_rate_percent", "fp_admission_rate_percent", "evidence_laundering_count", "quote_fidelity_percent",
            "duration_mean_sec", "duration_median_sec", "duration_min_sec", "duration_max_sec",
            "visible_reasoning_runs", "thinking_parsed_findings", "output_parsed_findings",
            "thinking_tp", "output_tp", "thinking_only_tp", "output_only_tp", "thinking_to_output_coverage_percent"
        };
        header.AddRange(report.VulnerabilityAxis);
        builder.AppendLine(string.Join(",", header.Select(Csv)));

        foreach (var s in report.Series)
        {
            var cells = new List<string>
            {
                Csv(s.ModelFamily),
                Csv(s.Quant),
                s.RunCount.ToString(CultureInfo.InvariantCulture),
                Csv(s.Aggregate.ToString()),
                Csv(s.RunView.ToString()),
                Csv(report.ScoringProfile ?? "all"),
                s.OfficialComparableRunCount.ToString(CultureInfo.InvariantCulture),
                s.LegacyMigratedRunCount.ToString(CultureInfo.InvariantCulture),
                s.RescoredRunCount.ToString(CultureInfo.InvariantCulture),
                s.ScorePercent.ToString("0.##", CultureInfo.InvariantCulture),
                s.ScoreMean.ToString("0.##", CultureInfo.InvariantCulture),
                s.ScoreMedian.ToString("0.##", CultureInfo.InvariantCulture),
                s.ScoreStdDev.ToString("0.##", CultureInfo.InvariantCulture),
                s.ScoreIqr.ToString("0.##", CultureInfo.InvariantCulture),
                s.ScoreCi95.HasValue ? s.ScoreCi95.Value.ToString("0.##", CultureInfo.InvariantCulture) : string.Empty,
                s.ScoreMin.ToString("0.##", CultureInfo.InvariantCulture),
                s.ScoreMax.ToString("0.##", CultureInfo.InvariantCulture),
                (s.Precision * 100).ToString("0.#", CultureInfo.InvariantCulture),
                (s.Recall * 100).ToString("0.#", CultureInfo.InvariantCulture),
                (s.F1 * 100).ToString("0.#", CultureInfo.InvariantCulture),
                (s.CriticalRecall * 100).ToString("0.#", CultureInfo.InvariantCulture),
                (s.HighRecall * 100).ToString("0.#", CultureInfo.InvariantCulture),
                (s.MediumRecall * 100).ToString("0.#", CultureInfo.InvariantCulture),
                (s.LowRecall * 100).ToString("0.#", CultureInfo.InvariantCulture),
                (s.HighCriticalRecall * 100).ToString("0.#", CultureInfo.InvariantCulture),
                (s.MemorySafetyScore * 100).ToString("0.#", CultureInfo.InvariantCulture),
                (s.ConcurrencyScore * 100).ToString("0.#", CultureInfo.InvariantCulture),
                (s.InjectionScore * 100).ToString("0.#", CultureInfo.InvariantCulture),
                (s.AuthCryptoScore * 100).ToString("0.#", CultureInfo.InvariantCulture),
                (s.NumericDosScore * 100).ToString("0.#", CultureInfo.InvariantCulture),
                (s.FileIoScore * 100).ToString("0.#", CultureInfo.InvariantCulture),
                (s.CweCoverage * 100).ToString("0.#", CultureInfo.InvariantCulture),
                (s.VulnerabilityStability * 100).ToString("0.#", CultureInfo.InvariantCulture),
                (s.EvidenceFidelity * 100).ToString("0.#", CultureInfo.InvariantCulture),
                (s.LocationAccuracy * 100).ToString("0.#", CultureInfo.InvariantCulture),
                (s.HallucinationRate * 100).ToString("0.#", CultureInfo.InvariantCulture),
                (s.EvaluationConfidence * 100).ToString("0.#", CultureInfo.InvariantCulture),
                Csv(FormatTaxonomy(s.FalsePositiveTaxonomy)),
                s.FullTruePositives.ToString(CultureInfo.InvariantCulture),
                s.PartialTruePositives.ToString(CultureInfo.InvariantCulture),
                s.FalsePositives.ToString(CultureInfo.InvariantCulture),
                s.Duplicates.ToString(CultureInfo.InvariantCulture),
                s.IgnoredLowConfidence.ToString(CultureInfo.InvariantCulture),
                s.Missed.ToString(CultureInfo.InvariantCulture),
                (s.ParseSuccessRate * 100).ToString("0.#", CultureInfo.InvariantCulture),
                (s.LoopRate * 100).ToString("0.#", CultureInfo.InvariantCulture),
                (s.EmptyOutputRate * 100).ToString("0.#", CultureInfo.InvariantCulture),
                (s.VisibleReasoningRate * 100).ToString("0.#", CultureInfo.InvariantCulture),
                s.Run1Score.ToString("0.##", CultureInfo.InvariantCulture),
                s.Run2Score.ToString("0.##", CultureInfo.InvariantCulture),
                s.Run2ScoreDelta.ToString("0.##", CultureInfo.InvariantCulture),
                s.Run2FpReduction.ToString("0.##", CultureInfo.InvariantCulture),
                (s.Run2TpRetention * 100).ToString("0.#", CultureInfo.InvariantCulture),
                s.Run2DroppedTpCount.ToString("0.#", CultureInfo.InvariantCulture),
                s.Run2AddedTpCount.ToString("0.#", CultureInfo.InvariantCulture),
                s.TruthAuditRunCount.ToString(CultureInfo.InvariantCulture),
                s.AccountabilityScore.ToString("0.##", CultureInfo.InvariantCulture),
                (s.TruthAuditAccuracy * 100).ToString("0.#", CultureInfo.InvariantCulture),
                (s.OverclaimRate * 100).ToString("0.#", CultureInfo.InvariantCulture),
                (s.MissAdmissionRate * 100).ToString("0.#", CultureInfo.InvariantCulture),
                (s.FalsePositiveAdmissionRate * 100).ToString("0.#", CultureInfo.InvariantCulture),
                s.EvidenceLaunderingCount.ToString("0.#", CultureInfo.InvariantCulture),
                (s.QuoteFidelity * 100).ToString("0.#", CultureInfo.InvariantCulture),
                s.DurationMeanMs.HasValue ? (s.DurationMeanMs.Value / 1000.0).ToString("0.#", CultureInfo.InvariantCulture) : string.Empty,
                s.DurationMedianMs.HasValue ? (s.DurationMedianMs.Value / 1000.0).ToString("0.#", CultureInfo.InvariantCulture) : string.Empty,
                s.DurationMinMs.HasValue ? (s.DurationMinMs.Value / 1000.0).ToString("0.#", CultureInfo.InvariantCulture) : string.Empty,
                s.DurationMaxMs.HasValue ? (s.DurationMaxMs.Value / 1000.0).ToString("0.#", CultureInfo.InvariantCulture) : string.Empty,
                s.VisibleReasoningRunCount.ToString(CultureInfo.InvariantCulture),
                s.ReasoningParsedFindings.ToString("0.#", CultureInfo.InvariantCulture),
                s.OutputParsedFindings.ToString("0.#", CultureInfo.InvariantCulture),
                s.ReasoningTruePositives.ToString("0.#", CultureInfo.InvariantCulture),
                s.OutputTruePositives.ToString("0.#", CultureInfo.InvariantCulture),
                s.ReasoningOnlyTruePositives.ToString("0.#", CultureInfo.InvariantCulture),
                s.OutputOnlyTruePositives.ToString("0.#", CultureInfo.InvariantCulture),
                s.ReasoningToOutputCoverage.HasValue ? (s.ReasoningToOutputCoverage.Value * 100).ToString("0.#", CultureInfo.InvariantCulture) : string.Empty
            };
            cells.AddRange(s.PerVulnerabilityCredit.Select(v => v.ToString("0.###", CultureInfo.InvariantCulture)));
            builder.AppendLine(string.Join(",", cells));
        }

        return builder.ToString();
    }

    private static Dictionary<string, double> PercentDictionary(Dictionary<string, double> values)
        => values.ToDictionary(kvp => kvp.Key, kvp => Math.Round(kvp.Value * 100, 1), StringComparer.OrdinalIgnoreCase);

    private static Dictionary<string, double> CountDictionary(Dictionary<string, double> values)
        => values.ToDictionary(kvp => kvp.Key, kvp => Math.Round(kvp.Value, 1), StringComparer.OrdinalIgnoreCase);

    private static string MetricValue(ComparisonMetric metric) => metric switch
    {
        ComparisonMetric.CriticalRecall => "criticalRecall",
        ComparisonMetric.HighCriticalRecall => "highCriticalRecall",
        ComparisonMetric.F1 => "f1",
        ComparisonMetric.FpRate => "fpRate",
        ComparisonMetric.Stability => "stability",
        ComparisonMetric.Run2Delta => "run2Delta",
        ComparisonMetric.ThinkingCoverage => "thinkingCoverage",
        ComparisonMetric.EvidenceFidelity => "evidenceFidelity",
        ComparisonMetric.LocationAccuracy => "locationAccuracy",
        ComparisonMetric.HallucinationRate => "hallucinationRate",
        ComparisonMetric.EvaluationConfidence => "evaluationConfidence",
        ComparisonMetric.Accountability => "accountability",
        ComparisonMetric.OverclaimRate => "overclaimRate",
        ComparisonMetric.Duration => "duration",
        _ => "score"
    };

    private static string[] BuildPalette(int count)
    {
        var colors = new string[Math.Max(count, 1)];
        for (var i = 0; i < colors.Length; i++)
        {
            var hue = (int)Math.Round(360.0 * i / Math.Max(colors.Length, 1));
            colors[i] = HslToHex(hue, 65, 48);
        }

        return colors;
    }

    private static string HslToHex(double h, double s, double l)
    {
        s /= 100; l /= 100;
        var c = (1 - Math.Abs(2 * l - 1)) * s;
        var x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        var m = l - c / 2;
        double r = 0, g = 0, b = 0;
        if (h < 60) { r = c; g = x; }
        else if (h < 120) { r = x; g = c; }
        else if (h < 180) { g = c; b = x; }
        else if (h < 240) { g = x; b = c; }
        else if (h < 300) { r = x; b = c; }
        else { r = c; b = x; }

        var ri = (int)Math.Round((r + m) * 255);
        var gi = (int)Math.Round((g + m) * 255);
        var bi = (int)Math.Round((b + m) * 255);
        return $"#{ri:x2}{gi:x2}{bi:x2}";
    }

    private static string FormatTaxonomy(Dictionary<string, double> taxonomy)
    {
        if (taxonomy.Count == 0)
        {
            return string.Empty;
        }

        return string.Join("; ", taxonomy.OrderByDescending(kvp => kvp.Value).ThenBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase).Select(kvp => $"{kvp.Key}={kvp.Value:0.#}"));
    }

    private static string Csv(string value)
    {
        var v = value ?? string.Empty;
        if (v.Contains('"') || v.Contains(',') || v.Contains('\n'))
        {
            return "\"" + v.Replace("\"", "\"\"") + "\"";
        }

        return v;
    }

    private static string HtmlEscape(string value) =>
        value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
}
