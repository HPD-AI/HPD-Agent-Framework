# Batch Evals

Batch evals are the shortest path from "does this agent still behave?" to a repeatable regression check. Start with one deterministic case, make the output visible, then wire the same run into CI.

The examples below are grounded in the current `HPD-Agent.Evaluations` source: `RunEvals.ExecuteAsync(...)` runs each dataset case through an `Agent`, evaluates the captured turn, returns an `EvaluationReport`, and disables live evaluators for that batch turn so the evaluator list you pass to the batch run is the report source of truth.

## Add References

For an app or test project that consumes packages:

```bash
dotnet add package HPD-Agent.Evaluations --version 0.5.5
```

For source-tree development, reference the projects directly instead:

```xml
<ItemGroup>
  <ProjectReference Include="../src/HPD-Agent/HPD-Agent.csproj" />
  <ProjectReference Include="../src/HPD-Agent.Evaluations/HPD-Agent.Evaluations.csproj" />
</ItemGroup>
```

Common imports for a first deterministic batch eval:

```csharp
using HPD.Agent;
using HPD.Agent.Evaluations.Batch;
using HPD.Agent.Evaluations.Evaluators.Deterministic;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;
using System.Runtime.CompilerServices;
```

Add `using HPD.Agent.Evaluations.Storage;` when you persist scores to a store.
Add `using HPD.Agent.Evaluations.Evaluators.LlmJudge;` when you use judge evaluators such as `AspectCriticEvaluator`.

## Run One Locally

This sample uses a fixed `IChatClient`, so it runs without a model key and is safe to paste into a console app, test, or scratch file. Swap the fixed client for your provider-backed client after the batch wiring works.

```csharp
var agent = await new AgentBuilder()
    .WithName("EvalAgent")
    .WithChatClient(new FixedResponseChatClient("Paris"))
    .BuildAsync();

var dataset = new Dataset<string>
{
    DatasetId = "capital-smoke",
    Version = "1",
    Cases =
    [
        new EvalCase<string>
        {
            CaseId = "france-capital",
            Name = "france-capital",
            Version = "1",
            Input = "What is the capital of France?",
            GroundTruth = "Paris",
        },
    ],
};

var report = await RunEvals.ExecuteAsync(
    agent,
    dataset,
    evaluators:
    [
        new EqualsGroundTruthEvaluator(),
        new OutputContainsEvaluator("Paris"),
    ],
    experimentName: "local-smoke");

report.Print();
Console.WriteLine(report.ToJson());

if (report.HasPolicyViolations)
{
    throw new InvalidOperationException(report.FormatPolicyViolations());
}

sealed class FixedResponseChatClient(string responseText) : IChatClient
{
    public ChatClientMetadata Metadata => new("FixedResponseChatClient");

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => Task.FromResult(new ChatResponse(
            [new ChatMessage(ChatRole.Assistant, responseText)]));

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        yield return new ChatResponseUpdate
        {
            Contents = [new TextContent(responseText)],
            FinishReason = ChatFinishReason.Stop,
        };
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}
```

Expected console shape:

```text
=== local-smoke ===
  Equals Ground Truth: pass=100.0%  avg=0.000
  Output Contains: pass=100.0%  avg=0.000
```

`ToJson()` also includes the experiment name, cases, metric values, task duration, provider/model fields when known, and `infrastructure_error_rate`.

## Fail CI On Regressions

Deterministic evaluators derive from the deterministic evaluator base and default to `MustAlwaysPass` in batch runs. That means a failed `OutputContainsEvaluator`, `EqualsGroundTruthEvaluator`, tool-call assertion, JSON shape assertion, or similar built-in deterministic evaluator adds a policy violation.

xUnit style:

```csharp
[Fact]
public async Task Capital_eval_still_passes()
{
    var report = await RunEvals.ExecuteAsync(
        agent,
        dataset,
        evaluators: [new EqualsGroundTruthEvaluator(), new OutputContainsEvaluator("Paris")],
        experimentName: "capital-ci");

    Assert.Empty(report.Failures);
    Assert.False(report.HasPolicyViolations, report.FormatPolicyViolations());
    Assert.Equal(1.0, report.PassRate("Output Contains"));
}
```

NUnit style:

```csharp
Assert.That(report.Failures, Is.Empty);
Assert.That(report.HasPolicyViolations, Is.False, report.FormatPolicyViolations());
Assert.That(report.PassRate("Output Contains"), Is.EqualTo(1.0));
```

For CI artifacts, write both machine-readable JSON and human-readable HTML:

```csharp
Directory.CreateDirectory("artifacts/evals");
await File.WriteAllTextAsync("artifacts/evals/capital-ci.json", report.ToJson());
await report.WriteHtmlAsync("artifacts/evals/capital-ci.html");
```

The HTML report contains a metric summary, per-case rows, failures, and policy violation details.

## Move Cases To YAML

When the case list grows, keep evaluators in code or use the supported short forms in YAML. YAML parsing requires a delegate for the typed `input`.

```yaml
dataset_id: capital-smoke
version: "1"
evaluators:
  - EqualsGroundTruth
  - OutputContains: Paris
cases:
  - case_id: france-capital
    name: france-capital
    version: "1"
    input: What is the capital of France?
    ground_truth: Paris
    metadata:
      area: geography
```

```csharp
using System.Text.Json.Nodes;

var yaml = await File.ReadAllTextAsync("capital-smoke.yaml");
var dataset = Dataset<string>.FromYaml(
    yaml,
    node => node?.GetValue<string>() ?? string.Empty);

var report = await RunEvals.ExecuteAsync(
    agent,
    dataset,
    experimentName: "capital-yaml");
```

Supported YAML evaluator names are explicit factory mappings, not arbitrary type loading. For a first run, prefer `EqualsGroundTruth`, `OutputContains`, `ContainsAny`, `ContainsAll`, `IContains`, `StartsWith`, `WordCount`, `JsonValidity`, `ToolWasCalled`, and other built-ins already handled by the dataset evaluator factory.

## Run Against A Real Agent

The batch API does not require a special eval agent. Build your normal agent, then pass it to `RunEvals.ExecuteAsync(...)`.

```csharp
var agent = await new AgentBuilder()
    .WithName("SupportAgent")
    .WithChatClient(appChatClient)
    .BuildAsync();

var report = await RunEvals.ExecuteAsync(
    agent,
    dataset,
    evaluators: [new OutputContainsEvaluator("refund policy")],
    options: new RunEvalsOptions<string>
    {
        BaseRunConfig = new AgentRunConfig
        {
            ProviderKey = "openai",
            ModelId = "gpt-5-mini",
        },
        Concurrency = 4,
        Repeat = 1,
    },
    experimentName: "support-regression");
```

Use `Concurrency` for larger deterministic datasets. Use `Repeat` when you intentionally want to observe model variance. Keep `Repeat = 1` for strict CI unless the assertion logic accounts for repeated cases.

## Persist Scores

Use `PersistResults` when you want score records and run records outside the returned report:

```csharp
var store = new InMemoryScoreStore();

var report = await RunEvals.ExecuteAsync(
    agent,
    dataset,
    evaluators: [new OutputContainsEvaluator("Paris")],
    options: new RunEvalsOptions<string>
    {
        PersistResults = true,
        ScoreStore = store,
        OnCaseComplete = (evalCase, singleCaseReport) =>
        {
            Console.WriteLine($"{evalCase.CaseId}: {singleCaseReport.Cases.Count}");
        },
    },
    experimentName: "local-smoke");
```

`InMemoryScoreStore` is a local/dev store. Use application storage when you need durable history. Persisted records include dataset and case provenance when `DatasetId`, dataset `Version`, `CaseId`, and case `Version` are set.

## Add Judge Evaluators Later

Deterministic evaluators do not need judge configuration. LLM judge and safety evaluators do.

```csharp
var report = await RunEvals.ExecuteAsync(
    agent,
    dataset,
    evaluators: [new AspectCriticEvaluator("The answer is correct and concise.")],
    options: new RunEvalsOptions<string>
    {
        JudgeConfig = new EvalJudgeConfig
        {
            OverrideChatClient = judgeChatClient,
        },
    },
    experimentName: "judge-smoke");
```

Treat judge metrics as quality signals unless your own policy defines a threshold. Batch defaults deterministic evaluators to `MustAlwaysPass` and non-deterministic evaluators to trend tracking.

## Common Errors

`No ChatConfiguration was provided. An IChatClient is required for judge evaluation.`  
You used a judge evaluator such as `AspectCriticEvaluator` without `RunEvalsOptions.JudgeConfig`. Add `OverrideChatClient` or `OverrideAgent`, or use deterministic evaluators for the first CI check.

`Unknown evaluator '...'` from YAML loading.  
YAML evaluator names must be one of the supported factory mappings. If you wrote a custom evaluator, construct it in code and pass it to `RunEvals.ExecuteAsync(...)`.

`already registered with different content` when using a dataset store.  
The same `DatasetId` and `Version` were registered with different cases. Bump the dataset version or fix the accidental drift.

Pass rate is `0.0` for a metric you expected.  
Check the metric name. `PassRate(...)` uses metric display names such as `Output Contains`, `Equals Ground Truth`, and `Tool Was Called`, not necessarily the evaluator class name.

Infrastructure errors show up in the report.  
Batch separates infrastructure failures from task failures. `InfrastructureErrorRate` reflects exhausted infrastructure errors such as rate limits; treat high infrastructure error rates as flaky eval environment signals and rerun before making product decisions.
