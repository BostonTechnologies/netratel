.result.structuredContent as $denial
| .result.content as $content
| .jsonrpc == "2.0" and .id == 4
  and (has("error") | not)
  and .result.isError == true
  and ($denial | type == "object")
  and ($denial | keys == ["affectedIds", "confirmation", "correlationId", "data", "error", "failure", "requiresConfirmation", "status", "success", "summary"])
  and $denial.success == false and $denial.status == "failed"
  and $denial.error.code == "local_credential_permission_denied"
  and $denial.error.upstreamStatus == 403 and $denial.error.retryable == false
  and $denial.failure.code == $denial.error.code
  and $denial.failure.layer == "application_permission"
  and $denial.failure.retryable == false
  and $denial.failure.requiredOperation == "netratel_flows/list"
  and $denial.failure.requiredScopes == ["netratel.mcp.observe"]
  and $denial.failure.target == null
  and $denial.data == {requiredPermission: "flow.read", additionalPermissions: [], retryAfterSeconds: null}
  and ($denial.affectedIds == null or $denial.affectedIds == [])
  and $denial.requiresConfirmation == false and $denial.confirmation == null
  and ($denial.correlationId | type == "string" and length > 0)
  and ($content | type == "array" and length == 1)
  and $content[0].type == "text"
  and ((try ($content[0].text | fromjson) catch null) == $denial)
