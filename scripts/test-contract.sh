#!/usr/bin/env bash
# Run the contract tests with a native Linux .NET SDK. Under WSL, `dotnet` is often a symlink to the
# Windows dotnet.exe whose test host never starts against \\wsl.localhost paths (hangs indefinitely).
set -euo pipefail
cd "$(dirname "$0")/.."
DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"
if [ ! -x "$DOTNET" ] || ! "$DOTNET" --list-sdks 2>/dev/null | grep -q '^10\.'; then
  curl -sSL https://dot.net/v1/dotnet-install.sh | bash /dev/stdin --channel 10.0 --install-dir "$HOME/.dotnet" --no-path
  DOTNET="$HOME/.dotnet/dotnet"
fi
export DOTNET_ROOT="$(dirname "$DOTNET")" DOTNET_CLI_TELEMETRY_OPTOUT=1
# Minimal WSL images ship without libicu.
ldconfig -p 2>/dev/null | grep -q libicu || export DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1
exec "$DOTNET" test tests/Agent360.API.ContractTests -c Release "$@"
