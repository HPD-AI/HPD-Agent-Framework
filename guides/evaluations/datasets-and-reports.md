# Datasets And Reports

Start with cases in code. Move them to YAML when the list grows, when non-engineers need to review them, or when you want stable CI artifacts.

## Create A Dataset In Code

```csharp
var dataset = new Dataset<string>
{
    DatasetId = "support-bench",
    Version = "2026.02",
    Cases =
    [
        new EvalCase<string>
        {
            CaseId = "geo-001",
            Name = "capital",
            Version = "1",
            Input = "What is the capital of France?",
            GroundTruth = "Paris",
        },
    ],
};
```

Use stable `DatasetId`, `Version`, and `CaseId` values when reports need to be compared across commits or releases.

## Move Cases To YAML

```yaml
dataset_id: support-bench
version: 2026.02
cases:
  - case_id: geo-001
    name: capital
    version: "1"
    input: What is the capital of France?
    ground_truth: Paris
```

YAML helpers take delegates for serializing and parsing the typed input. That keeps the dataset format flexible without relying on reflection for arbitrary case types.

```csharp
using System.Text.Json.Nodes;

var yaml = dataset.ToYaml(input => JsonValue.Create(input));

var roundTrip = Dataset<string>.FromYaml(
    yaml,
    node => node?.GetValue<string>() ?? string.Empty);
```

For object inputs, parse the `JsonNode` into your app's request type in that delegate.

## Keep Case IDs Stable

Use `CaseId` as the durable identity for a test case. Keep it the same when you edit metadata or expected output, and bump `Version` when the case meaning changes.

Good case IDs are boring:

```text
support-refusal-001
tool-weather-003
invoice-json-002
```

That makes reports easier to compare across threads and releases.

## Reports

`RunEvals.ExecuteAsync(...)` returns an `EvaluationReport`.

Common report operations:

```csharp
Console.WriteLine(report.Cases.Count);
Console.WriteLine(report.PassRate("Output Contains"));

var json = report.ToJson();
await report.WriteHtmlAsync("eval-report.html");
```

Use JSON output for CI artifacts, dashboards, or later comparison. Use HTML when you want a quick human-readable report. Use score stores when you need per-metric records outside the report object.

## Register Dataset Versions

Use a dataset store when you want HPD Agent to remember immutable dataset versions before a run:

```csharp
using HPD.Agent.Evaluations.Batch;
using HPD.Agent.Evaluations.Evaluators.Deterministic;
using HPD.Agent.Evaluations.Storage;

var datasetStore = new InMemoryDatasetStore();

var report = await RunEvals.ExecuteAsync(
    agent,
    dataset,
    evaluators: [new EqualsGroundTruthEvaluator()],
    options: new RunEvalsOptions<string>
    {
        DatasetStore = datasetStore,
    },
    experimentName: "support-regression");
```

`InMemoryDatasetStore` is for local runs and tests. Use application storage for durable dataset history.

## Compare Dataset Versions

Dataset stores can compare versions when your app needs review tooling around benchmark changes:

```csharp
var diff = await datasetStore.CompareVersionsAsync<string>(
    datasetId: "support-bench",
    fromVersion: "2026.01",
    toVersion: "2026.02");

Console.WriteLine(diff.Added.Count);
Console.WriteLine(diff.Changed.Count);
```

## Report Boundaries

Report JSON is practical output for tooling and CI artifacts. Treat custom evaluator construction from serialized YAML/JSON as application code; HPD Agent does not make arbitrary serialized evaluator types a public configuration contract.
