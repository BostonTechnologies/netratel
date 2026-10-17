# Set up the HTTP MCP server, then connect clients

There are two separate connections:

```text
Netclaw / other HTTP MCP client → HTTP MCP server → NetRatel API
          client credential         host service identity
```

**Host setup connects the MCP server to NetRatel once per deployment. Client setup gives each consumer its own access.** Host/client does not mean home/company use or single/multiple tenants. Ordinary Web/API and the native NetRatel Client agent do not require HTTP MCP.

## Which setup command do I run?

These are commands for an already-open terminal, with the matching tools installed:

| What you are setting up | Command | Terminal |
| --- | --- | --- |
| **MCP server → API** | `dotnet /app/NetRatel.Mcp.Http.dll setup --mode host` | Inside a **prepared HTTP MCP container**; see the storage/deployment prerequisite below. |
| **Personal HTTP MCP client → server** | `dotnet /app/NetRatel.Mcp.Http.dll setup` | Inside the HTTP MCP container, with persistent client storage. |
| **CLI → API** | `netratel setup` | The client machine where the CLI is installed. |
| **Stdio MCP → API** | `dotnet NetRatel.Mcp.dll setup` | The extracted stdio tooling directory on the client machine. |

The direct **host** command needs persistent host-setup storage and prepared deployment information. It cannot discover Docker-host files or recreate sibling services from a container terminal. **For an existing deployment, use the complete host procedure below:** it obtains those details, starts the same host wizard and checks the applied containers afterward. Client setup alone cannot supply the server's missing prerequisites.

When starting a new tooling container through the image dispatcher, `setup --target http-host` selects host setup and `setup --target http-client` selects client setup. Those are image arguments, not standalone shell commands.

## Connect the server once

Use matching verified API, Web and HTTP MCP images. Have your public HTTPS Web URL, an instance-administrator login, and access to the saved deployment through its existing manager. The **Linux Docker host** needs Docker Compose, Bash, Python 3 and GNU coreutils. Keep the existing database, ingress, volumes and protected settings.

1. **Open a terminal on the Docker host**, not inside the MCP container. List the HTTP MCP containers:

   ```sh
   docker ps --filter label=com.docker.compose.service=mcp-http --format 'table {{.Names}}\t{{.Image}}'
   ```

   In a writable directory, copy the setup tool from the installed image. Replace `YOUR_MCP_CONTAINER` with its **name** from that list:

   ```sh
   mkdir -p netratel-mcp-tools
   cd netratel-mcp-tools
   mcp_container=YOUR_MCP_CONTAINER
   docker cp "$mcp_container:/app/setup-http-mcp.sh" ./setup-http-mcp.sh
   bash ./setup-http-mcp.sh --container "$mcp_container"
   ```

   You do not need another extracted Compose bundle. The tool reads the selected container's actual saved deployment files. If ownership is unknown, select the existing deployment manager or explicitly release-owned Compose when asked. A missing source must be restored before approval; see [source recovery](hosting-reference.md#missing-saved-source-or-first-time-mcp-enablement).

2. **Enter the NetRatel Web URL**, such as `https://netratel.example.invalid` or its `/mcp` URL. Check the displayed Web/MCP/API addresses. Existing API and authentication settings are reused. For a deliberately separate API address, choose **Advanced → Separate API HTTPS origin**.

3. **Approve the server.** Review the effective configuration and redacted changes, open the displayed approval link and sign in as the instance administrator. Check the code and choose **Review host**. This approves the persistent server connection; it grants no personal or tenant client access.

4. **Save, then deploy.** **Saved — apply pending** means deployment is still required. For an existing manager, choose **Export and exit**, privately open the complete Compose document at the printed host path, replace the **entire saved document in that same manager**, retain its interpolation/secret values, then **Save and Deploy**. For release-owned Compose, run the complete reviewed apply command from the deployment directory.

5. **Verify after deployment.** On the Docker host, in the same tools directory, run:

   ```sh
   bash ./setup-http-mcp.sh --container "$mcp_container" --verify
   ```

   In a new terminal, set `mcp_container` to its current name again first. In local credential mode, separately approve the **Temporary verification client**: only `mcp.discovery.read`, no tenant grants; it is short-lived and revoked afterward. For OIDC, follow the existing provider/account prompts. If interrupted before Save/apply, rerun the ordinary command from step 1 first.

6. **Finish at End-to-end verified.** Configuration, authentication, MCP initialization/catalog and the permitted API-backed status read must pass. **Deployment applied — verify pending**, a saved file, a token or HTTP 200 is not completion. Keep the checkpoint and persistent storage. Follow [recovery](hosting-reference.md) if cleanup remains pending or changed sources require renewed review.

If `docker cp` reports that the tool is absent, the running image predates this bootstrap method. Upgrade to the matching verified release first, or use the [existing bundle acquisition commands](hosting-reference.md#use-a-verified-compose-download). If there is no MCP container yet, use [first-time enablement](hosting-reference.md#missing-saved-source-or-first-time-mcp-enablement).

## Connect each HTTP MCP client afterward

**MCP container terminal:** start the client wizard with this command, without `--mode host`:

```sh
dotnet /app/NetRatel.Mcp.Http.dll setup
```

From a **Docker-host terminal**, the equivalent is:

```sh
docker exec -it "$mcp_container" dotnet /app/NetRatel.Mcp.Http.dll setup
```

Enter the Web URL. In local credential mode, open the browser approval link, choose **Review client**, and select only the required tenant/server permissions. For OIDC, follow the configured provider/account prompts. Return to the terminal, review, Save and complete its read-only verification. Keep the client-data volume: this profile belongs to that container, not another computer.

If that container lacks persistent client storage, use the copied host tool instead:

```sh
bash ./setup-http-mcp.sh --container "$mcp_container" --target http-client
```

For **Netclaw or another third-party client**, create a separate HTTP MCP credential under **Integrations → API & MCP credentials → Create credential** or approve that client's code under **Connect CLI / MCP client → Review client**. Install the one-time secret in that client's own protected storage and use the canonical `/mcp` URL. A container profile is not automatically installed into Netclaw. Follow [Netclaw customer connections](../integrations/netclaw-http-mcp.md) for its transport, policy and per-customer checks.

See [CLI/stdio commands](../integrations/CLI_AND_MCP.md), [host/storage recovery](hosting-reference.md), [configuration](configuration.md) and [troubleshooting](troubleshooting.md) for other contexts.
