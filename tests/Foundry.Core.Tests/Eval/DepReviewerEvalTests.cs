using CsvHelper;
using CsvHelper.Configuration;
using System.Globalization;
using System.IO;
using Foundry.Core.Agents;
using Foundry.Models;
using Xunit;
using Xunit.Abstractions;

namespace Foundry.Core.Tests.Eval;

public sealed class DepReviewerEvalTests
{
    private readonly ITestOutputHelper _output;

    public DepReviewerEvalTests(ITestOutputHelper output) => _output = output;

    [Fact(DisplayName = "DepReviewer: eval against historical-prs.csv")]
    [Trait("Category", "Eval")]
    public void DepReviewer_Eval_HistoricalPrs()
    {
        var csvPath = ResolveCsvPath();
        if (csvPath is null)
        {
            _output.WriteLine("SKIP: foundry-evals/dep-reviewer/historical-prs.csv not found — run from repo root.");
            return;
        }

        var stripList = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Microsoft.ML.OnnxRuntime", "Microsoft.ML", "TorchSharp", "TensorFlow.NET", "ONNX",
        };
        var devToolingList = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "pytest", "xunit", "Microsoft.NET.Test.Sdk", "coverlet.collector",
            "Microsoft.AspNetCore.Mvc.Testing", "png-to-ico", "sharp",
        };

        var rows = ReadCsvRows(csvPath);
        var results = EvaluateRows(rows, stripList, devToolingList);
        PrintReport(results);

        var scorable = results.Where(r => r.VerdictClass != "stale-config").ToList();
        var correct = scorable.Count(r => r.Labeled == r.Predicted);
        var pct = scorable.Count == 0 ? 100.0 : correct * 100.0 / scorable.Count;

        Assert.True(pct >= 90.0,
            $"Agreement on non-stale-config rows: {correct}/{scorable.Count} ({pct:F1}%) — below 90% threshold.");
    }

    private List<EvalResult> EvaluateRows(
        IReadOnlyList<CsvRow> rows,
        HashSet<string> stripList,
        HashSet<string> devToolingList)
    {
        var results = new List<EvalResult>(rows.Count);
        foreach (var row in rows)
        {
            var payload = new DependencyReviewPayload
            {
                Kind = "pull-request",
                Repository = row.Repo,
                PackageName = row.Package,
                Ecosystem = row.Ecosystem,
                CurrentVersion = row.FromVersion,
                TargetVersion = row.ToVersion,
                UpdateType = row.SemverBump,
            };
            var outcome = DepReviewerAgent.BuildRuleBasedOutcome(payload, stripList, devToolingList);
            results.Add(new EvalResult(
                row.Repo,
                row.Package,
                row.VerdictClass,
                row.Verdict,
                CategoryToVerdict(outcome.Category),
                outcome.Category));
        }
        return results;
    }

    private void PrintReport(List<EvalResult> results)
    {
        var total = results.Count;
        var correct = results.Count(r => r.Labeled == r.Predicted);
        _output.WriteLine("=== DepReviewer Eval Report ===");
        _output.WriteLine($"Overall:  {correct}/{total} ({correct * 100.0 / total:F1}%)");
        _output.WriteLine(string.Empty);

        _output.WriteLine("Per-class breakdown:");
        foreach (var group in results
            .GroupBy(r => string.IsNullOrEmpty(r.VerdictClass) ? "(safe-rows)" : r.VerdictClass)
            .OrderBy(g => g.Key))
        {
            var groupCorrect = group.Count(r => r.Labeled == r.Predicted);
            _output.WriteLine($"  {group.Key,-22} {groupCorrect}/{group.Count()}");
        }

        _output.WriteLine(string.Empty);

        var falseSafe = results.Where(r => r.Labeled == "HOLD" && r.Predicted == "SAFE").ToList();
        var falseHold = results.Where(r => r.Labeled == "SAFE" && r.Predicted == "HOLD").ToList();

        _output.WriteLine($"False-SAFE (HOLD→SAFE): {falseSafe.Count}");
        foreach (var r in falseSafe)
            _output.WriteLine($"  {r.Repo}/{r.Package} [{r.VerdictClass}] agent={r.PredictedCategory}");

        _output.WriteLine($"False-HOLD (SAFE→HOLD): {falseHold.Count}");
        foreach (var r in falseHold)
            _output.WriteLine($"  {r.Repo}/{r.Package} [{r.VerdictClass}] agent={r.PredictedCategory}");

        var scorable = results.Where(r => r.VerdictClass != "stale-config").ToList();
        var scorableCorrect = scorable.Count(r => r.Labeled == r.Predicted);
        _output.WriteLine(string.Empty);
        _output.WriteLine($"Non-stale-config: {scorableCorrect}/{scorable.Count} ({scorableCorrect * 100.0 / scorable.Count:F1}%) [threshold ≥90%]");
    }

    private static string CategoryToVerdict(string category) =>
        category is DependencyNotificationCategory.Info or DependencyNotificationCategory.NeedsReview
            ? "SAFE"
            : "HOLD";

    private static string? ResolveCsvPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "foundry-evals", "dep-reviewer", "historical-prs.csv");
            if (File.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }
        return null;
    }

    private static IReadOnlyList<CsvRow> ReadCsvRows(string path)
    {
        using var reader = new StreamReader(path);
        using var csv = new CsvReader(reader, new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            HeaderValidated = null,
            MissingFieldFound = null,
        });

        var rows = new List<CsvRow>();
        csv.Read();
        csv.ReadHeader();
        while (csv.Read())
        {
            rows.Add(new CsvRow(
                Repo: csv.GetField("repo") ?? string.Empty,
                Package: csv.GetField("package") ?? string.Empty,
                FromVersion: csv.GetField("from_version") ?? string.Empty,
                ToVersion: csv.GetField("to_version") ?? string.Empty,
                Ecosystem: csv.GetField("ecosystem") ?? string.Empty,
                SemverBump: csv.GetField("semver_bump") ?? string.Empty,
                Verdict: csv.GetField("verdict") ?? string.Empty,
                VerdictClass: csv.GetField("verdict_class") ?? string.Empty));
        }
        return rows;
    }

    private sealed record CsvRow(
        string Repo,
        string Package,
        string FromVersion,
        string ToVersion,
        string Ecosystem,
        string SemverBump,
        string Verdict,
        string VerdictClass);

    private sealed record EvalResult(
        string Repo,
        string Package,
        string VerdictClass,
        string Labeled,
        string Predicted,
        string PredictedCategory);
}
