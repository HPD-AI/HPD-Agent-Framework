using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
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
                return await Task.FromResult(CommandResult.Exit("Goodbye! 👋"));
            }
        };
    }
    
    /// <summary>
    /// /model - Show current model information and optionally switch models
    /// </summary>
    private static SlashCommand CreateModelCommand()
    {
        return new SlashCommand
        {
            Name = "model",
            AltNames = new List<string> { "models" },
            Description = "Show/switch current AI model (usage: /model [provider:model])",
            AutoExecute = false,
            Action = async (ctx) =>
            {
                // Get agent from context
                if (!ctx.Data.TryGetValue("Agent", out var agentObj) || agentObj is not HPD.Agent.Agent agent)
                {
                    return CommandResult.Error("Agent not available");
                }

                // Get configuration for available providers
                var config = ctx.Data.TryGetValue("Configuration", out var configObj)
                    ? configObj as Microsoft.Extensions.Configuration.IConfiguration
                    : null;

                var currentProvider = agent.Config.Provider?.ProviderKey ?? "unknown";
                var currentModel = agent.Config.Provider?.ModelName ?? "unknown";

                // If no arguments, show current model and available options
                if (string.IsNullOrWhiteSpace(ctx.Arguments))
                {
                    // Show current model
                    AnsiConsole.MarkupLine("[bold yellow]Current Model[/]");
                    AnsiConsole.MarkupLine($"  Provider: [cyan]{currentProvider}[/]");
                    AnsiConsole.MarkupLine($"  Model:    [green]{currentModel}[/]");
                    AnsiConsole.WriteLine();

                    // Show configured providers from appsettings.json
                    if (config != null)
                    {
                        var providersSection = config.GetSection("Providers");
                        var providers = providersSection.GetChildren().ToList();

                        if (providers.Count > 0)
                        {
                            AnsiConsole.MarkupLine("[bold yellow]Configured Providers[/]");
                            foreach (var provider in providers)
                            {
                                var providerKey = provider["ProviderKey"] ?? provider.Key.ToLower();
                                var hasApiKey = !string.IsNullOrEmpty(provider["ApiKey"]);
                                var status = hasApiKey ? "[green]✓[/]" : "[red]✗[/]";
                                AnsiConsole.MarkupLine($"  {status} [cyan]{providerKey}[/]");
                            }
                            AnsiConsole.WriteLine();
                        }
                    }

                    // Show usage
                    AnsiConsole.MarkupLine("[dim]Usage: /model <provider>:<model-name>[/]");
                    AnsiConsole.MarkupLine("[dim]Example: /model openrouter:anthropic/claude-3.5-sonnet[/]");
                    AnsiConsole.MarkupLine("[dim]Example: /model anthropic:claude-3-5-sonnet-20241022[/]");
                    AnsiConsole.MarkupLine("[dim]Example: /model ollama:llama3.2[/]");

                    return await Task.FromResult(CommandResult.Ok());
                }

                // Parse provider:model argument
                var input = ctx.Arguments.Trim();
                string newProvider;
                string newModel;

                if (input.Contains(':'))
                {
                    var parts = input.Split(':', 2);
                    newProvider = parts[0].Trim().ToLower();
                    newModel = parts[1].Trim();
                }
                else
                {
                    // Assume same provider, just changing model
                    newProvider = currentProvider;
                    newModel = input;
                }

                if (string.IsNullOrEmpty(newModel))
                {
                    return CommandResult.Error("Model name is required. Usage: /model <provider>:<model>");
                }

                // Get API key for the provider from config
                string? apiKey = null;
                if (config != null)
                {
                    // Try to find provider config (case-insensitive)
                    var providersSection = config.GetSection("Providers");
                    foreach (var provider in providersSection.GetChildren())
                    {
                        var providerKey = provider["ProviderKey"] ?? provider.Key.ToLower();
                        if (providerKey.Equals(newProvider, StringComparison.OrdinalIgnoreCase))
                        {
                            apiKey = provider["ApiKey"];
                            break;
                        }
                    }
                }

                // Store the switch request in context for the main loop to handle
                if (!ctx.Data.ContainsKey("ModelSwitchRequest"))
                {
                    ctx.Data["ModelSwitchRequest"] = null;
                }

                ctx.Data["ModelSwitchRequest"] = new ModelSwitchRequest
                {
                    Provider = newProvider,
                    Model = newModel,
                    ApiKey = apiKey
                };

                return new CommandResult
                {
                    Success = true,
                    Message = $"Switching to {newProvider}:{newModel}...",
                    ShouldSwitchModel = true
                };
            }
        };
    }

    /// <summary>
    /// Request to switch model/provider at runtime
    /// </summary>
    public class ModelSwitchRequest
    {
        public string Provider { get; set; } = "";
        public string Model { get; set; } = "";
        public string? ApiKey { get; set; }
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
