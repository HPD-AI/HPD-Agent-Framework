# OpenAI And Azure OpenAI

The `HPD-Agent.Providers.OpenAI` package registers two provider keys:

- `openai` for OpenAI models.
- `azure-openai` for traditional Azure OpenAI resources.

Use `openai` when `ModelName` is an OpenAI model id. Use `azure-openai` when `ModelName` is an Azure OpenAI deployment name.

## OpenAI

Set an API key:

```bash
export OPENAI_API_KEY="..."
```

Then configure the agent with the fluent helper:

```csharp
using HPD.Agent;
using HPD.Agent.Providers.OpenAI;

var agent = await new AgentBuilder()
    .WithOpenAI(model: "gpt-5-mini")
    .BuildAsync();

var result = await agent.RunAsync("Write one sentence about OpenAI setup.");
Console.WriteLine(result.Text);
```

The equivalent `Clients.Chat` shape is:

```json
{
  "Clients": {
    "Chat": {
      "ProviderKey": "openai",
      "ModelName": "gpt-4o"
    }
  }
}
```

`Endpoint` can be used for a custom OpenAI-compatible endpoint. The API key alias is `OPENAI_API_KEY`; custom endpoint lookup follows the environment naming convention `OPENAI_ENDPOINT`. Missing credentials fail at `BuildAsync()` before a live model call.

The OpenAI provider package also contributes image generation, embeddings, and hosted-file clients under the `openai` key. Configure those through the matching `Clients.ImageGeneration`, `Clients.Embeddings`, or `Clients.HostedFiles` family slot when your app needs them. Audio lives in the separate OpenAI audio provider package.

## Azure OpenAI

Set the traditional Azure OpenAI endpoint and API key:

```bash
export AZURE_OPENAI_ENDPOINT="https://YOUR-RESOURCE.openai.azure.com/"
export AZURE_OPENAI_API_KEY="..."
```

Then configure the agent with the deployment name:

```csharp
using HPD.Agent;
using HPD.Agent.Providers.OpenAI;

var agent = await new AgentBuilder()
    .WithAzureOpenAI(
        endpoint: "https://YOUR-RESOURCE.openai.azure.com/",
        apiKey: Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY"),
        model: "YOUR-DEPLOYMENT-NAME")
    .BuildAsync();

var result = await agent.RunAsync("Write one sentence about Azure OpenAI setup.");
Console.WriteLine(result.Text);
```

The equivalent `Clients.Chat` shape is:

```json
{
  "Clients": {
    "Chat": {
      "ProviderKey": "azure-openai",
      "Endpoint": "https://YOUR-RESOURCE.openai.azure.com/",
      "ApiKey": "...",
      "ModelName": "YOUR-DEPLOYMENT-NAME"
    }
  }
}
```

`azure-openai` means traditional Azure OpenAI. It is not the Azure AI Foundry-first path. Azure AI Projects / Foundry uses the `azure-ai` provider key and has different endpoint/auth requirements.

The Azure OpenAI provider package also contributes image generation, embeddings, and hosted-file clients under the `azure-openai` key. The model value is still a deployment name for model-scoped families.

## API Surface

HPD provider docs are organized by client family, not by provider-specific agent subclasses.

`Clients.Chat` is the normal text model path. Tools, permissions, middleware, sessions, threads, events, and hosting stay in HPD's runtime.

`Clients.HostedFiles` is used for provider-native file upload when the content upload strategy chooses hosted files. The framework content store remains the app-owned fallback or local path.

OpenAI realtime audio is configured by the OpenAI audio provider under `Clients.Realtime`; it is separate from the chat provider and from finite speech-to-text.

## Setup Caveat

Provider validation checks API key, endpoint, and model values. Explicit API key, endpoint, and model values remain the clearest sample shape when you want configuration to be self-contained.
