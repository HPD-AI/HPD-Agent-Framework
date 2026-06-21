# TUI Composition

`HpdAgentTuiBuilder` composes the terminal shell. Use it to install defaults, render events, add commands and pages, configure shortcuts, and connect app policy to TUI interactions.

## Start With Defaults

```csharp
await using var app = HpdAgentTuiApp.Create(
    runtime,
    scope,
    tui => tui.AddAgentTuiDefaults());
```

`AddAgentTuiDefaults()` installs the default header, footer, prompt, shell layout, slash-command autocomplete, help page, `/help`, and `/clear`.

Defaults are shell mechanics. Add your own event handlers for the transcript, tools, cards, product events, or custom state your users need to see.

The sample names below such as `TextMessageStreamHandler`, `ContextWidget`, `ModelStatusItem`, `MyAutocompleteProvider`, and `MyPermissionInteractionHandler` are application-owned examples, not framework-provided types.

## Add, TryAdd, Replace

Most composition APIs come in three forms:

```csharp
tui.AddSlashCommand(command);
tui.TryAddSlashCommand(command);
tui.ReplaceSlashCommand(command);
```

`Add*` is strict and throws on a duplicate key.

`TryAdd*` keeps the existing contribution when the key is already present.

`Replace*` overwrites an existing contribution or supplies a new one.

This lets libraries safely install defaults with `TryAdd*`, while applications can intentionally replace them.

## Event Handlers

```csharp
tui.AddEventHandler("myapp.text", new TextMessageStreamHandler());
```

Event handlers decide how `AgentEvent` instances affect the terminal shell. A handler can update transcript rows, add status output, track event state, or ignore events that are not relevant to the current surface.

Use stable keys for handlers so your app can replace or suppress behavior deliberately.

## Pages And Commands

```csharp
tui
    .AddPage("myapp.status", context => new Text("Agent status"))
    .AddSlashCommand(new HpdAgentTuiCommandDescriptor(
        "status",
        context => context.Navigation.GoToPage("myapp.status")));
```

Pages render alternate terminal surfaces. Slash commands let users invoke shell behavior without sending text to the agent.

## Widgets And Status Items

Widgets attach UI to shell slots. Status items expose compact runtime state such as model selection, connection state, or the active thread.

```csharp
tui.AddWidget(TuiSlot.BelowEditor, "myapp.context", new ContextWidget());
tui.AddStatusItem("myapp.model", new ModelStatusItem());
```

Keep widgets app-owned. The TUI framework provides placement and lifecycle; your component decides what the information means.

## Shortcuts And Autocomplete

```csharp
tui.AddShortcut(new HpdAgentTuiShortcutDescriptor(
    "myapp.backToTranscript",
    new KeyGesture(KeyCode.Escape),
    context => context.Navigation.GoToTranscript()));

tui.AddAutocompleteProvider("myapp.commands", new MyAutocompleteProvider());
```

Shortcuts should map to clear shell actions. Autocomplete providers can suggest slash commands, agent ids, filenames, or app-specific tokens, depending on what your app supports.

## Run Configuration

The prompt submission path can attach an `AgentRunConfig` to the input event:

```csharp
tui.SetRunConfigComposer(context => new AgentRunConfig
{
    ProviderKey = "openai",
    ModelId = "gpt-5-mini"
});
```

Model selection helpers are composition conveniences. They do not change the underlying rule: each submitted prompt can carry run configuration into the agent runtime.

## Interaction Handlers

```csharp
tui.AddInteractionHandler(
    "myapp.permissions",
    new MyPermissionInteractionHandler());
```

Interaction handlers handle request/response workflows such as permissions, continuations, clarifications, and client tools. Keep product policy in your app layer. For example, a development TUI might allow an approval prompt, while a production TUI might deny or restrict the same request.

## Composition Boundary

Provider pickers, model catalogs, coding harness UI, and product-specific diagnostics can all be layered through composition, but they are not core TUI guarantees. Treat them as app features built on the shell primitives.

