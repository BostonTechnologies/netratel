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
