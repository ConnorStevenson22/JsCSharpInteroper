#!/bin/bash
# Prepares a Claude Code cloud session: installs the .NET SDK pinned in global.json and
# restores NuGet packages so builds and tests work immediately. Idempotent.
set -euo pipefail

if [ "${CLAUDE_CODE_REMOTE:-}" != "true" ]; then
  exit 0
fi

repo="${CLAUDE_PROJECT_DIR:-$(cd "$(dirname "$0")/../.." && pwd)}"
dotnet_root="$HOME/.dotnet"
sdk_version="$(sed -n 's/.*"version": *"\([^"]*\)".*/\1/p' "$repo/global.json" | head -n 1)"

if ! "$dotnet_root/dotnet" --list-sdks 2>/dev/null | grep -q "^${sdk_version} "; then
  installer="$(mktemp -d)/dotnet-install.sh"
  curl -sSL --retry 4 --retry-delay 2 -o "$installer" https://dot.net/v1/dotnet-install.sh
  bash "$installer" --jsonfile "$repo/global.json" --install-dir "$dotnet_root" --no-path
fi

if [ -n "${CLAUDE_ENV_FILE:-}" ]; then
  {
    echo "export DOTNET_ROOT=\"$dotnet_root\""
    echo "export PATH=\"$dotnet_root:\$HOME/.dotnet/tools:\$PATH\""
    echo "export DOTNET_CLI_TELEMETRY_OPTOUT=1"
    echo "export DOTNET_NOLOGO=1"
  } >> "$CLAUDE_ENV_FILE"
fi
export DOTNET_ROOT="$dotnet_root" PATH="$dotnet_root:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

# Restore packages so the first build in the session doesn't wait on NuGet.
# Prefer the solution once it exists; until then, the spike projects.
cd "$repo"
if ls ./*.slnx ./*.sln >/dev/null 2>&1; then
  for solution in ./*.slnx ./*.sln; do
    [ -e "$solution" ] && dotnet restore "$solution" --verbosity quiet
  done
else
  find . -name '*.csproj' -not -path './node_modules/*' -print0 |
    while IFS= read -r -d '' project; do dotnet restore "$project" --verbosity quiet; done
fi
