#!/usr/bin/env sh
set -eu

NetRatel_HTTP_PORT="${NetRatel_HTTP_PORT:-9111}"
export ASPNETCORE_HTTP_PORTS="$NetRatel_HTTP_PORT"
export ASPNETCORE_URLS="http://+:$NetRatel_HTTP_PORT"

exec "$@"
