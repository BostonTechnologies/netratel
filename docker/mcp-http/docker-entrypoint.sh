#!/bin/sh
set -eu

# Tooling is included at image build time. Stdio writes only protocol frames to
# stdout; this dispatcher emits nothing before exec.
case "${1:-http}" in
  http)
    if [ "$#" -gt 0 ]; then shift; fi
    exec dotnet /app/NetRatel.Mcp.Http.dll "$@"
    ;;
  cli)
    shift
    exec dotnet /app/tooling/cli/netratel.dll "$@"
    ;;
  stdio)
    shift
    exec dotnet /app/tooling/stdio/NetRatel.Mcp.dll "$@"
    ;;
  setup)
    # The supplied setup Compose service mounts a seeded durable volume here.
    # Do not invent a writable ephemeral default for arbitrary legacy containers.
    if [ -z "${NETRATEL_SETUP_DIRECTORY:-}" ] && [ -n "${NETRATEL_SETUP_VOLUME:-}" ]; then
      export NETRATEL_SETUP_DIRECTORY=/setup-output
    fi
    # The shared setup boundary infers this distribution's host default and
    # accepts explicit mode/target/transport options in any argument position.
    exec dotnet /app/NetRatel.Mcp.Http.dll "$@"
    ;;
  *)
    # Retain historical HTTP host arguments, including --urls overrides.
    exec dotnet /app/NetRatel.Mcp.Http.dll "$@"
    ;;
esac
