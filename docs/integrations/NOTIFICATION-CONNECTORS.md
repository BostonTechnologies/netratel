# Discord and Slack notifications

## Configure a destination

Open `/account/integration-credentials` and select **Notifications**, or use `/account/integration-credentials?tab=notifications`. Choose an authorized tenant, then **Add Discord** or **Add Slack**. Each tenant can have up to 64 named destinations.

| Option | Discord | Slack |
| --- | --- | --- |
| Name | Required display name for flow selection | Required display name for flow selection |
| Webhook URL | Incoming webhook for the destination channel | Incoming webhook created for the destination channel |
| Enabled | Controls test sends, flow selection and dispatch | Controls test sends, flow selection and dispatch |
| Sender name | Optional webhook username override | Uses the identity configured by Slack |
| Thread ID | Optional existing Discord thread ID | Uses the channel configured by the webhook |

Save the destination, then use **Send test notification** to verify delivery when ready. Saving, listing, editing, validating and previewing do not send messages. Test delivery requires an enabled target and current permission to use its credential.

Webhook URLs are credentials. They are encrypted with the API's persisted Data Protection key ring and bound to the tenant and connector ID. Reads return only whether a webhook is configured; the URL does not appear in the browser response, flow graph, run history or diagnostic text. Leave the password field blank when editing to preserve the credential. A provider change requires a replacement URL. Deleting a destination disables it and clears its stored credential while retaining its identity to prevent old versions from silently reusing it.

The connector uses the existing tenant integration-management and credential-use permissions. A target's owner and the flow's current execution principal must still be authorized at dispatch. Configuration updates increment the target revision. Choose **Use updated target** in each affected flow's action settings and publish a new version before expecting it to run with the change.

## Add actions to a flow

Open `/flows`, choose the same tenant, and create or edit a flow. Add an **Action group**, or convert an existing RatelDesk incident action. Select Discord, Slack or RatelDesk for each child and choose its enabled target. A group supports one to eight independently tracked actions, such as:

- Discord notification to the site operations channel.
- Slack notification to the service team channel.
- RatelDesk create incident using the existing incident connector.

The mapping node supplies the title and description to every child. Each action has its own delivery outcome and receipt, so a failed channel does not hide a successful incident. Validate, publish, and select that immutable published version in the monitoring rule. See [flows](../operations/FLOWS.md) for authority, retries and run history, and [monitoring](../operations/MONITORING.md#alert-if-offline) for grouped outages.

## Delivery behavior

Only supported provider HTTPS webhook addresses are accepted. Requests do not follow redirects, use ambient proxies or include application trace propagation. Messages are bounded to 1,800 UTF-16 code units. Discord disables automatic mentions; Slack sends escaped plain text without unfurls. Offline alerts include a short client list or an exact count instead of an unbounded ticket/message body.

Discord uses `wait=true` and records the returned message ID. Slack accepts only its documented successful `ok` response. Rate-limit rejections retain a safe retry delay. A timeout, interrupted send or ambiguous response records **DeliveryUnknown**, because incoming webhooks do not offer the verified replay contract used by the RatelDesk receiver. Successful children are never resent just because another child needs a retry.

The `AddNotificationConnectors` database migration creates a new tenant-scoped table; it does not rewrite existing integration credentials, flow graphs or monitoring rules. Normal API migration and persisted Data Protection key-ring requirements apply during deployment.

Provider references: [Discord incoming webhooks](https://docs.discord.com/developers/resources/webhook), [Discord rate limits](https://docs.discord.com/developers/topics/rate-limits), [Slack incoming webhooks](https://docs.slack.dev/messaging/sending-messages-using-incoming-webhooks/) and [Slack rate limits](https://docs.slack.dev/apis/web-api/rate-limits/).
