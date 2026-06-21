using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace SuperCalcBenchmark.Core;

/// <summary>
/// Renders a <see cref="ComparisonReport"/> to a single self-contained HTML file:
///   - a grouped bar chart of total score per model+quant,
///   - a radar ("net") chart of per-vulnerability credit per model+quant,
///   - a sortable summary table.
/// Chart.js is pulled from a CDN; the data itself is embedded as JSON so the page renders
/// without any local server. Both files (CSV alongside) are written for spreadsheet use.
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
        // Distinct, deterministic colours per series.
        var palette = BuildPalette(report.Series.Count);

        var payload = new
        {
            benchmarkId = report.BenchmarkId,
            generatedAt = report.GeneratedAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
            axis = report.VulnerabilityAxis,
            series = report.Series.Select((s, i) => new
            {
                label = s.Label,
                family = s.ModelFamily,
                quant = s.Quant,
                runCount = s.RunCount,
                aggregate = s.Aggregate.ToString(),
                score = Math.Round(s.ScorePercent, 2),
                precision = Math.Round(s.Precision * 100, 1),
                recall = Math.Round(s.Recall * 100, 1),
                f1 = Math.Round(s.F1 * 100, 1),
                fullTp = s.FullTruePositives,
                partialTp = s.PartialTruePositives,
                falsePositives = s.FalsePositives,
                missed = s.Missed,
                perVuln = s.PerVulnerabilityCredit.Select(v => Math.Round(v, 3)),
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
  :root { color-scheme: light dark; }
  body { font-family: "Segoe UI", system-ui, sans-serif; margin: 0; padding: 24px; background: #f6f7f9; color: #1d2129; }
  h1 { font-size: 22px; margin: 0 0 4px; }
  h2 { font-size: 16px; margin: 28px 0 10px; }
  .meta { color: #6b7280; font-size: 13px; margin-bottom: 8px; }
  .card { background: #fff; border: 1px solid #e3e6ea; border-radius: 10px; padding: 18px; margin-bottom: 20px; box-shadow: 0 1px 2px rgba(0,0,0,.04); }
  .grid { display: grid; grid-template-columns: 1fr; gap: 20px; }
  @media (min-width: 1100px) { .grid { grid-template-columns: 1fr 1fr; } }
  canvas { max-width: 100%; }
  .chart-box { position: relative; height: 440px; }
  table { border-collapse: collapse; width: 100%; font-size: 13px; }
  th, td { padding: 7px 10px; border-bottom: 1px solid #eceef1; text-align: right; white-space: nowrap; }
  th:first-child, td:first-child { text-align: left; }
  th { cursor: pointer; user-select: none; background: #fafbfc; position: sticky; top: 0; }
  th.sorted-asc::after { content: " \25B2"; }
  th.sorted-desc::after { content: " \25BC"; }
  tr.swatch td:first-child::before { content: ""; display: inline-block; width: 11px; height: 11px; border-radius: 3px; margin-right: 8px; vertical-align: -1px; background: var(--swatch); }
  .empty { padding: 40px; text-align: center; color: #6b7280; }
  .controls { margin: 0 0 14px; font-size: 13px; color: #374151; }
  .note { font-size: 12px; color: #6b7280; margin-top: 8px; }
  code { background: #eef0f2; padding: 1px 5px; border-radius: 4px; }
</style>
</head>
<body>
<h1>SuperCalc Benchmark — Modellvergleich</h1>
<div class="meta" id="meta"></div>
<div id="content"></div>
<script src="
""");
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
  meta.textContent = "Benchmark: " + data.benchmarkId + " · erzeugt " + data.generatedAt + " · " + data.series.length + " Modell(e)/Quants";

  if (!data.series.length) {
    content.innerHTML = '<div class="card empty">Noch keine archivierten Runs gefunden. Starte einen Benchmark, danach erscheinen hier die Vergleiche.</div>';
    return;
  }

  const hasChart = typeof Chart !== "undefined";
  const charts = hasChart
    ? '<div class="grid">'
      + '<div class="card"><h2>Gesamtpunkte je Modell + Quant</h2><div class="chart-box"><canvas id="barChart"></canvas></div></div>'
      + '<div class="card"><h2>Einzelwerte je Schwachstelle (Netz)</h2><div class="controls"><label><input type="checkbox" id="fillToggle" /> Flächen füllen</label></div><div class="chart-box"><canvas id="radarChart"></canvas></div></div>'
      + '</div>'
    : '<div class="card note">Chart.js konnte nicht geladen werden (keine Internetverbindung?). Die Tabelle unten funktioniert weiterhin offline.</div>';

  content.innerHTML = charts + '<div class="card"><h2>Übersicht</h2><div id="tableWrap"></div><div class="note">Werte je Schwachstelle: 1.0 = voll erkannt, 0.5 = teilweise, 0.0 = verpasst. Spaltenkopf klicken zum Sortieren.</div></div>';

  if (hasChart) {
    const barCtx = document.getElementById("barChart");
    new Chart(barCtx, {
      type: "bar",
      data: {
        labels: data.series.map(s => s.label),
        datasets: [{
          label: "Score (0–100)",
          data: data.series.map(s => s.score),
          backgroundColor: data.series.map(s => s.color + "cc"),
          borderColor: data.series.map(s => s.color),
          borderWidth: 1
        }]
      },
      options: {
        indexAxis: "y",
        responsive: true,
        maintainAspectRatio: false,
        scales: { x: { beginAtZero: true, max: 100, title: { display: true, text: "Score" } } },
        plugins: { legend: { display: false }, tooltip: { callbacks: { label: c => " " + c.parsed.x.toFixed(1) + " / 100" } } }
      }
    });

    const radarChart = new Chart(document.getElementById("radarChart"), {
      type: "radar",
      data: {
        labels: data.axis,
        datasets: data.series.map(s => ({
          label: s.label,
          data: s.perVuln,
          borderColor: s.color,
          backgroundColor: s.color + "33",
          pointBackgroundColor: s.color,
          fill: false,
          borderWidth: 2,
          pointRadius: 2
        }))
      },
      options: {
        responsive: true,
        maintainAspectRatio: false,
        scales: { r: { min: 0, max: 1, ticks: { stepSize: 0.5, showLabelBackdrop: false }, pointLabels: { font: { size: 10 } } } },
        plugins: { legend: { position: "bottom", labels: { boxWidth: 12, font: { size: 11 } } } }
      }
    });

    document.getElementById("fillToggle").addEventListener("change", e => {
      const fill = e.target.checked;
      radarChart.data.datasets.forEach(d => { d.fill = fill; });
      radarChart.update();
    });
  }

  // ---- Summary table with click-to-sort -----------------------------------
  const cols = [
    { key: "label", title: "Modell · Quant", kind: "text" },
    { key: "runCount", title: "Runs", kind: "num" },
    { key: "score", title: "Score", kind: "num" },
    { key: "precision", title: "Precision %", kind: "num" },
    { key: "recall", title: "Recall %", kind: "num" },
    { key: "f1", title: "F1 %", kind: "num" },
    { key: "fullTp", title: "Full TP", kind: "num" },
    { key: "partialTp", title: "Partial", kind: "num" },
    { key: "falsePositives", title: "FP", kind: "num" },
    { key: "missed", title: "Missed", kind: "num" }
  ];
  let sortKey = "score";
  let sortDir = -1;

  function render() {
    const rows = data.series.slice().sort((a, b) => {
      const av = a[sortKey], bv = b[sortKey];
      if (typeof av === "number") return (av - bv) * sortDir;
      return String(av).localeCompare(String(bv)) * sortDir;
    });
    let html = "<table><thead><tr>";
    cols.forEach(c => {
      const cls = c.key === sortKey ? (sortDir === 1 ? "sorted-asc" : "sorted-desc") : "";
      html += '<th class="' + cls + '" data-key="' + c.key + '">' + c.title + "</th>";
    });
    html += "</tr></thead><tbody>";
    rows.forEach(s => {
      html += '<tr class="swatch" style="--swatch:' + s.color + '">';
      cols.forEach(c => {
        let v = s[c.key];
        if (c.kind === "num" && typeof v === "number") v = Number.isInteger(v) ? v : v.toFixed(1);
        html += "<td>" + v + "</td>";
      });
      html += "</tr>";
    });
    html += "</tbody></table>";
    document.getElementById("tableWrap").innerHTML = html;
    document.querySelectorAll("th[data-key]").forEach(th => {
      th.addEventListener("click", () => {
        const k = th.getAttribute("data-key");
        if (k === sortKey) { sortDir = -sortDir; } else { sortKey = k; sortDir = (k === "label") ? 1 : -1; }
        render();
      });
    });
  }
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
            "model_family", "quant", "run_count", "aggregate", "score_percent",
            "precision_percent", "recall_percent", "f1_percent",
            "full_tp", "partial_tp", "false_positives", "missed"
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
                s.ScorePercent.ToString("0.##", CultureInfo.InvariantCulture),
                (s.Precision * 100).ToString("0.#", CultureInfo.InvariantCulture),
                (s.Recall * 100).ToString("0.#", CultureInfo.InvariantCulture),
                (s.F1 * 100).ToString("0.#", CultureInfo.InvariantCulture),
                s.FullTruePositives.ToString(CultureInfo.InvariantCulture),
                s.PartialTruePositives.ToString(CultureInfo.InvariantCulture),
                s.FalsePositives.ToString(CultureInfo.InvariantCulture),
                s.Missed.ToString(CultureInfo.InvariantCulture)
            };
            cells.AddRange(s.PerVulnerabilityCredit.Select(v => v.ToString("0.###", CultureInfo.InvariantCulture)));
            builder.AppendLine(string.Join(",", cells));
        }

        return builder.ToString();
    }

    private static string[] BuildPalette(int count)
    {
        // Evenly spaced hues → distinct, stable colours regardless of series count.
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
