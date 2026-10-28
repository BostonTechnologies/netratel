using System.Text.Json.Nodes;
using NetRatel.Shared.Contracts.ComputerUse;

namespace NetRatel.Mcp.Core;

public static partial class NetRatelMcpToolDefinitions
{
    private static RequestDefinition ComputerUseRequest(string operation)
    {
        var fields = new List<(string Name, JsonObject Schema, bool Required)>
        {
            PositiveIntIdentifier("tenantId", "Exact authorized tenant identifier."),
            Uuid("agentId", "Exact enrolled V2 agent identifier.", true)
        };
        if (operation is "capabilities" or "list_windows" or "preview_open" or "open" or "open_view")
        {
            fields.Add(("target", ComputerUseTargetSchema(operation is "preview_open" or "open" or "open_view"), true));
            fields.Add(("provider", Enum("provider", "Explicit provider; unsupported capabilities return a refusal and never trigger an implicit fallback.",
                ComputerUseProviders.CuaDriver, ComputerUseProviders.NativeWindows).Schema, true));
            if (operation is "preview_open" or "open" or "open_view")
            {
                fields.Add(Integer("lifetimeSeconds", "Lease duration, default 300 seconds.", 30, 300));
                fields.Add(Integer("idleTimeoutSeconds", "Idle timeout, default 60 seconds; must not exceed lifetimeSeconds.", 15, 120));
            }
        }
        else
            fields.Add(Uuid("sessionId", "Caller-owned computer-use lease returned by open or open_view; the API resolves its target.", true));

        if (operation == "observe")
            fields.Add(Boolean("includeScreenshot", "Return one bounded screenshot as MCP image content when the selected provider supports it."));
        if (operation is "preview_act" or "act")
        {
            fields.Add(RequiredPatternString("observationId", "Exact fresh observation identifier returned for this lease/window; never reuse it after an action.", "^[A-Za-z0-9_-]{1,128}$", 128));
            fields.Add(("action", ComputerUseActionSchema(), true));
        }
        if (operation is "open" or "act")
            fields.Add(("planToken", OpaquePlanToken("planToken", "Opaque credential from the matching preview with unchanged target and action.").Schema, true));
        if (operation is "open" or "open_view" or "act" or "pause" or "resume" or "close" or "close_view")
            fields.Add(("idempotencyKey", OpaquePlanToken("idempotencyKey", "Opaque key from the preview, or a new caller-generated 32–128 character URL-safe key for lease lifecycle operations; reuse only for an unchanged retry.").Schema, true));
        return RequiredRequest(fields);
    }

    private static JsonObject ComputerUseTargetSchema(bool requireWindow)
    {
        var fields = new List<(string Name, JsonObject Schema, bool Required)>
        {
            ("windowsSessionId", Integer("windowsSessionId", "Exact positive interactive Windows session ID from current Remote Support inventory.", 1, int.MaxValue).Schema, true),
            RequiredPatternString("userSidHash", "Exact user-session identity hash from the same current inventory.", "^[^\\u0000-\\u0020\\u007f]{1,128}$", 128)
        };
        if (requireWindow)
        {
            fields.Add(("processId", Integer("processId", "Exact process ID returned by list_windows for this provider/session.", 1, int.MaxValue).Schema, true));
            fields.Add(RequiredPatternString("windowId", "Exact opaque provider window identifier returned by list_windows.", "^[^\\u0000-\\u001f\\u007f]{1,256}$", 256));
        }
        return RequiredRequest(fields).Schema;
    }

    private static JsonObject ComputerUseActionSchema()
    {
        JsonObject Action(string kind, params (string Name, JsonObject Schema, bool Required)[] fields) => RequiredRequest([
            ("kind", new JsonObject { ["const"] = kind }, true), .. fields
        ]).Schema;
        var element = RequiredPatternString("elementId", "Semantic element identifier from the named observation.", "^[^\\u0000-\\u001f\\u007f]{1,256}$", 256);
        var x = ("x", Integer("x", "Window-relative x coordinate in the named screenshot.", 0, 32767).Schema, true);
        var y = ("y", Integer("y", "Window-relative y coordinate in the named screenshot.", 0, 32767).Schema, true);
        return new JsonObject
        {
            ["oneOf"] = new JsonArray(
                Action(ComputerUseActionKinds.Invoke, element),
                Action(ComputerUseActionKinds.Click, x, y,
                    Enum("button", "Mouse button, default left.", "left", "right", "middle")),
                Action(ComputerUseActionKinds.Type,
                    ("text", new JsonObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = ComputerUseContract.MaximumTextLength, ["pattern"] = "^[^\\u0000]*$",
                        ["description"] = "Text to enter; whitespace and line breaks are preserved. Never supply credentials." }, true),
                    (element.Name, element.Schema.DeepClone().AsObject(), false)),
                Action(ComputerUseActionKinds.Key,
                    ("key", Enum("key", "One bounded navigation key; chords and arbitrary shell keys are excluded.",
                        "Enter", "Tab", "Escape", "Backspace", "Delete", "Space", "ArrowUp", "ArrowDown", "ArrowLeft", "ArrowRight", "Home", "End", "PageUp", "PageDown").Schema, true)),
                Action(ComputerUseActionKinds.Scroll,
                    ("x", x.Item2.DeepClone().AsObject(), true), ("y", y.Item2.DeepClone().AsObject(), true),
                    ("scrollX", Integer("scrollX", "Horizontal scroll steps; exactly one scroll axis must be nonzero.", -50, 50).Schema, true),
                    ("scrollY", Integer("scrollY", "Vertical scroll steps; exactly one scroll axis must be nonzero.", -50, 50).Schema, true)))
        };
    }
}
