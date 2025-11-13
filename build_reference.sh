#!/bin/zsh

REFERENCE_DIR="Reference"

# List your reference repositories here
REPOS=(
    "https://github.com/dotnet/extensions.git"
    "https://github.com/microsoft/semantic-kernel.git"
    "https://github.com/microsoft/agent-framework.git"
    "https://github.com/google-gemini/gemini-cli.git"
    "https://github.com/langchain-ai/langgraph.git"
    "https://github.com/pydantic/pydantic-ai.git"
    "https://github.com/strands-agents/sdk-python.git"
    "https://github.com/ag-ui-protocol/ag-ui.git"
    "https://github.com/openai/codex.git"
    "https://github.com/langchain-ai/langchain.git"
    "https://github.com/microsoft/vscode-copilot-chat.git"
    "https://github.com/Aider-AI/aider.git"
    "https://github.com/microsoft/kernel-memory.git"
    "https://github.com/openai/openai-agents-python.git"
    "https://github.com/lsiddiquee/SemanticPluginForge.git"
    "https://github.com/anthropic-experimental/sandbox-runtime.git"
    "https://github.com/praeclarum/CrossIntelligence.git"
    "https://github.com/anthropics/skills.git"
    

    # Add more repo URLs as needed
)

mkdir -p "$REFERENCE_DIR"

for repo in "${REPOS[@]}"; do
    repo_name=$(basename "$repo" .git)
    target_dir="$REFERENCE_DIR/$repo_name"
    # Check if any folder in REFERENCE_DIR is a git repo with the same remote URL
    found_match=false
    for dir in "$REFERENCE_DIR"/*; do
        if [ -d "$dir/.git" ]; then
            remote_url=$(git -C "$dir" config --get remote.origin.url)
            if [ "$remote_url" = "$repo" ]; then
                echo "Updating $(basename "$dir") in $REFERENCE_DIR..."
                git -C "$dir" pull
                found_match=true
                break
            fi
        fi
    done
    if [ "$found_match" = false ]; then
        echo "Cloning $repo into $target_dir..."
        git clone "$repo" "$target_dir"
    fi
done

echo "All reference repositories are up to date in $REFERENCE_DIR."
