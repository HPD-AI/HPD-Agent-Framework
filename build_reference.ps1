# PowerShell script to clone or update reference repositories

$REFERENCE_DIR = "Reference"

# List your reference repositories here
$REPOS = @(
    "https://github.com/dotnet/extensions.git",
    "https://github.com/microsoft/semantic-kernel.git",
    "https://github.com/microsoft/agent-framework.git",
    "https://github.com/google-gemini/gemini-cli.git",
    "https://github.com/langchain-ai/langgraph.git",
    "https://github.com/pydantic/pydantic-ai.git",
    "https://github.com/strands-agents/sdk-python.git",
    "https://github.com/ag-ui-protocol/ag-ui.git",
    "https://github.com/google-gemini/gemini-cli.git",
    "https://github.com/openai/codex.git"
    # Add more repo URLs as needed
)

if (!(Test-Path $REFERENCE_DIR)) {
    New-Item -ItemType Directory -Path $REFERENCE_DIR | Out-Null
}

foreach ($repo in $REPOS) {
    $repo_name = [System.IO.Path]::GetFileNameWithoutExtension($repo)
    $target_dir = Join-Path $REFERENCE_DIR $repo_name
    $found_match = $false
    foreach ($dir in Get-ChildItem -Path $REFERENCE_DIR -Directory) {
        $git_dir = Join-Path $dir.FullName ".git"
        if (Test-Path $git_dir) {
            $remote_url = git -C $dir.FullName config --get remote.origin.url 2>$null
            if ($remote_url -eq $repo) {
                Write-Host "Updating $($dir.Name) in $REFERENCE_DIR..."
                git -C $dir.FullName pull
                $found_match = $true
                break
            }
        }
    }
    if (-not $found_match) {
        Write-Host "Cloning $repo into $target_dir..."
        git clone $repo $target_dir
    }
}

Write-Host "All reference repositories are up to date in $REFERENCE_DIR."
