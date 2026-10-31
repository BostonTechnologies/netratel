# Operator guides

These guides describe application permissions, monitoring, services and operational workflows. Use [troubleshooting](TROUBLESHOOTING.md) for common failures and [HTTP MCP troubleshooting](../mcp-http/troubleshooting.md) for that optional interface.

- [Monitoring flows](FLOWS.md)
- [Monitoring](MONITORING.md)
- [Client Services](SERVICES.md)
- [Troubleshooting](TROUBLESHOOTING.md)
- [Scoped RBAC](rbac.md)

## Task history

Open **Tasks → History** and use the row or its chevron to expand a run summary.
The panel shows recorded timing, target, status, exit code and correlation IDs,
with a compact preview of the final console output when available. Only the
expanded run loads its details. **Refresh task preview** reloads that snapshot;
the far-right navigation icon and **Full output** open the complete task view,
including live logs for a running task.

The preview displays at most 60 lines and 12,288 characters in a scrolling panel.
Results larger than 65,536 characters stay in the full task view. Filtering,
paging or refreshing history closes the current expansion.

## Script Library

The folder tree uses compact single-line names, nested indentation and a count
of matching scripts in each folder, including its subfolders. Search and Type
filter both the tree and its counts. Hover over a shortened name to read it in
full.

Select a script, click its name above Monaco and type to rename it in place.
Changing the name or code enables **Save**; both changes are saved together.
**Delete selected script** sits beside Save and asks for confirmation.

Use **Edit script full screen** to open the full-screen editor. It has the same
name, Save and Delete controls. **Exit full screen** returns to the library with
the current draft intact, including unsaved edits. Switching to a different
script asks before discarding a draft. Failed saves retain the name and code
for correction or retry. Existing library permissions and source-revision
checks also apply in full screen.
