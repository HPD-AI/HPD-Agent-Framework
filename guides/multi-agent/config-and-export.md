# Config And Export

Multi-agent workflows have a serializable configuration shape for agents, edges, and settings. Treat this as a workflow definition format, not a snapshot of a running workflow.

## Minimal Shape

```json
{
  "Name": "ReviewWorkflow",
  "Version": "1.0.0",
  "Agents": {
    "draft": {
      "Agent": {
        "Name": "Drafter",
        "SystemInstructions": "Draft a concise answer."
      },
      "OutputMode": "String",
      "OutputKey": "draft"
    },
    "review": {
      "Agent": {
        "Name": "Reviewer",
        "SystemInstructions": "Improve the draft."
      },
      "InputKey": "draft"
    }
  },
  "Edges": [
    {
      "From": "draft",
      "To": "review"
    }
  ],
  "Settings": {
    "EnableCheckpointing": false,
    "EnableMetrics": true
  }
}
```

Config can include:

- workflow name, description, and version
- agent node definitions
- per-node output mode, timeout, retry, input/output keys, input template, and additional instructions
- routing edges and declarative conditions
- workflow settings and iteration options

## Load From Config

```csharp
var workflow = await AgentWorkflow
    .FromJson("review-workflow.json")
    .BuildAsync();
```

or:

```csharp
var workflow = await AgentWorkflow
    .FromConfig(config)
    .BuildAsync();
```

## Export

`AgentWorkflowInstance.ExportConfigJson()` reconstructs a config document from a built workflow.

Export is useful for inspection, tests, generated examples, and config authoring workflows. It does not serialize live agent instances, service providers, delegates, chat clients, runtime state, or in-flight execution state.

## Round-Trip Boundaries

These pieces export cleanly:

- config-backed agent definitions
- node ids and regular graph edges
- string output keys and input keys/templates
- declarative edge conditions
- compound conditions and regex options
- retry, timeout, and selected node options

These pieces need care:

- prebuilt agent object state is not serialized
- predicate edge delegates are non-serializable runtime code
- structured and union type names are represented, but workflows that depend on full `Type` restoration should verify import behavior in their target runtime
- handoff target descriptions are not fully represented by the current node config shape
- error/fallback settings exist in config/export, but execution wiring should be validated before relying on them as runtime policy
- direct export currently uses Pascal-case property names

## Iteration Settings

Prefer `IterationOptions` for cyclic and change-aware workflows:

```json
{
  "Settings": {
    "IterationOptions": {
      "MaxIterations": 10,
      "UseChangeAwareIteration": true,
      "EnableAutoConvergence": true,
      "IgnoreFieldsForChangeDetection": ["timestamp"]
    }
  }
}
```

`WorkflowSettingsConfig.MaxIterations` also exists, but the clearest wired path is fluent `WithMaxIterations(...)` or `Settings.IterationOptions.MaxIterations`.

## Checkpointing

`EnableCheckpointing` controls whether workflow execution asks for a checkpoint store. Fluent workflows can enable checkpointing and provide storage without adding extra package-facing setup:

```csharp
var workflow = await AgentWorkflow.Create()
    .WithCheckpointing()
    .WithJsonWorkflowStore("App_Data/workflows")
    .AddAgent("draft", draftConfig)
    .BuildAsync();
```

Use `WithInMemoryWorkflowStore()` for tests and short-lived development runs. Use `WithJsonWorkflowStore(...)` when local checkpoints should survive process restarts.

Workflow stores are for workflow definitions and checkpoints. Use `WithSessionStore(...)` with a [Conversation Policy](conversation-policies.md) when node agent transcripts should be saved into HPD sessions and threads.

## Related Pages

- [Build A Multi-Agent Workflow](build-a-workflow.md)
- [Conversation Policies](conversation-policies.md)
- [Routing And Handoffs](routing-and-handoffs.md)
- [Workflow Events](workflow-events.md)
