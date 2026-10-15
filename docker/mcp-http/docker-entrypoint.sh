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
    shift
    setup_mode=host
    setup_transport=
    if [ "${1:-}" = "--target" ]; then
      case "${2:-}" in
        http-host) setup_mode=host ;;
        cli) setup_mode=client; setup_transport=cli ;;
        stdio) setup_mode=client; setup_transport=stdio ;;
        http-client) setup_mode=client; setup_transport=http ;;
        *) echo "setup --target must be http-host, cli, stdio or http-client" >&2; exit 2 ;;
      esac
      shift 2
    fi
    if [ -n "$setup_transport" ]; then
      exec dotnet /app/tooling/cli/netratel.dll setup --mode "$setup_mode" --transport "$setup_transport" "$@"
    fi
    exec dotnet /app/tooling/cli/netratel.dll setup --mode "$setup_mode" "$@"
    ;;
  *)
    # Retain historical HTTP host arguments, including --urls overrides.
    exec dotnet /app/NetRatel.Mcp.Http.dll "$@"
    ;;
esac
