# Set up HTTP MCP

**Host approval connects the persistent HTTP MCP service to NetRatel. Client approval grants one CLI/MCP client access.** Complete host setup once per deployment, then connect individual clients with their own permissions.

## Connect an existing deployment

Use API, Web, HTTP MCP and setup tools from the same verified release. You need Docker access on the deployment host, an instance-administrator account, the public HTTPS Web URL and permission to apply through your existing deployment manager. Keep the database, ingress, volumes and protected configuration.

1. Extract the verified release Compose bundle into a separate tools directory. Open a terminal **on the deployment host**, enter that directory and run:

   ```sh
   ./setup-http-mcp.sh
   ```

   The launcher discovers the running MCP container, ordered saved Compose sources, image and persistent storage. Select a container only if prompted. If discovery stops, correct the specific missing prerequisite before continuing; no approval has been requested.

2. Enter the **NetRatel Web URL**, using its root or `/mcp` address. Check the normalized Web and MCP addresses. Existing API/authentication/instance settings are retained. Use **Advanced** only for a deliberate separate API destination or another supported configuration. A hostname never selects development or production for you.

3. Review the effective deployment proposal and redacted changes. Open the approval link, sign in as the instance administrator and choose **Review host**. Confirm the persistent host deployment proposal. This approval does not grant personal or tenant client access. Configuration problems must be corrected and reviewed before approval.

4. Choose **Save**. **Saved — apply pending** means configuration is prepared. For externally managed Compose, choose **Export and exit**, review the complete private document at the printed path, then save/deploy through the same manager. For release-managed Compose, run the reviewed apply command. Preserve the printed checkpoint and persistent storage.

5. Resume verification after deployment. **Deployment applied — verify pending** still needs a live check. Separately review the **Temporary verification client** and explicitly consent to its short-lived `mcp.discovery.read` access with no tenant grants. It is used only to check this connection and is cleaned up afterward; a cleanup-pending result needs recovery.

6. Finish only at **End-to-end verified**: configuration, authentication, MCP initialization/catalog and the permitted API-backed status operation passed. Keep the sanitized result. A token, HTTP 200 or an exported file alone is insufficient.

For interrupted setup or verification-only resume, rerun from the same tools directory:

```sh
./setup-http-mcp.sh --verify
```

Resume reuses the approved generation; changed sources require renewed review. See [recovery and alternate contexts](hosting-reference.md), [configuration](configuration.md) and [troubleshooting](troubleshooting.md).

## Connect another CLI/MCP client

After host verification, open **Integrations → API & MCP credentials → Create credential**, or enter the client's code under **Connect CLI / MCP client → Review client**. Select only the explicit tenant/server permissions it needs. Store its one-time secret securely. Each consumer uses its own credential; the setup verifier is temporary.

For Netclaw, follow [customer connection setup](../integrations/netclaw-http-mcp.md). For native CLI/stdio and container tooling, use [CLI and MCP](../integrations/CLI_AND_MCP.md) and the [host reference](hosting-reference.md#client-tooling-in-the-http-image).
