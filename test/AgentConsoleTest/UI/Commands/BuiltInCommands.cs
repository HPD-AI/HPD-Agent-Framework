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
    /// /model - Show current model information (placeholder for future expansion)
    /// </summary>
    private static SlashCommand CreateModelCommand()
    {
        return new SlashCommand
        {
            Name = "model",
            Description = "Show current AI model information",
            AutoExecute = true,
            Action = async (ctx) =>
            {
                // Placeholder - can be expanded to show/switch models
                var message = "Model information:\n" +
                            "  Current: (configured model)\n" +
                            "  Provider: (configured provider)\n" +
                            "  Status: Active";
                
                return await Task.FromResult(CommandResult.Ok(message));
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

                // Get all session files
                var sessionFiles = Directory.GetFiles(sessionsPath, "*.json")
                    .OrderByDescending(f => File.GetLastWriteTime(f))
                    .ToList();

                if (sessionFiles.Count == 0)
                {
                    return CommandResult.Ok("No previous sessions found");
                }

                // Show session list for selection
                var sessionOptions = sessionFiles.Select(f => new
                {
                    File = f,
                    Name = Path.GetFileNameWithoutExtension(f),
                    Modified = File.GetLastWriteTime(f).ToString("yyyy-MM-dd HH:mm:ss"),
                    Size = new FileInfo(f).Length
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
                    return CommandResult.Ok($"Restored session: {sessionId}");
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
