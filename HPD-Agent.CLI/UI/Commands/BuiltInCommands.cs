using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using HPD_Agent.CLI.Anthropic;
using HPD_Agent.CLI.Auth;
using HPD_Agent.CLI.Codex;
using HPD_Agent.CLI.Models;
using Microsoft.Extensions.AI;
using Spectre.Console;

/// <summary>
/// Built-in slash commands for HPD Agent.
/// Inspired by Gemini CLI's command set.
/// </summary>
public static class BuiltInCommands
{
    /// <summary>
    /// Create and register all built-in commands.
    /// </summary>
    public static void RegisterAll(CommandRegistry registry)
    {
        registry.RegisterMany(
            CreateHelpCommand(),
            CreateClearCommand(),
            CreateStatsCommand(),
            CreateExitCommand(),
            CreateModelCommand(),
            CreateSessionCommand(),
            CreateSessionsCommand(),
            CreateAudioCommand()
        );
    }
    
    /// <summary>
    /// /help - Display help information
    /// </summary>
    private static SlashCommand CreateHelpCommand()
    {
        return new SlashCommand
        {
            Name = "help",
            AltNames = new List<string> { "?" },
            Description = "Show available commands and usage",
            AutoExecute = true,
            Action = async (ctx) =>
            {
                if (ctx.UIRenderer == null)
                    return CommandResult.Error("UI renderer not available");
                    
                ctx.UIRenderer.ShowHelp();
                return await Task.FromResult(CommandResult.Ok());
            }
        };
    }
    
    /// <summary>
    /// /clear - Clear conversation history
    /// </summary>
    private static SlashCommand CreateClearCommand()
    {
        return new SlashCommand
        {
            Name = "clear",
            AltNames = new List<string> { "cls", "reset" },
            Description = "Clear conversation history and screen",
            AutoExecute = true,
            Action = async (ctx) =>
            {
                if (ctx.UIRenderer == null)
                    return CommandResult.Error("UI renderer not available");
                
                // Clear console
                Console.Clear();
                
                // Clear state
                ctx.UIRenderer.StateManager.ClearHistory();
                
                // Show header again
                ctx.UIRenderer.ShowHeader();
                
                return await Task.FromResult(new CommandResult
                {
                    Success = true,
                    Message = "Conversation cleared",
                    ShouldClearHistory = true
                });
            }
        };
    }
    
    /// <summary>
    /// /stats - Display session statistics
    /// </summary>
    private static SlashCommand CreateStatsCommand()
    {
        return new SlashCommand
        {
            Name = "stats",
            AltNames = new List<string> { "statistics", "info" },
            Description = "Show session statistics (tokens, time, tool calls)",
            AutoExecute = true,
            Action = async (ctx) =>
            {
                if (ctx.UIRenderer == null)
                    return CommandResult.Error("UI renderer not available");
                
                ctx.UIRenderer.ShowStats();
                return await Task.FromResult(CommandResult.Ok());
            }
        };
    }
    
    /// <summary>
    /// /exit - Exit the application
    /// </summary>
    private static SlashCommand CreateExitCommand()
    {
        return new SlashCommand
        {
            Name = "exit",
            AltNames = new List<string> { "quit", "q", "bye" },
            Description = "Exit the application",
            AutoExecute = true,
            Action = async (ctx) =>
            {
                return await Task.FromResult(CommandResult.Exit("Goodbye! ~"));
            }
        };
    }
    
    /// <summary>
    /// /model - Interactive model selection for current provider
    /// /model provider - Interactive provider and model selection
    /// /model provider:model - Direct switch (backward compatible)
    /// </summary>
    private static SlashCommand CreateModelCommand()
    {
        return new SlashCommand
        {
            Name = "model",
            AltNames = new List<string> { "models" },
            Description = "Switch model (/model) or provider (/model provider)",
            AutoExecute = false,
            Action = async (ctx) =>
            {
                // Get agent from context
                if (!ctx.Data.TryGetValue("Agent", out var agentObj) || agentObj is not HPD.Agent.Agent agent)
                {
                    return CommandResult.Error("Agent not available");
                }

                // Get configuration and auth manager
                var config = ctx.Data.TryGetValue("Configuration", out var configObj)
                    ? configObj as Microsoft.Extensions.Configuration.IConfiguration
                    : null;

                AuthManager? authManager = null;
                ctx.Data.TryGetValue("AuthManager", out var authManagerObj);
                authManager = authManagerObj as AuthManager;

                var currentProvider = agent.Config.Provider?.ProviderKey ?? "unknown";
                var currentModel = agent.Config.Provider?.ModelName ?? "unknown";

                var input = ctx.Arguments?.Trim() ?? "";

                // Case 1: /model provider - Interactive provider + model selection
                if (input.Equals("provider", StringComparison.OrdinalIgnoreCase))
                {
                    return await HandleProviderSelectionAsync(ctx, config, authManager, currentProvider);
                }

                // Case 2: /model provider:model - Direct switch (backward compatible)
                if (input.Contains(':'))
                {
                    var parts = input.Split(':', 2);
                    var newProvider = parts[0].Trim().ToLower();
                    var newModel = parts[1].Trim();

                    if (string.IsNullOrEmpty(newModel))
                    {
                        return CommandResult.Error("Model name is required. Usage: /model provider:model");
                    }

                    return await SwitchToModelAsync(ctx, config, authManager, newProvider, newModel);
                }

                // Case 3: /model (no args) - Interactive model selection for current provider
                if (string.IsNullOrWhiteSpace(input))
                {
                    return await HandleModelSelectionAsync(ctx, config, authManager, currentProvider, currentModel);
                }

                // Case 4: /model <model-name> - Switch model on current provider
                return await SwitchToModelAsync(ctx, config, authManager, currentProvider, input);
            }
        };
    }

    /// <summary>
    /// Handle interactive model selection for current provider
    /// </summary>
    private static async Task<CommandResult> HandleModelSelectionAsync(
        CommandContext ctx,
        Microsoft.Extensions.Configuration.IConfiguration? config,
        AuthManager? authManager,
        string currentProvider,
        string currentModel)
    {
        // Show current model
        AnsiConsole.MarkupLine($"[dim]Current: {currentProvider}:{currentModel}[/]");
        AnsiConsole.WriteLine();

        // Get known models for this provider
        var knownModels = KnownModels.GetModelsForProvider(currentProvider);

        // Build selection list with Custom first
        var choices = new List<ModelChoice>
        {
            new ModelChoice("__custom__", "[Custom model name...]", false)
        };

        // Add "Free models" option for OpenRouter
        if (currentProvider.Equals("openrouter", StringComparison.OrdinalIgnoreCase))
        {
            choices.Add(new ModelChoice("__free__", "[Browse free models...]", false));
        }

        // Add known models (recommended first)
        foreach (var model in knownModels.OrderByDescending(m => m.IsRecommended))
        {
            choices.Add(new ModelChoice(model.Id, model.Description, model.IsRecommended));
        }

        var selected = AnsiConsole.Prompt(
            new SelectionPrompt<ModelChoice>()
                .Title($"Select a model for [cyan]{currentProvider}[/]:")
                .PageSize(12)
                .AddChoices(choices)
                .UseConverter(c => c.Display));

        string newModel;
        if (selected.Id == "__custom__")
        {
            newModel = AnsiConsole.Prompt(
                new TextPrompt<string>("Enter model name:")
                    .PromptStyle("green"));

            if (string.IsNullOrWhiteSpace(newModel))
            {
                return CommandResult.Error("Model name cannot be empty");
            }
        }
        else if (selected.Id == "__free__")
        {
            // Fetch and display free models from OpenRouter
            return await HandleOpenRouterFreeModelsAsync(ctx, config, authManager);
        }
        else
        {
            newModel = selected.Id;
        }

        return await SwitchToModelAsync(ctx, config, authManager, currentProvider, newModel);
    }

    /// <summary>
    /// Handle interactive provider + model selection
    /// </summary>
    private static async Task<CommandResult> HandleProviderSelectionAsync(
        CommandContext ctx,
        Microsoft.Extensions.Configuration.IConfiguration? config,
        AuthManager? authManager,
        string currentProvider)
    {
        // Build provider list from config
        var providerChoices = new List<ProviderChoice>();

        if (config != null)
        {
            var providersSection = config.GetSection("Providers");
            foreach (var provider in providersSection.GetChildren())
            {
                var providerKey = provider["ProviderKey"] ?? provider.Key.ToLower();
                var hasApiKey = !string.IsNullOrEmpty(provider["ApiKey"]);

                // Check auth storage
                var hasAuthCredentials = false;
                var authSource = "";
                if (authManager != null)
                {
                    hasAuthCredentials = authManager.Storage.HasCredentialsAsync(providerKey).GetAwaiter().GetResult();
                    if (hasAuthCredentials) authSource = "oauth";
                }

                var isConfigured = hasApiKey || hasAuthCredentials;
                var isCurrent = providerKey.Equals(currentProvider, StringComparison.OrdinalIgnoreCase);

                providerChoices.Add(new ProviderChoice(providerKey, isConfigured, isCurrent, authSource));
            }
        }

        if (providerChoices.Count == 0)
        {
            return CommandResult.Error("No providers configured in appsettings.json");
        }

        // Sort: configured first, then alphabetical
        providerChoices = providerChoices
            .OrderByDescending(p => p.IsConfigured)
            .ThenBy(p => p.Key)
            .ToList();

        var selectedProvider = AnsiConsole.Prompt(
            new SelectionPrompt<ProviderChoice>()
                .Title("Select a [cyan]provider[/]:")
                .PageSize(12)
                .AddChoices(providerChoices)
                .UseConverter(p => p.Display));

        // Now select model for chosen provider
        return await HandleModelSelectionForProviderAsync(ctx, config, authManager, selectedProvider.Key);
    }

    /// <summary>
    /// Handle model selection for a specific provider
    /// </summary>
    private static async Task<CommandResult> HandleModelSelectionForProviderAsync(
        CommandContext ctx,
        Microsoft.Extensions.Configuration.IConfiguration? config,
        AuthManager? authManager,
        string providerKey)
    {
        // Get known models for this provider
        var knownModels = KnownModels.GetModelsForProvider(providerKey);

        // Build selection list with Custom first
        var choices = new List<ModelChoice>
        {
            new ModelChoice("__custom__", "[Custom model name...]", false)
        };

        // Add "Free models" option for OpenRouter
        if (providerKey.Equals("openrouter", StringComparison.OrdinalIgnoreCase))
        {
            choices.Add(new ModelChoice("__free__", "[Browse free models...]", false));
        }

        // Add known models (recommended first)
        foreach (var model in knownModels.OrderByDescending(m => m.IsRecommended))
        {
            choices.Add(new ModelChoice(model.Id, model.Description, model.IsRecommended));
        }

        var selected = AnsiConsole.Prompt(
            new SelectionPrompt<ModelChoice>()
                .Title($"Select a model for [cyan]{providerKey}[/]:")
                .PageSize(12)
                .AddChoices(choices)
                .UseConverter(c => c.Display));

        string newModel;
        if (selected.Id == "__custom__")
        {
            newModel = AnsiConsole.Prompt(
                new TextPrompt<string>("Enter model name:")
                    .PromptStyle("green"));

            if (string.IsNullOrWhiteSpace(newModel))
            {
                return CommandResult.Error("Model name cannot be empty");
            }
        }
        else if (selected.Id == "__free__")
        {
            // Fetch and display free models from OpenRouter
            return await HandleOpenRouterFreeModelsAsync(ctx, config, authManager);
        }
        else
        {
            newModel = selected.Id;
        }

        return await SwitchToModelAsync(ctx, config, authManager, providerKey, newModel);
    }

    /// <summary>
    /// Execute the model switch
    /// </summary>
    private static async Task<CommandResult> SwitchToModelAsync(
        CommandContext ctx,
        Microsoft.Extensions.Configuration.IConfiguration? config,
        AuthManager? authManager,
        string provider,
        string model)
    {
        // Get API key for the provider from auth storage or config
        string? apiKey = null;
        string? baseUrl = null;
        Dictionary<string, string>? customHeaders = null;
        bool isFromAuth = false;
        bool isOAuthBased = false;
        IChatClient? overrideChatClient = null;

        if (authManager != null)
        {
            var resolvedCreds = await authManager.ResolveCredentialsAsync(provider);
            if (resolvedCreds != null)
            {
                apiKey = resolvedCreds.ApiKey;
                baseUrl = resolvedCreds.BaseUrl;
                customHeaders = resolvedCreds.CustomHeaders != null
                    ? new Dictionary<string, string>(resolvedCreds.CustomHeaders)
                    : new Dictionary<string, string>();
                isFromAuth = resolvedCreds.Source.StartsWith("oauth") || resolvedCreds.Source.StartsWith("api");

                // Check if this is OpenAI with OAuth authentication (Codex API)
                // OAuth requires the specialized CodexChatClient, not the standard OpenAI provider
                if (provider.Equals("openai", StringComparison.OrdinalIgnoreCase))
                {
                    isOAuthBased = await CodexClientFactory.IsCodexAuthAsync(authManager);

                    if (isOAuthBased)
                    {
                        // Get session ID from context for Codex headers
                        var sessionId = ctx.Data.TryGetValue("CurrentSessionId", out var sidObj)
                            ? sidObj?.ToString() ?? Guid.NewGuid().ToString()
                            : Guid.NewGuid().ToString();

                        // Required headers per  reference
                        customHeaders["session_id"] = sessionId;
                        customHeaders["User-Agent"] = $"/1.0.0 ({Environment.OSVersion.Platform} {Environment.OSVersion.Version}; {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture})";
                        customHeaders["originator"] = "";

                        // Create CodexChatClient for OAuth-based OpenAI
                        // This bypasses the standard OpenAI provider which doesn't support Codex API
                        var codexOptions = new CodexClientOptions
                        {
                            CustomHeaders = customHeaders,
                            ReasoningEffort = CodexMessageConverter.IsReasoningModel(model) ? "medium" : null
                        };

                        overrideChatClient = CodexClientFactory.Create(resolvedCreds, model, codexOptions);
                        AnsiConsole.MarkupLine($"[dim]Using OAuth authentication (Codex API)[/]");
                    }
                }
                // Check if this is Anthropic with OAuth authentication
                else if (provider.Equals("anthropic", StringComparison.OrdinalIgnoreCase) &&
                         await AnthropicOAuthClientFactory.IsOAuthAsync(authManager))
                {
                    isOAuthBased = true;

                    // Create AnthropicOAuthChatClient for OAuth-based Anthropic
                    // This uses Bearer token auth instead of x-api-key
                    var anthropicCreds = await authManager.ResolveCredentialsAsync("anthropic");
                    if (anthropicCreds != null)
                    {
                        var anthropicOptions = new AnthropicOAuthClientOptions
                        {
                            MaxTokens = 4096,
                            CustomHeaders = anthropicCreds.CustomHeaders != null
                                ? new Dictionary<string, string>(anthropicCreds.CustomHeaders)
                                : null
                        };

                        overrideChatClient = AnthropicOAuthClientFactory.Create(anthropicCreds, model, anthropicOptions);
                        AnsiConsole.MarkupLine($"[dim]Using OAuth authentication (Anthropic Bearer token)[/]");
                    }
                }
                else if (baseUrl?.Contains("codex", StringComparison.OrdinalIgnoreCase) == true)
                {
                    // Non-OpenAI provider but using Codex endpoint (edge case)
                    var sessionId = ctx.Data.TryGetValue("CurrentSessionId", out var sidObj)
                        ? sidObj?.ToString() ?? Guid.NewGuid().ToString()
                        : Guid.NewGuid().ToString();

                    customHeaders["session_id"] = sessionId;
                    customHeaders["User-Agent"] = $"/1.0.0 ({Environment.OSVersion.Platform} {Environment.OSVersion.Version}; {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture})";
                    customHeaders["originator"] = "";
                }
            }
        }

        // Fall back to appsettings.json (only for non-OAuth scenarios)
        if (string.IsNullOrEmpty(apiKey) && config != null && !isOAuthBased)
        {
            var providersSection = config.GetSection("Providers");
            foreach (var p in providersSection.GetChildren())
            {
                var providerKey = p["ProviderKey"] ?? p.Key.ToLower();
                if (providerKey.Equals(provider, StringComparison.OrdinalIgnoreCase))
                {
                    apiKey = p["ApiKey"];
                    break;
                }
            }
        }

        // Store the switch request
        if (!ctx.Data.ContainsKey("ModelSwitchRequest"))
        {
            ctx.Data["ModelSwitchRequest"] = null;
        }

        // Show credential source
        if (string.IsNullOrEmpty(apiKey) && overrideChatClient == null)
        {
            AnsiConsole.MarkupLine($"[yellow]⚠ No API key configured for '{provider}' - using environment variable[/]");
        }
        else if (isFromAuth && !isOAuthBased)
        {
            AnsiConsole.MarkupLine($"[dim]Using credentials from auth storage[/]");
        }

        ctx.Data["ModelSwitchRequest"] = new ModelSwitchRequest
        {
            Provider = provider,
            Model = model,
            ApiKey = apiKey,
            BaseUrl = baseUrl,
            CustomHeaders = customHeaders,
            OverrideChatClient = overrideChatClient,
            IsOAuthBased = isOAuthBased
        };

        return new CommandResult
        {
            Success = true,
            Message = $"Switching to {provider}:{model}...\n[dim](Model will be validated on first use)[/]",
            ShouldSwitchModel = true
        };
    }

    /// <summary>
    /// Fetch and display free models from OpenRouter
    /// </summary>
    private static async Task<CommandResult> HandleOpenRouterFreeModelsAsync(
        CommandContext ctx,
        Microsoft.Extensions.Configuration.IConfiguration? config,
        AuthManager? authManager)
    {
        List<OpenRouterFreeModel> freeModels;

        try
        {
            freeModels = await AnsiConsole.Status()
                .StartAsync("Fetching free models from OpenRouter...", async statusCtx =>
                {
                    statusCtx.Spinner(Spinner.Known.Dots);
                    return await FetchOpenRouterFreeModelsAsync();
                });
        }
        catch (Exception ex)
        {
            return CommandResult.Error($"Failed to fetch free models: {ex.Message}");
        }

        // Filter to only models with tool calling support (required for agents)
        var toolCapableModels = freeModels.Where(m => m.SupportsTools).ToList();

        if (toolCapableModels.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No free models with tool calling support found.[/]");
            return CommandResult.Ok();
        }

        AnsiConsole.MarkupLine($"[green]Found {toolCapableModels.Count} free models with tool support[/]");
        AnsiConsole.MarkupLine("[dim]Note: Free models require allowing prompt logging in OpenRouter privacy settings[/]");
        AnsiConsole.MarkupLine("[dim]Configure at: https://openrouter.ai/settings/privacy[/]");
        AnsiConsole.MarkupLine("[yellow]Warning: Free models may have limited agentic capabilities (weak tool calling)[/]");
        AnsiConsole.WriteLine();

        // Build selection list sorted by name
        var choices = toolCapableModels
            .OrderBy(m => m.Name)
            .ToList();

        var selected = AnsiConsole.Prompt(
            new SelectionPrompt<OpenRouterFreeModel>()
                .Title("Select a [cyan]free model[/]:")
                .PageSize(15)
                .AddChoices(choices)
                .UseConverter(m => $"{m.Id} [dim]- {m.Name}[/]"));

        return await SwitchToModelAsync(ctx, config, authManager, "openrouter", selected.Id);
    }

    /// <summary>
    /// Fetches the list of free models from OpenRouter's API
    /// </summary>
    private static async Task<List<OpenRouterFreeModel>> FetchOpenRouterFreeModelsAsync()
    {
        using var httpClient = new HttpClient();
        httpClient.DefaultRequestHeaders.Add("User-Agent", "HPD-Agent-CLI");

        var response = await httpClient.GetAsync("https://openrouter.ai/api/v1/models");
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);

        var freeModels = new List<OpenRouterFreeModel>();

        if (doc.RootElement.TryGetProperty("data", out var dataArray))
        {
            foreach (var model in dataArray.EnumerateArray())
            {
                var id = model.GetProperty("id").GetString() ?? "";
                var name = model.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null;

                // Check if it's a free model
                var isFree = id.EndsWith(":free", StringComparison.OrdinalIgnoreCase);

                if (!isFree && model.TryGetProperty("pricing", out var pricing))
                {
                    var prompt = pricing.TryGetProperty("prompt", out var promptProp) ? promptProp.GetString() : null;
                    var completion = pricing.TryGetProperty("completion", out var completionProp) ? completionProp.GetString() : null;
                    isFree = prompt == "0" && completion == "0";
                }

                if (isFree)
                {
                    // Check if model supports tool calling
                    var supportsTools = false;
                    if (model.TryGetProperty("supported_parameters", out var supportedParams))
                    {
                        foreach (var param in supportedParams.EnumerateArray())
                        {
                            if (param.GetString() == "tools")
                            {
                                supportsTools = true;
                                break;
                            }
                        }
                    }

                    freeModels.Add(new OpenRouterFreeModel(id, name ?? id, supportsTools));
                }
            }
        }

        return freeModels;
    }

    // Simple record for OpenRouter free models
    private record OpenRouterFreeModel(string Id, string Name, bool SupportsTools);

    // Helper record for model selection
    private record ModelChoice(string Id, string Description, bool IsRecommended)
    {
        public string Display => Id switch
        {
            "__custom__" => "[yellow]» Enter custom model name...[/]",
            "__free__" => "[cyan]» Browse free models...[/]",
            _ => IsRecommended
                ? $"[green]{Id}[/] - {Description} [green](Recommended)[/]"
                : $"{Id} [dim]- {Description}[/]"
        };
    }

    // Helper record for provider selection
    private record ProviderChoice(string Key, bool IsConfigured, bool IsCurrent, string AuthSource)
    {
        public string Display
        {
            get
            {
                var status = IsConfigured ? "[green]✓[/]" : "[red]✗[/]";
                var current = IsCurrent ? " [cyan](current)[/]" : "";
                var auth = !string.IsNullOrEmpty(AuthSource) ? $" [dim]({AuthSource})[/]" : "";
                return $"{status} {Key}{auth}{current}";
            }
        }
    }

    /// <summary>
    /// Request to switch model/provider at runtime
    /// </summary>
    public class ModelSwitchRequest
    {
        public string Provider { get; set; } = "";
        public string Model { get; set; } = "";
        public string? ApiKey { get; set; }
        public string? BaseUrl { get; set; }
        public Dictionary<string, string>? CustomHeaders { get; set; }

        /// <summary>
        /// When set, this client overrides the provider registry entirely.
        /// Used for OAuth-based providers like OpenAI Codex that require a specialized client.
        /// </summary>
        public IChatClient? OverrideChatClient { get; set; }

        /// <summary>
        /// Indicates if this is using OAuth authentication (Codex API for OpenAI).
        /// </summary>
        public bool IsOAuthBased { get; set; }
    }

    /// <summary>
    /// /session - Show current session info or create a new session
    /// </summary>
    private static SlashCommand CreateSessionCommand()
    {
        return new SlashCommand
        {
            Name = "session",
            AltNames = new List<string> { "current" },
            Description = "Show current session or create new (usage: /session [new])",
            AutoExecute = false,
            Action = async (ctx) =>
            {
                if (!ctx.Data.TryGetValue("CurrentSessionId", out var sessionIdObj) ||
                    !ctx.Data.TryGetValue("OnSessionSwitch", out var callbackObj) ||
                    !ctx.Data.TryGetValue("SessionsPath", out var sessionsPathObj))
                {
                    return CommandResult.Error("Session feature not available");
                }

                var currentSessionId = sessionIdObj?.ToString() ?? "unknown";
                var sessionsPath = sessionsPathObj?.ToString() ?? "";
                var callback = callbackObj as Func<string, Task>;
                var args = ctx.Arguments?.Trim().ToLower() ?? "";

                // /session new - Create a new session
                if (args == "new" || args == "create")
                {
                    var newSessionId = $"console-{DateTime.Now:yyyy-MM-dd-HHmmss}-{Guid.NewGuid().ToString()[..8]}";

                    if (callback != null)
                    {
                        // Store the new session ID request
                        ctx.Data["NewSessionRequest"] = newSessionId;
                        return new CommandResult
                        {
                            Success = true,
                            Message = $"Creating new session: {newSessionId}",
                            Data = new Dictionary<string, object> { ["NewSessionId"] = newSessionId }
                        };
                    }

                    return CommandResult.Error("Cannot create new session - callback not available");
                }

                // /session (no args) - Show current session info
                AnsiConsole.MarkupLine("[bold yellow]Current Session[/]");
                AnsiConsole.MarkupLine($"  ID: [cyan]{currentSessionId}[/]");

                // Try to get session file info (JsonSessionStore uses {sessionId}/session.json structure)
                var sessionFile = Path.Combine(sessionsPath, currentSessionId, "session.json");
                if (File.Exists(sessionFile))
                {
                    var fileInfo = new FileInfo(sessionFile);
                    AnsiConsole.MarkupLine($"  Size: [dim]{FormatBytes(fileInfo.Length)}[/]");
                    AnsiConsole.MarkupLine($"  Modified: [dim]{fileInfo.LastWriteTime:g}[/]");
                }

                // Get message count from thread if available
                if (ctx.Data.TryGetValue("Thread", out var threadObj) && threadObj != null)
                {
                    var thread = threadObj as dynamic;
                    try
                    {
                        var messageCount = thread?.Messages?.Count ?? 0;
                        AnsiConsole.MarkupLine($"  Messages: [dim]{messageCount}[/]");
                    }
                    catch { /* ignore */ }
                }

                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine("[dim]Commands:[/]");
                AnsiConsole.MarkupLine("[dim]  /session new    - Start a fresh session[/]");
                AnsiConsole.MarkupLine("[dim]  /sessions       - Browse previous sessions[/]");

                return await Task.FromResult(CommandResult.Ok());
            }
        };
    }

    /// <summary>
    /// /sessions - Browse and restore previous sessions
    /// </summary>
    private static SlashCommand CreateSessionsCommand()
    {
        return new SlashCommand
        {
            Name = "sessions",
            AltNames = new List<string> { "history", "browse" },
            Description = "Browse and restore previous sessions",
            AutoExecute = false,
            Action = async (ctx) =>
            {
                if (ctx.Data == null || !ctx.Data.TryGetValue("SessionsPath", out var sessionsPathObj) ||
                    !ctx.Data.TryGetValue("Agent", out var agentObj) ||
                    !ctx.Data.TryGetValue("OnSessionSwitch", out var callbackObj))
                {
                    return CommandResult.Error("Sessions feature not available");
                }

                var sessionsPath = sessionsPathObj.ToString();
                var agent = agentObj as dynamic;
                var callback = callbackObj as Func<string, Task>;

                if (!Directory.Exists(sessionsPath))
                {
                    return CommandResult.Error("No sessions directory found");
                }

                // Get all session directories (JsonSessionStore uses directory-per-session structure)
                // Each session is stored as: {sessionsPath}/{sessionId}/session.json
                var sessionDirs = Directory.GetDirectories(sessionsPath)
                    .Where(d => File.Exists(Path.Combine(d, "session.json")))  // Only dirs with session.json
                    .OrderByDescending(d => Directory.GetLastWriteTime(d))
                    .ToList();

                if (sessionDirs.Count == 0)
                {
                    return CommandResult.Ok("No previous sessions found");
                }

                // Show session list for selection
                var sessionOptions = sessionDirs.Select(d =>
                {
                    var sessionFile = Path.Combine(d, "session.json");
                    return new
                    {
                        Directory = d,
                        Name = Path.GetFileName(d),
                        Modified = File.GetLastWriteTime(sessionFile).ToString("yyyy-MM-dd HH:mm:ss"),
                        Size = new FileInfo(sessionFile).Length
                    };
                }).ToList();

                // Create display options with info
                var displayOptions = sessionOptions.Select(s =>
                    $"{s.Name} [dim]({s.Modified}, {FormatBytes(s.Size)})[/]"
                ).ToList();

                // Use Spectre.Console for selection
                var selectedIndex = AnsiConsole.Prompt(
                    new SelectionPrompt<int>()
                        .Title("[yellow]Select a session to restore:[/]")
                        .PageSize(10)
                        .MoreChoicesText("[dim](Move up and down to see more)[/]")
                        .AddChoices(Enumerable.Range(0, displayOptions.Count))
                        .UseConverter(i => displayOptions[i])
                );

                var selected = sessionOptions[selectedIndex];
                var sessionId = selected.Name;

                // Restore the session
                try
                {
                    if (callback != null)
                    {
                        await callback(sessionId);
                    }
                    // No message here - the callback already displays session info and history
                    return CommandResult.Ok();
                }
                catch (Exception ex)
                {
                    return CommandResult.Error($"Failed to restore session: {ex.Message}");
                }
            }
        };
    }

    /// <summary>
    /// /audio - Process audio through TTS/STT pipeline
    /// </summary>
    private static SlashCommand CreateAudioCommand()
    {
        return new SlashCommand
        {
            Name = "audio",
            AltNames = new List<string> { "voice", "tts" },
            Description = "Process audio through STT → Agent → TTS pipeline (usage: /audio <path>)",
            AutoExecute = false,
            Action = async (ctx) =>
            {
                if (string.IsNullOrWhiteSpace(ctx.Arguments))
                {
                    return CommandResult.Error("Usage: /audio <input-audio-file-path>");
                }

                var inputPath = ctx.Arguments.Trim().Trim('"');

                if (!File.Exists(inputPath))
                {
                    return CommandResult.Error($"File not found: {inputPath}");
                }

                try
                {
                    AnsiConsole.MarkupLine("[yellow]Processing audio through pipeline...[/]");

                    // Get ElevenLabs API key from configuration or environment
                    var config = ctx.Data.TryGetValue("Configuration", out var configObj)
                        ? configObj as Microsoft.Extensions.Configuration.IConfiguration
                        : null;

                    var apiKey = config?["ElevenLabs:ApiKey"] ?? Environment.GetEnvironmentVariable("ELEVENLABS_API_KEY");
                    if (string.IsNullOrEmpty(apiKey))
                    {
                        return CommandResult.Error("ElevenLabs:ApiKey not set in appsettings.json or ELEVENLABS_API_KEY environment variable");
                    }

                    // Get agent from context
                    if (!ctx.Data.TryGetValue("Agent", out var agentObj) || agentObj is not HPD.Agent.Agent agent)
                    {
                        return CommandResult.Error("Agent not available in context");
                    }

                    // Create ElevenLabs STT client
                    var sttConfig = new HPD.Agent.Audio.ElevenLabs.ElevenLabsAudioConfig
                    {
                        ApiKey = apiKey
                    };
                    var sttClient = new HPD.Agent.Audio.ElevenLabs.ElevenLabsSpeechToTextClient(sttConfig);

                    // Step 1: STT - Convert audio to text
                    AnsiConsole.MarkupLine("[cyan]Step 1/3:[/] Transcribing audio to text...");
                    string transcribedText;
                    await using (var audioStream = File.OpenRead(inputPath))
                    {
                        var sttResponse = await sttClient.GetTextAsync(audioStream);
                        transcribedText = sttResponse.Text;
                        AnsiConsole.MarkupLine($"[green]✓[/] Transcribed: [white]{transcribedText}[/]");
                    }

                    // Step 2: Agent - Send to agent and get response
                    AnsiConsole.MarkupLine("[cyan]Step 2/3:[/] Sending to agent...");
                    string responseText = string.Empty;
                    await foreach (var evt in agent.RunAsync(transcribedText))
                    {
                        if (evt is HPD.Agent.TextDeltaEvent textDelta)
                        {
                            responseText += textDelta.Text;
                        }
                    }
                    AnsiConsole.MarkupLine($"[green]✓[/] Agent response: [white]{responseText}[/]");

                    // Step 3: TTS - Convert agent response to speech
                    AnsiConsole.MarkupLine("[cyan]Step 3/3:[/] Converting response to speech...");
                    var ttsConfig = new HPD.Agent.Audio.ElevenLabs.ElevenLabsAudioConfig
                    {
                        ApiKey = apiKey
                    };
                    var ttsClient = new HPD.Agent.Audio.ElevenLabs.ElevenLabsTextToSpeechClient(ttsConfig);

                    var ttsResponse = await ttsClient.GetSpeechAsync(responseText);

                    // Save output audio next to input file
                    var inputDir = Path.GetDirectoryName(inputPath) ?? ".";
                    var inputFileName = Path.GetFileNameWithoutExtension(inputPath);
                    var outputPath = Path.Combine(inputDir, $"{inputFileName}_output.mp3");

                    if (ttsResponse.Audio?.Data != null)
                    {
                        var audioData = ttsResponse.Audio.Data.ToArray();
                        await File.WriteAllBytesAsync(outputPath, audioData);

                        AnsiConsole.MarkupLine($"[green]✓[/] Audio saved to: [blue]{outputPath}[/]");
                        AnsiConsole.MarkupLine($"[dim]Size: {FormatBytes(audioData.Length)}[/]");

                        return CommandResult.Ok($"Pipeline complete! Output: {outputPath}");
                    }
                    else
                    {
                        return CommandResult.Error("TTS response contained no audio data");
                    }
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine($"[red]Error:[/] {ex.Message}");
                    return CommandResult.Error($"Audio processing failed: {ex.Message}");
                }
            }
        };
    }

    /// <summary>
    /// Format bytes to human-readable size
    /// </summary>
    private static string FormatBytes(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB" };
        double len = bytes;
        int order = 0;
        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len = len / 1024;
        }
        return $"{len:0.##} {sizes[order]}";
    }
}
