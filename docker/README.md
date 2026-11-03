# NetRatel container images

Build all images with the repository root (`.`) as the context. The public
images use only public source and generic configuration:

| Image | Dockerfile |
| --- | --- |
| API | `docker/api/Dockerfile` |
| Web | `docker/web/Dockerfile` |
| Database migrations | `docker/migrations/Dockerfile` |
| HTTP MCP | `docker/mcp-http/Dockerfile` |
| Native Client | `docker/client/Dockerfile.public` |

For local evaluation, use the source Compose recipe in `compose.yaml` and copy
`.env.example` to `.env` with synthetic or instance-specific values. Release
deployments consume `release/compose.images.yaml` and immutable, owner-approved
image digests. `release/compose.mcp-http.yaml` is an explicit optional overlay
for the HTTP MCP image and requires separate resource-server configuration.
The public Client image is a standalone agent image; operational tooling and
site-specific overlays remain outside this repository.

The API image selects framework-dependent Linux x64 assets during restore,
publish and runtime-SBOM generation. The optional integration workflow's
`suite=api-rid` compares that payload with the former portable selection on
the same source, pinned SDK/runtime image digests and diagnostic build timestamp.
Both diagnostic arms use identical x64 compiler targets and generated-source
paths, preserving strict assembly and symbol comparisons.
It retains symbols, checks the selected Linux LMDB library with a committed
write/read round trip, and reuses the native/GSSAPI, local-upgrade, identity and
shutdown checks. Retained results distinguish application bytes from standalone
and five-image unique normalized gzip layer exports; these exports exclude
image configs/manifests and do not measure registry transfer. Existing
`suite=api-image` continues the separate Noble/Chiseled base comparison.
