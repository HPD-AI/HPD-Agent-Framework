using HPD_Agent.CLI.Auth.Providers;
using Spectre.Console;

namespace HPD_Agent.CLI.Auth;

/// <summary>
/// CLI commands for managing authentication.
/// Provides /auth login, logout, list, and status commands.
/// </summary>
public static class AuthCommands
{
    /// <summary>
    /// Handles the /auth command and its subcommands.
    /// </summary>
    public static async Task HandleAuthCommandAsync(AuthManager authManager, string[] args, CancellationToken cancellationToken = default)
    {
        if (args.Length == 0)
        {
            ShowAuthHelp();
            return;
        }

        var subCommand = args[0].ToLowerInvariant();
        var subArgs = args.Skip(1).ToArray();

        switch (subCommand)
        {
            case "login":
                await LoginAsync(authManager, subArgs, cancellationToken);
                break;
            case "logout":
                await LogoutAsync(authManager, subArgs);
                break;
            case "list":
                await ListAsync(authManager);
                break;
            case "status":
                await StatusAsync(authManager, subArgs);
                break;
            default:
                AnsiConsole.MarkupLine($"[red]Unknown auth command:[/] {subCommand}");
                ShowAuthHelp();
                break;
        }
    }

    private static void ShowAuthHelp()
    {
        AnsiConsole.MarkupLine("[bold]Authentication Commands[/]");
        AnsiConsole.WriteLine();

        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("Command")
            .AddColumn("Description");

        table.AddRow("[cyan]/auth login[/] [dim][[provider]][/]", "Authenticate with a provider");
        table.AddRow("[cyan]/auth logout[/] [dim]<provider>[/]", "Remove credentials for a provider");
        table.AddRow("[cyan]/auth list[/]", "List all authenticated providers");
        table.AddRow("[cyan]/auth status[/] [dim][[provider]][/]", "Check token status");

        AnsiConsole.Write(table);
    }

    /// <summary>
    /// Handles /auth login [provider]
    /// </summary>
    public static async Task LoginAsync(AuthManager authManager, string[] args, CancellationToken cancellationToken)
    {
        IAuthProvider provider;

        if (args.Length > 0)
        {
            // Provider specified
            var providerId = args[0];
            provider = authManager.GetProvider(providerId)
                ?? throw new InvalidOperationException($"Unknown provider: {providerId}");
        }
        else
        {
            // Interactive provider selection
            provider = SelectProvider(authManager);
        }

        AnsiConsole.MarkupLine($"\n[bold]Authenticating with {provider.DisplayName}[/]\n");

        // Select auth method
        var method = SelectAuthMethod(provider);

        // Handle API key input specially
        if (method.Type == AuthType.ApiKey)
        {
            await HandleApiKeyInputAsync(authManager, provider, cancellationToken);
            return;
        }

        // Start the auth flow
        AnsiConsole.MarkupLine("[dim]Starting authentication flow...[/]");

        var result = await method.StartFlow(cancellationToken);
        await HandleAuthFlowResultAsync(authManager, provider, result, cancellationToken);
    }

    private static IAuthProvider SelectProvider(AuthManager authManager)
    {
        var choices = authManager.Providers
            .Select(p => new ProviderChoice(p))
            .ToList();

        var selected = AnsiConsole.Prompt(
            new SelectionPrompt<ProviderChoice>()
                .Title("Select a [green]provider[/] to authenticate:")
                .PageSize(10)
                .AddChoices(choices)
                .UseConverter(c => c.ToString()));

        return selected.Provider;
    }

    private static AuthMethod SelectAuthMethod(IAuthProvider provider)
    {
        if (provider.Methods.Count == 1)
        {
            return provider.Methods[0];
        }

        var choices = provider.Methods.ToList();
        var selected = AnsiConsole.Prompt(
            new SelectionPrompt<AuthMethod>()
                .Title("Select an [green]authentication method[/]:")
                .PageSize(10)
                .AddChoices(choices)
                .UseConverter(m =>
                {
                    var label = m.Label;
                    if (m.IsRecommended) label += " [green](Recommended)[/]";
                    if (!string.IsNullOrEmpty(m.Description)) label += $" [dim]- {m.Description}[/]";
                    return label;
                }));

        return selected;
    }

    private static async Task HandleApiKeyInputAsync(AuthManager authManager, IAuthProvider provider, CancellationToken cancellationToken)
    {
        var apiKey = AnsiConsole.Prompt(
            new TextPrompt<string>($"Enter your [green]{provider.DisplayName}[/] API key:")
                .Secret());

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            AnsiConsole.MarkupLine("[red]API key cannot be empty.[/]");
            return;
        }

        var entry = GenericApiKeyAuthProvider.CreateApiKeyEntry(apiKey);
        await authManager.Storage.SetAsync(provider.ProviderId, entry);

        AnsiConsole.MarkupLine($"\n[green]✓[/] Authenticated with {provider.DisplayName}");
        AnsiConsole.MarkupLine($"[dim]Credentials saved to {authManager.Storage.FilePath}[/]");
    }

    private static async Task HandleAuthFlowResultAsync(
        AuthManager authManager,
        IAuthProvider provider,
        AuthFlowResult result,
        CancellationToken cancellationToken)
    {
        switch (result)
        {
            case AuthFlowResult.Success success:
                await authManager.Storage.SetAsync(provider.ProviderId, success.Entry);
                AnsiConsole.MarkupLine($"\n[green]✓[/] Authenticated with {provider.DisplayName}");

                if (success.Entry is OAuthEntry oauth)
                {
                    if (!string.IsNullOrEmpty(oauth.AccountId))
                    {
                        AnsiConsole.MarkupLine($"  [dim]Account:[/] {oauth.AccountId}");
                    }
                    AnsiConsole.MarkupLine($"  [dim]Expires:[/] {oauth.ExpiresAt:yyyy-MM-dd HH:mm:ss} UTC");
                }

                AnsiConsole.MarkupLine($"[dim]Credentials saved to {authManager.Storage.FilePath}[/]");
                break;

            case AuthFlowResult.PendingUserAction pending:
                // Display instructions to user
                AnsiConsole.WriteLine();

                if (!string.IsNullOrEmpty(pending.UserCode))
                {
                    var panel = new Panel(new Markup($"[bold yellow]{pending.UserCode}[/]"))
                        .Header("[bold]Enter this code[/]")
                        .Border(BoxBorder.Rounded)
                        .Padding(2, 1);
                    AnsiConsole.Write(panel);
                }

                if (!string.IsNullOrEmpty(pending.Url))
                {
                    AnsiConsole.MarkupLine($"\nOpen: [link={pending.Url}]{pending.Url}[/]");

                    // Try to open browser
                    if (OAuthHelpers.OpenBrowser(pending.Url))
                    {
                        AnsiConsole.MarkupLine("[dim]Browser opened automatically.[/]");
                    }
                }

                if (!string.IsNullOrEmpty(pending.Message))
                {
                    AnsiConsole.MarkupLine($"\n[dim]{pending.Message}[/]");
                }

                // Wait for completion with spinner
                AnsiConsole.WriteLine();
                var completionResult = await AnsiConsole.Status()
                    .StartAsync("Waiting for authorization...", async ctx =>
                    {
                        ctx.Spinner(Spinner.Known.Dots);
                        return await pending.WaitForCompletion(cancellationToken);
                    });

                // Handle the completion result recursively
                await HandleAuthFlowResultAsync(authManager, provider, completionResult, cancellationToken);
                break;

            case AuthFlowResult.Cancelled:
                AnsiConsole.MarkupLine("[yellow]Authentication cancelled.[/]");
                break;

            case AuthFlowResult.Failed failed:
                AnsiConsole.MarkupLine($"[red]✗ Authentication failed:[/] {failed.Error}");
                if (failed.Exception != null)
                {
                    AnsiConsole.WriteException(failed.Exception, ExceptionFormats.ShortenEverything);
                }
                break;

            case AuthFlowResult.NeedsUserInput needsInput:
                // Prompt user to enter a value (e.g., authorization code from browser)
                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine($"[dim]{needsInput.Prompt}[/]");
                AnsiConsole.WriteLine();

                var userInput = AnsiConsole.Prompt(
                    new TextPrompt<string>($"[green]{needsInput.InputLabel}:[/]")
                        .AllowEmpty());

                if (string.IsNullOrWhiteSpace(userInput))
                {
                    AnsiConsole.MarkupLine("[yellow]No input provided. Authentication cancelled.[/]");
                    return;
                }

                // Complete the flow with user's input
                AnsiConsole.WriteLine();
                var inputResult = await AnsiConsole.Status()
                    .StartAsync("Exchanging code for tokens...", async ctx =>
                    {
                        ctx.Spinner(Spinner.Known.Dots);
                        return await needsInput.CompleteWithInput(userInput.Trim(), cancellationToken);
                    });

                // Handle the result recursively
                await HandleAuthFlowResultAsync(authManager, provider, inputResult, cancellationToken);
                break;
        }
    }

    /// <summary>
    /// Handles /auth logout <provider>
    /// </summary>
    public static async Task LogoutAsync(AuthManager authManager, string[] args)
    {
        if (args.Length == 0)
        {
            AnsiConsole.MarkupLine("[red]Please specify a provider to logout from.[/]");
            AnsiConsole.MarkupLine("[dim]Usage: /auth logout <provider>[/]");
            return;
        }

        var providerId = args[0].ToLowerInvariant();
        var provider = authManager.GetProvider(providerId);
        var displayName = provider?.DisplayName ?? providerId;

        var removed = await authManager.Storage.RemoveAsync(providerId);

        if (removed)
        {
            AnsiConsole.MarkupLine($"[green]✓[/] Logged out from {displayName}");
        }
        else
        {
            AnsiConsole.MarkupLine($"[yellow]No credentials found for {displayName}[/]");
        }
    }

    /// <summary>
    /// Handles /auth list
    /// </summary>
    public static async Task ListAsync(AuthManager authManager)
    {
        var summaries = await authManager.GetAuthSummaryAsync();

        AnsiConsole.MarkupLine($"\n[bold]Credentials[/] [dim]{authManager.Storage.FilePath}[/]\n");

        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("Provider")
            .AddColumn("Status")
            .AddColumn("Source")
            .AddColumn("Details");

        var authenticatedCount = 0;
        var envCount = 0;

        foreach (var summary in summaries)
        {
            string status;
            string details = "";

            if (summary.IsAuthenticated)
            {
                if (summary.IsExpired)
                {
                    status = "[red]expired[/]";
                }
                else if (summary.ExpiresAt.HasValue)
                {
                    var remaining = summary.ExpiresAt.Value - DateTimeOffset.UtcNow;
                    status = "[green]authenticated[/]";
                    details = $"expires in {OAuthHelpers.FormatTimeRemaining(remaining)}";
                }
                else
                {
                    status = "[green]authenticated[/]";
                }

                if (!string.IsNullOrEmpty(summary.AccountId))
                {
                    details = string.IsNullOrEmpty(details)
                        ? summary.AccountId
                        : $"{summary.AccountId}, {details}";
                }

                if (summary.Source?.StartsWith("env:") == true)
                {
                    envCount++;
                }
                else
                {
                    authenticatedCount++;
                }
            }
            else
            {
                status = "[dim]not authenticated[/]";
            }

            var source = summary.Source ?? "-";
            if (source.StartsWith("env:"))
            {
                source = $"[cyan]{source}[/]";
            }
            else if (source == "oauth")
            {
                source = "[magenta]oauth[/]";
            }
            else if (source == "api")
            {
                source = "[blue]api[/]";
            }

            table.AddRow(
                summary.DisplayName,
                status,
                source,
                details);
        }

        AnsiConsole.Write(table);

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[dim]{authenticatedCount} credential(s), {envCount} environment variable(s)[/]");
    }

    /// <summary>
    /// Handles /auth status [provider]
    /// </summary>
    public static async Task StatusAsync(AuthManager authManager, string[] args)
    {
        if (args.Length == 0)
        {
            // Show status for all authenticated providers
            await ListAsync(authManager);
            return;
        }

        var providerId = args[0].ToLowerInvariant();
        var provider = authManager.GetProvider(providerId);
        var displayName = provider?.DisplayName ?? providerId;

        var entry = await authManager.Storage.GetAsync(providerId);

        if (entry == null)
        {
            // Check environment variables
            if (provider != null)
            {
                foreach (var envVar in provider.EnvironmentVariables)
                {
                    var value = Environment.GetEnvironmentVariable(envVar);
                    if (!string.IsNullOrEmpty(value))
                    {
                        AnsiConsole.MarkupLine($"[bold]{displayName}[/]");
                        AnsiConsole.MarkupLine($"  [dim]Type:[/] Environment variable");
                        AnsiConsole.MarkupLine($"  [dim]Source:[/] {envVar}");
                        AnsiConsole.MarkupLine($"  [dim]Value:[/] {OAuthHelpers.MaskToken(value)}");
                        AnsiConsole.MarkupLine($"\n[green]✓[/] Credentials available via environment variable");
                        return;
                    }
                }
            }

            AnsiConsole.MarkupLine($"[yellow]No credentials found for {displayName}[/]");
            AnsiConsole.MarkupLine($"[dim]Run '/auth login {providerId}' to authenticate.[/]");
            return;
        }

        AnsiConsole.MarkupLine($"\n[bold]{displayName}[/]\n");

        switch (entry)
        {
            case OAuthEntry oauth:
                AnsiConsole.MarkupLine($"  [dim]Type:[/] OAuth");
                AnsiConsole.MarkupLine($"  [dim]Access Token:[/] {OAuthHelpers.MaskToken(oauth.AccessToken)}");
                AnsiConsole.MarkupLine($"  [dim]Refresh Token:[/] {OAuthHelpers.MaskToken(oauth.RefreshToken)}");

                if (!string.IsNullOrEmpty(oauth.AccountId))
                {
                    AnsiConsole.MarkupLine($"  [dim]Account ID:[/] {oauth.AccountId}");
                }

                if (!string.IsNullOrEmpty(oauth.EnterpriseUrl))
                {
                    AnsiConsole.MarkupLine($"  [dim]Enterprise URL:[/] {oauth.EnterpriseUrl}");
                }

                AnsiConsole.MarkupLine($"  [dim]Expires:[/] {oauth.ExpiresAt:yyyy-MM-dd HH:mm:ss} UTC");

                AnsiConsole.WriteLine();
                if (oauth.IsExpired)
                {
                    AnsiConsole.MarkupLine("[red]✗ Token has expired[/]");
                    AnsiConsole.MarkupLine($"[dim]Run '/auth login {providerId}' to re-authenticate.[/]");
                }
                else
                {
                    var remaining = oauth.TimeRemaining;
                    AnsiConsole.MarkupLine($"[green]✓[/] Token is valid (expires in {OAuthHelpers.FormatTimeRemaining(remaining)})");
                }
                break;

            case ApiKeyEntry apiKey:
                AnsiConsole.MarkupLine($"  [dim]Type:[/] API Key");
                AnsiConsole.MarkupLine($"  [dim]Key:[/] {OAuthHelpers.MaskToken(apiKey.Key)}");
                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine("[green]✓[/] API key configured");
                break;

            case WellKnownEntry wellKnown:
                AnsiConsole.MarkupLine($"  [dim]Type:[/] Environment Variable Reference");
                AnsiConsole.MarkupLine($"  [dim]Variable:[/] {wellKnown.EnvVarName}");

                var currentValue = Environment.GetEnvironmentVariable(wellKnown.EnvVarName);
                if (!string.IsNullOrEmpty(currentValue))
                {
                    AnsiConsole.MarkupLine($"  [dim]Value:[/] {OAuthHelpers.MaskToken(currentValue)}");
                    AnsiConsole.WriteLine();
                    AnsiConsole.MarkupLine("[green]✓[/] Environment variable is set");
                }
                else
                {
                    AnsiConsole.WriteLine();
                    AnsiConsole.MarkupLine($"[red]✗ Environment variable {wellKnown.EnvVarName} is not set[/]");
                }
                break;
        }
    }

    private record ProviderChoice(IAuthProvider Provider)
    {
        public override string ToString()
        {
            var hasOAuth = Provider.Methods.Any(m =>
                m.Type == AuthType.OAuthBrowser ||
                m.Type == AuthType.OAuthDeviceCode ||
                m.Type == AuthType.OAuthManualCode);

            var suffix = hasOAuth ? " [magenta](OAuth)[/]" : " [dim](API key)[/]";
            return Provider.DisplayName + suffix;
        }
    }
}
