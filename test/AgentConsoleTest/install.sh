#!/usr/bin/env bash
set -euo pipefail
APP=hpd-agent

MUTED='\033[0;2m'
RED='\033[0;31m'
ORANGE='\033[38;5;214m'
CYAN='\033[0;36m'
NC='\033[0m' # No Color

usage() {
    cat <<EOF
HPD-Agent Installer

Usage: install.sh [options]

Options:
    -h, --help              Display this help message
    -v, --version <version> Install a specific version (e.g., 1.0.0)
    -b, --binary <path>     Install from a local binary instead of downloading
        --no-modify-path    Don't modify shell config files (.zshrc, .bashrc, etc.)

Examples:
    curl -fsSL https://hpd-agent.dev/install | bash
    curl -fsSL https://hpd-agent.dev/install | bash -s -- --version 1.0.0
    ./install.sh --binary /path/to/hpd-agent
EOF
}

requested_version=${VERSION:-}
no_modify_path=false
binary_path=""

while [[ $# -gt 0 ]]; do
    case "$1" in
        -h|--help)
            usage
            exit 0
            ;;
        -v|--version)
            if [[ -n "${2:-}" ]]; then
                requested_version="$2"
                shift 2
            else
                echo -e "${RED}Error: --version requires a version argument${NC}"
                exit 1
            fi
            ;;
        -b|--binary)
            if [[ -n "${2:-}" ]]; then
                binary_path="$2"
                shift 2
            else
                echo -e "${RED}Error: --binary requires a path argument${NC}"
                exit 1
            fi
            ;;
        --no-modify-path)
            no_modify_path=true
            shift
            ;;
        *)
            echo -e "${ORANGE}Warning: Unknown option '$1'${NC}" >&2
            shift
            ;;
    esac
done

INSTALL_DIR=$HOME/.hpd-agent/bin
mkdir -p "$INSTALL_DIR"

# Detect OS and architecture
raw_os=$(uname -s)
os=$(echo "$raw_os" | tr '[:upper:]' '[:lower:]')
case "$raw_os" in
  Darwin*) os="osx" ;;
  Linux*) os="linux" ;;
  MINGW*|MSYS*|CYGWIN*) os="win" ;;
esac

arch=$(uname -m)
if [[ "$arch" == "aarch64" ]]; then
  arch="arm64"
fi
if [[ "$arch" == "x86_64" ]]; then
  arch="x64"
fi

# Handle Rosetta on M1/M2 Macs
if [ "$os" = "osx" ] && [ "$arch" = "x64" ]; then
  rosetta_flag=$(sysctl -n sysctl.proc_translated 2>/dev/null || echo 0)
  if [ "$rosetta_flag" = "1" ]; then
    arch="arm64"
  fi
fi

combo="$os-$arch"
case "$combo" in
  linux-x64|linux-arm64|osx-x64|osx-arm64|win-x64)
    ;;
  *)
    echo -e "${RED}Unsupported OS/Arch: $os/$arch${NC}"
    exit 1
    ;;
esac

# If --binary is provided, skip download
if [ -n "$binary_path" ]; then
    if [ ! -f "$binary_path" ]; then
        echo -e "${RED}Error: Binary not found at ${binary_path}${NC}"
        exit 1
    fi
    echo -e "${CYAN}Installing from local binary: ${binary_path}${NC}"
    cp "$binary_path" "$INSTALL_DIR/hpd-agent"
    chmod +x "$INSTALL_DIR/hpd-agent"
    specific_version="local"
else
    # TODO: Replace with actual GitHub releases URL
    # For now, show what would be downloaded
    DOWNLOAD_URL="https://github.com/yourusername/hpd-agent/releases/download/v${requested_version:-latest}/hpd-agent-${combo}"

    echo -e "${CYAN}Downloading HPD-Agent for ${combo}...${NC}"
    echo -e "${MUTED}URL: ${DOWNLOAD_URL}${NC}"
    echo -e "${ORANGE}Note: Download not implemented yet - use --binary flag${NC}"
    exit 1
fi

# Add to PATH if not already present
add_to_path() {
    local config_file=$1
    local command=$2

    if ! grep -qF "$command" "$config_file" 2>/dev/null; then
        echo "" >> "$config_file"
        echo "# HPD-Agent" >> "$config_file"
        echo "$command" >> "$config_file"
        echo -e "${CYAN}Added to PATH in ${config_file}${NC}"
        echo -e "${MUTED}  $command${NC}"
    fi
}

XDG_CONFIG_HOME=${XDG_CONFIG_HOME:-$HOME/.config}

current_shell=$(basename "$SHELL")
case $current_shell in
    fish)
        config_files="$HOME/.config/fish/config.fish"
    ;;
    zsh)
        config_files="${ZDOTDIR:-$HOME}/.zshrc ${ZDOTDIR:-$HOME}/.zshenv $XDG_CONFIG_HOME/zsh/.zshrc $XDG_CONFIG_HOME/zsh/.zshenv"
    ;;
    bash)
        config_files="$HOME/.bashrc $HOME/.bash_profile $HOME/.profile $XDG_CONFIG_HOME/bash/.bashrc $XDG_CONFIG_HOME/bash/.bash_profile"
    ;;
    *)
        config_files="$HOME/.bashrc $HOME/.bash_profile $XDG_CONFIG_HOME/bash/.bashrc $XDG_CONFIG_HOME/bash/.bash_profile"
    ;;
esac

if [[ "$no_modify_path" != "true" ]]; then
    config_file=""
    for file in $config_files; do
        if [[ -f $file ]]; then
            config_file=$file
            break
        fi
    done

    if [[ -z $config_file ]]; then
        echo -e "${ORANGE}No config file found for $current_shell. You may need to manually add to PATH:${NC}"
        echo -e "${MUTED}  export PATH=$INSTALL_DIR:\$PATH${NC}"
    elif [[ ":$PATH:" != *":$INSTALL_DIR:"* ]]; then
        case $current_shell in
            fish)
                add_to_path "$config_file" "fish_add_path $INSTALL_DIR"
            ;;
            *)
                add_to_path "$config_file" "export PATH=$INSTALL_DIR:\$PATH"
            ;;
        esac
    fi
fi

# Create data directory
DATA_DIR="$HOME/Library/Application Support/HPD-Agent"
if [ "$os" = "linux" ]; then
    DATA_DIR="${XDG_DATA_HOME:-$HOME/.local/share}/HPD-Agent"
fi
mkdir -p "$DATA_DIR/sessions"

echo -e ""
echo -e "${CYAN}██╗  ██╗██████╗ ██████╗       █████╗  ██████╗ ███████╗███╗   ██╗████████╗${NC}"
echo -e "${CYAN}██║  ██║██╔══██╗██╔══██╗     ██╔══██╗██╔════╝ ██╔════╝████╗  ██║╚══██╔══╝${NC}"
echo -e "${CYAN}███████║██████╔╝██║  ██║█████╗███████║██║  ███╗█████╗  ██╔██╗ ██║   ██║${NC}"
echo -e "${CYAN}██╔══██║██╔═══╝ ██║  ██║╚════╝██╔══██║██║   ██║██╔══╝  ██║╚██╗██║   ██║${NC}"
echo -e "${CYAN}██║  ██║██║     ██████╔╝      ██║  ██║╚██████╔╝███████╗██║ ╚████║   ██║${NC}"
echo -e "${CYAN}╚═╝  ╚═╝╚═╝     ╚═════╝       ╚═╝  ╚═╝ ╚═════╝ ╚══════╝╚═╝  ╚═══╝   ╚═╝${NC}"
echo -e ""
echo -e "${MUTED}Powered by HPD Agent Framework${NC}"
echo -e ""
echo -e "${CYAN}✓ HPD-Agent installed successfully!${NC}"
echo -e ""
echo -e "${MUTED}Installation:${NC}"
echo -e "  Binary: ${INSTALL_DIR}/hpd-agent"
echo -e "  Data:   ${DATA_DIR}"
echo -e ""
echo -e "${MUTED}To start using HPD-Agent:${NC}"
echo -e "  ${CYAN}hpd-agent${NC}     # Start interactive mode"
echo -e ""
echo -e "${MUTED}Restart your shell or run:${NC}"
echo -e "  ${CYAN}source ${config_file}${NC}"
echo -e ""
