using Bunit;
using Microsoft.JSInterop;
using NetRatel.Web.Components.Shared.Netclaw;
using Xunit;

namespace NetRatel.Web.ComponentTests;

public sealed class ChatMessageContentTests : AsyncBunitContext
{
    public ChatMessageContentTests() => JSInterop.Mode = JSRuntimeMode.Loose;

    [Theory]
    [InlineData(true, "\n")]
    [InlineData(false, "\r\n")]
    public async Task Pipe_tables_preserve_columns_formatting_and_copy_only_the_selected_table(bool borders, string newline)
    {
        var lines = new[] { "Host | Free | Note", ":--- | ---: | :---:", "node-01 | **17.8 GiB** | `a\\|b`", "node-02 | | ok" };
        var table = string.Join(newline, lines.Select(line => borders ? $"| {line} |" : line));
        const string other = "| Value |\n| --- |\n| <img src=x onerror=alert(1)> |";
        var text = $"Disk report\n{table}\n\nAfter the report.\n\n{other}";
        var cut = Render<ChatMessageContent>(parameters => parameters.Add(x => x.Text, text));

        Assert.Equal(2, cut.FindAll("table").Count);
        var first = cut.FindAll(".chat-table-block")[0];
        Assert.Equal(new[] { "Host", "Free", "Note" }, first.QuerySelectorAll("th[scope=col]").Select(cell => cell.TextContent));
        Assert.Equal(new[] { "text-align: left", "text-align: right", "text-align: center" }, first.QuerySelectorAll("th").Select(cell => cell.GetAttribute("style")));
        Assert.Equal("17.8 GiB", first.QuerySelector("td strong")!.TextContent);
        Assert.Equal("a|b", first.QuerySelector("td code")!.TextContent);
        Assert.Equal("", first.QuerySelectorAll("tbody tr")[1].QuerySelectorAll("td")[1].TextContent);
        Assert.Empty(cut.FindAll("img, script"));
        Assert.Equal("<img src=x onerror=alert(1)>", cut.FindAll("table")[1].QuerySelector("td")!.TextContent);

        await first.QuerySelector("button")!.ClickAsync(new());
        Assert.Equal(table.ReplaceLineEndings("\n"), JSInterop.Invocations["navigator.clipboard.writeText"][0].Arguments[0]);
        Assert.Equal("Table copied as Markdown.", cut.Find(".chat-table-toolbar [role=status]").TextContent);

        await cut.FindAll(".chat-table-block button")[1].ClickAsync(new());
        Assert.Equal(other, JSInterop.Invocations["navigator.clipboard.writeText"][1].Arguments[0]);
        await cut.Find(".chat-response-actions button").ClickAsync(new());
        Assert.Equal(text, JSInterop.Invocations["navigator.clipboard.writeText"][2].Arguments[0]);
    }

    [Theory]
    [InlineData("| Host | Free |\n| --- |\n| node-01 | 17 GiB |")]
    [InlineData("| Host | Free |\n| words | --- |\n| node-01 | 17 GiB |")]
    [InlineData("| Host | Free |\n| --- | :")]
    public void Malformed_or_incomplete_table_syntax_stays_readable(string text)
    {
        var cut = Render<ChatMessageContent>(parameters => parameters.Add(x => x.Text, text));
        Assert.Empty(cut.FindAll("table"));
        Assert.Equal(text, cut.Find("p").TextContent);
    }

    [Fact]
    public void Streamed_table_updates_rows_and_leaves_fenced_examples_as_code()
    {
        const string header = "| Host | Free |\n| :--- | ---: |";
        var cut = Render<ChatMessageContent>(parameters => parameters.Add(x => x.Text, "| Host | Free |").Add(x => x.IsStreaming, true));
        Assert.Empty(cut.FindAll("table"));
        cut.Render(parameters => parameters.Add(x => x.Text, header + "\n| node-01 |"));
        Assert.Equal(new[] { "node-01", "" }, cut.FindAll("tbody td").Select(cell => cell.TextContent));
        Assert.Empty(cut.FindAll(".chat-response-actions button"));
        var text = header + "\n| node-01 | **17 GiB** |\n| node-02 | 20 GiB | extra |\n\n```markdown\n" + header + "\n```";
        cut.Render(parameters => parameters.Add(x => x.Text, text));
        Assert.Single(cut.FindAll("table"));
        Assert.Equal(4, cut.FindAll("tbody td").Count);
        Assert.Equal("17 GiB", cut.Find("td strong").TextContent);
        Assert.Equal(header, cut.Find("pre code").TextContent);
        cut.Render(parameters => parameters.Add(x => x.IsStreaming, false));
        Assert.Single(cut.FindAll(".chat-response-actions button"));
    }

    [Fact]
    public async Task Denied_table_clipboard_access_shows_feedback_next_to_the_copy_action()
    {
        JSInterop.SetupVoid("navigator.clipboard.writeText", _ => true).SetException(new JSException("Permission denied"));
        var cut = Render<ChatMessageContent>(parameters => parameters.Add(x => x.Text, "| Host |\n| --- |\n| node-01 |").Add(x => x.IsStreaming, true));
        await cut.Find(".chat-table-block button").ClickAsync(new());
        Assert.Equal("Select the text and copy it manually.", cut.Find(".chat-table-toolbar [role=status]").TextContent);
        Assert.Single(cut.FindAll("table"));
    }
}
