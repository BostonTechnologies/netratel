using System.Collections;
using System.Text.Json;
using System.Text.RegularExpressions;
using NetRatel.Shared.Contracts.Execution;
using NetRatel.Shared.Contracts.Jobs;

namespace NetRatel.API.Services.Jobs;

/// <summary>
/// Native-only node values travel separately from caller parameters. Payloads bind these values
/// after JSON parsing, and shell commands use an explicit shell literal rather than executable text.
/// </summary>
public static class JobNodeTemplateContext
{
    private static readonly Regex Placeholder = new("@node\\.([a-zA-Z]+)@", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    public static IReadOnlyDictionary<string, object?> Bind(IReadOnlyDictionary<string, object?> inputs, JobTargetNode node)
    {
        if (inputs.Keys.Any(key => key.StartsWith("node.", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("The node context is server-owned and cannot be supplied as a job parameter.");
        return new NodeInputs(inputs, node);
    }

    public static string? ResolveLiteral(string? value, IReadOnlyDictionary<string, object?> inputs)
    {
        if (value is null || inputs is not NodeInputs bound) return value;
        return Placeholder.Replace(value, match => Value(bound.Node, match.Groups[1].Value));
    }

    public static string ResolveCommand(string command, ShellExecutor shell, IReadOnlyDictionary<string, object?> inputs)
    {
        if (inputs is not NodeInputs bound || !Placeholder.IsMatch(command)) return command;
        if (shell is not (ShellExecutor.Bash or ShellExecutor.Pwsh or ShellExecutor.WindowsPowerShell))
            throw new ArgumentException("Node command operands require an explicit Bash or PowerShell runner; library parameters accept node literals with every supported script runner.");
        if (command.IndexOfAny(['\r', '\n']) >= 0 || command.Contains("<<", StringComparison.Ordinal) ||
            command.Contains("$(", StringComparison.Ordinal) || command.Contains("${", StringComparison.Ordinal) ||
            shell == ShellExecutor.Bash && command.Contains('`') ||
            shell != ShellExecutor.Bash && (command.Contains("@\"", StringComparison.Ordinal) || command.Contains("@'", StringComparison.Ordinal) ||
                command.Contains("<#", StringComparison.Ordinal)))
            throw new ArgumentException("Direct node command bindings support single-line ordinary arguments. Use typed library-script parameters for here-documents, comments, substitutions or complex scripts.");
        return Placeholder.Replace(command, match =>
        {
            if (match.Index == 0 || !char.IsWhiteSpace(command[match.Index - 1]) ||
                match.Index + match.Length < command.Length && !char.IsWhiteSpace(command[match.Index + match.Length]) ||
                !Unquoted(command, match.Index, shell))
                throw new ArgumentException("Use node command placeholders as unquoted operands; NetRatel supplies shell-literal quoting.");
            var literal = Value(bound.Node, match.Groups[1].Value);
            return shell == ShellExecutor.Bash
                ? "'" + literal.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'"
                : "'" + literal.Replace("'", "''", StringComparison.Ordinal) + "'";
        });
    }

    private static string Value(JobTargetNode node, string key) => key.ToLowerInvariant() switch
    {
        "id" => node.AgentId.ToString("D"),
        "hostname" => node.Hostname,
        "displayname" => node.DisplayName ?? node.Hostname,
        "tags" => JsonSerializer.Serialize(node.Tags),
        _ => throw new ArgumentException("The node template field is unsupported.")
    };

    private static bool Unquoted(string text, int end, ShellExecutor shell)
    {
        var quote = '\0';
        var comment = false;
        for (var index = 0; index < end; index++)
        {
            var character = text[index];
            if (comment) { if (character is '\r' or '\n') comment = false; else continue; }
            if (quote == '\0' && character == '#') { comment = true; continue; }
            if (quote != '\'' && character == (shell == ShellExecutor.Bash ? '\\' : '`'))
            {
                // An escape immediately before the placeholder would consume the opening
                // literal quote supplied by the binder and expose metadata as shell syntax.
                if (index + 1 == end) return false;
                index++; continue;
            }
            if (character is not ('\'' or '"')) continue;
            if (quote == '\0') quote = character;
            else if (quote == character)
            {
                if (shell != ShellExecutor.Bash && character == '\'' && index + 1 < end && text[index + 1] == '\'') index++;
                else quote = '\0';
            }
        }
        return quote == '\0' && !comment;
    }

    private sealed class NodeInputs(IReadOnlyDictionary<string, object?> inputs, JobTargetNode node) : IReadOnlyDictionary<string, object?>
    {
        public JobTargetNode Node { get; } = node;
        public object? this[string key] => inputs[key];
        public IEnumerable<string> Keys => inputs.Keys;
        public IEnumerable<object?> Values => inputs.Values;
        public int Count => inputs.Count;
        public bool ContainsKey(string key) => inputs.ContainsKey(key);
        public bool TryGetValue(string key, out object? value) => inputs.TryGetValue(key, out value);
        public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() => inputs.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
