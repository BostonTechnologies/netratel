# Tenant secret providers and credential profiles

In **Tenants**, open an existing tenant's editor and select **Secrets & credentials**. Local encrypted storage is the initial default. Add a connection, save it, and run **Test connection**. The test attempts a metadata list. Create, update, and delete permissions can be verified with a disposable entry in the provider.

Select **Save default** to change the initial provider shown in new pickers. A saved reference identifies a specific connection, locator, optional field and version policy. **Latest at execution** follows the provider's current version for a new selection. **Pinned version** identifies a specific version. Keep the corresponding provider history for as long as a pin is needed. Changing a connection's display name or rotating its authentication preserves its ID. To change an origin, project, mount or scope, create a new connection and explicitly update the affected references.

## Provider setup

| Provider | Connection settings and rights | Write and delete behavior |
| --- | --- | --- |
| Local | Uses the API database and its shared Data Protection key ring. Tenant browse includes the tenant's existing Local records. | Edits check the current revision. Value replacement creates a version. Deletion removes the entry and its Local version history. |
| Azure Key Vault | Enter the public-cloud vault name or `https://example-app-vault.vault.azure.net/`, the Entra directory tenant ID, application client ID and client secret. Alternatively, select a user-assigned managed identity that the API deployment explicitly allows. Use a data-plane role such as Key Vault Secrets Officer for management. Assign narrower secret get/list rights for reference-only connections. | Value writes create versions. Metadata-only edits update the selected version's properties. Edits check observed metadata before sending the write. Azure handles deletion as a recoverable operation. |
| OpenBao KV v2 | Enter the HTTPS origin, KV v2 mount such as `secret`, allowed base path such as `stage/tools`, and AppRole authentication mount. Use an AppRole limited to metadata `list`/`read`, subkey `read`, data `read`/`create`/`update` and version-soft-delete `update` inside the configured scope. Review the role's Secret ID use limit and issue a new Secret ID for authentication rotation. | Values are named scalar fields. An edit merges changed fields into the selected version and sends a native compare-and-set write. Ordinary deletion soft-deletes the reviewed current version. |
| Infisical | Enter the HTTPS origin, project ID, environment slug, allowed secret path and Universal Auth client ID/secret. Assign the machine identity the project permissions for shared secrets and optional folder listing within the configured path. | Uses the v4 shared-secret API. The list excludes values, reference expansion, imports, personal overrides and recursive lookup. Writes check observed metadata before sending the update. Delete follows the provider's shared-entry semantics. |

Azure and Infisical value writes use best-effort stale metadata checks. Concurrent writes made through other clients can still succeed between the check and the write. Azure search filters the current continuation page. OpenBao and Infisical search the current folder with a bounded result set. Use the browser's folder and continuation controls to navigate the displayed scope.

Azure managed identity selection uses `SecretProviders__AzureManagedIdentityClientIds__0` and additional numbered entries for allowed user-assigned client IDs. Private HTTPS origins require a matching `SecretProviders__PrivateOrigins__0` entry on the API and a CA trusted by its OS or container trust store. Keep certificate validation enabled. For an isolated Development fixture, allow its exact private HTTP origin with `SecretProviders__DevelopmentHttpOrigins__0`.

## Permissions and credential profiles

Grant `secret.browse` for provider and entry metadata, `secret.use` for reference use, `secret.manage` for value replacement and deletion, and `secret.provider.admin` for provider configuration, testing and default selection. A human with `secret.reveal` can deliberately reveal a value. This operation records an audit event with the exact version. Management responses contain metadata. Allow only the provider operations needed by the connection.

Use **Credentials** to save a profile if your role has `credential.profile.manage`. Select a public username or an opaque username reference, and specify the exact client IDs plus the approved account SID or UID. Windows password profiles require a password reference. Unix privileged-local profiles identify a local account by approved UID. The connection test checks only vault metadata availability. For deployment and recovery, see [API key-ring protection](../deployment/CONFIGURATION.md#api-key-ring-protection).

Local provider metadata and the new REST operations live under `/api/v1/tenants/{tenantId}/secret-vault`. In the generated API reference, use the **Secret vault** group. To remove a secret, profile or provider connection, preview the exact current revision and confirm with the returned token. The browser shows provider-specific deletion behavior before confirmation.

## Protected inputs for jobs

Declare a **Secret** input on the job and a matching **Secret** parameter on a Bash or PowerShell library script. In **Run Job**, choose **Secret provider** for that input and select a reference. The direct operator path requires job permission and the tenant's `secret.use` and `secret.reveal` permissions. Agents must advertise `protected-job-parameters-v1`.

To reuse a reference, edit the job parameter and select **Use a provider reference as the default**. The reference retains the selected connection and its scope revision. A supplied manual run value takes precedence over the saved default. A schedule using a provider default uses the configuring operator's saved authority and rechecks that authority at dispatch.

PowerShell receives the value through its named script parameter. Bash receives it in a shell-local `NETRATEL_SECRET_<parameter name>` variable, preserving case. For example, a Bash script with a declared `ApiToken` secret parameter can read `"$NETRATEL_SECRET_ApiToken"`. Use the matching library-script parameter to consume the value. An `@option.ApiToken@` placeholder in command text or a step payload is rejected during admission.

The server saves the connection scope revision and selects exact versions once for an aggregate, as it prepares the first physical dispatch. It shares that selection across the aggregate's nodes and starts a new selection for each new run. Provider values reach a capable agent through the authenticated job stream and private parameter files. The agent registers the values with output redaction before starting the script.

REST callers supply a `providerInputs` array on `POST /api/v1/jobruns/start/{id}`. Each item has a `parameterName` and a `reference` in the same format as the vault picker. The CLI accepts that array with `job-runs start --provider-inputs` or in the job request body supplied with `--body-file`.
