using System.Text;
using System.Text.RegularExpressions;
using NetRatel.Shared.Contracts.Jobs;

namespace NetRatel.Application.Jobs;

public sealed class JobTargetValidationException(string code, string message, int statusCode = 400) : InvalidOperationException(message)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
}

/// <summary>The one bounded grammar for saved selectors, builders, previews and execution.</summary>
public static class JobNodeFilterParser
{
    public const int MaximumQueryLength = 4096;
    public const int MaximumClauses = 32;
    public const int MaximumOperands = 128;
    public const int MaximumRegexLength = 256;
    public const int MaximumLiteralLength = 512;
    public static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(50);
    public static IReadOnlyList<JobNodeFilterPropertyDto> Properties { get; } =
    [
        new("id", "Stable AgentId; exact canonical UUID.", false),
        new("hostname", "Exact last-known hostname.", true),
        new("name", "Exact registered directory name.", true),
        new("displayname", "Exact friendly/display name.", true),
        new("tags", "Case-insensitive canonical tags; comma is OR and + is AND.", true, true),
        new("os", "Last-known operating system.", true),
        new("architecture", "Last-known processor architecture.", true),
        new("all", "Explicit all: true within the authorized tenant inventory.", false)
    ];

    public static JobNodeFilterExpression Parse(string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) throw Error("target_filter_required", "A nonempty filter is required. Use all: true to select all authorized nodes explicitly.");
        if (query.Length > MaximumQueryLength) throw Error("target_filter_too_large", "The filter exceeds 4096 characters.");
        return new Parser(query).Parse();
    }

    public static JobNodeFilterExpression Bind(JobNodeFilterExpression expression, IReadOnlyDictionary<string, string[]>? parameters)
    {
        if (parameters?.Count > MaximumOperands) throw Error("target_filter_too_complex", "Target bindings accept at most 128 named parameters.");
        var values = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        if (parameters is not null)
            foreach (var parameter in parameters)
                if (!values.TryAdd(parameter.Key, parameter.Value))
                    throw Error("target_parameter_ambiguous", "Target parameter names must be unique under case-insensitive comparison.");
        var boundCount = 0;
        return new(expression.Clauses.Select(clause => clause with
        {
            Alternatives = clause.Alternatives.Select(term => term with
            {
                Operands = term.Operands.Select(operand =>
                {
                    if (operand.Kind != JobNodeFilterOperandKind.Parameter)
                    {
                        if (++boundCount > MaximumOperands) throw Error("target_filter_too_complex", "Bound target operands exceed 128 values.");
                        return operand;
                    }
                    if (!values.TryGetValue(operand.Value, out var supplied) || supplied is null || supplied.Length == 0)
                        throw Error("target_parameter_required", $"The declared target parameter '{operand.Value}' needs bounded literal values.");
                    if (supplied.Length > MaximumOperands || boundCount + supplied.Length > MaximumOperands)
                        throw Error("target_filter_too_complex", "Raw bound target operands exceed 128 values before deduplication.");
                    if (supplied.Any(value => string.IsNullOrWhiteSpace(value) || value.Length > MaximumLiteralLength))
                        throw Error("target_parameter_required", $"The declared target parameter '{operand.Value}' needs bounded literal values.");
                    boundCount += supplied.Length;
                    var literals = supplied.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                    return new JobNodeFilterOperand(JobNodeFilterOperandKind.Literal, literals[0]) { BoundValues = literals };
                }).ToArray()
            }).ToArray()
        }).ToArray());
    }

    public static IReadOnlyList<string> ParameterNames(JobNodeFilterExpression expression) => expression.Clauses
        .SelectMany(x => x.Alternatives).SelectMany(x => x.Operands).Where(x => x.Kind == JobNodeFilterOperandKind.Parameter)
        .Select(x => x.Value).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();

    public static string Format(JobNodeFilterExpression expression) => string.Join(" ", expression.Clauses.Select(clause =>
        (clause.Excluded ? "!" : "") + clause.Field + ": " + string.Join(",", clause.Alternatives.Select(term =>
            string.Join("+", term.Operands.Select(FormatOperand))))));

    private static string FormatOperand(JobNodeFilterOperand operand)
    {
        if (operand.BoundValues is { Count: > 1 }) throw Error("bound_filter_format_unsupported", "A bound AST with list operands is not an editable query string.");
        return operand.Kind switch
        {
            JobNodeFilterOperandKind.Regex => "/" + operand.Value.Replace("/", "\\/", StringComparison.Ordinal) + "/",
            JobNodeFilterOperandKind.Parameter => "@option." + operand.Value + "@",
            _ => "\"" + operand.Value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\""
        };
    }

    internal static Regex CompileRegex(string pattern)
    {
        if (pattern.Length == 0 || pattern.Length > MaximumRegexLength) throw Error("target_regex_invalid", "Regex operands require 1–256 characters.");
        try { return new(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, RegexTimeout); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        { throw Error("target_regex_unsupported", "The regex is invalid or uses a construct unsupported by the bounded nonbacktracking engine."); }
    }

    private static JobTargetValidationException Error(string code, string message) => new(code, message);

    private sealed class Parser(string text)
    {
        private int position;
        private int operands;
        private readonly List<JobNodeFilterClause> clauses = [];
        private readonly List<JobNodeFilterTerm> bare = [];

        public JobNodeFilterExpression Parse()
        {
            while (true)
            {
                Space(); if (position == text.Length) break;
                var excluded = Take('!');
                var saved = position;
                var field = ReadField();
                if (field is not null && Take(':'))
                {
                    var canonical = CanonicalField(field);
                    Space(); var terms = ReadAlternatives(canonical);
                    if (canonical == "all" && (excluded || terms.Count != 1 || terms[0].Operands.Count != 1 ||
                        terms[0].Operands[0].Kind != JobNodeFilterOperandKind.Literal || !string.Equals(terms[0].Operands[0].Value, "true", StringComparison.OrdinalIgnoreCase)))
                        throw Error("target_filter_invalid_all", "The explicit all-nodes clause is all: true.");
                    clauses.Add(new(canonical, excluded, terms));
                }
                else
                {
                    position = saved;
                    if (excluded) throw Error("target_filter_invalid", "Exclusions require an allowlisted property followed by ':'.");
                    var alternatives = ReadAlternatives("node");
                    bare.AddRange(alternatives);
                }
                if (clauses.Count + (bare.Count > 0 ? 1 : 0) > MaximumClauses)
                    throw Error("target_filter_too_complex", "The filter exceeds 32 clauses.");
                if (position < text.Length && !char.IsWhiteSpace(text[position]))
                    throw Error("target_filter_invalid", "Separate property clauses with whitespace.");
            }
            if (bare.Count > 0) clauses.Insert(0, new("node", false, bare));
            if (!clauses.Any(x => !x.Excluded)) throw Error("target_filter_inclusion_required", "An exclusion needs an explicit inclusion clause or all: true.");
            return new(clauses.ToArray());
        }

        private List<JobNodeFilterTerm> ReadAlternatives(string field)
        {
            var result = new List<JobNodeFilterTerm>();
            while (true)
            {
                var conjunction = new List<JobNodeFilterOperand> { ReadOperand(field) };
                while (Take('+'))
                {
                    if (field != "tags") throw Error("target_filter_invalid_operator", "The + all-of operator is supported only for tags.");
                    Space(); conjunction.Add(ReadOperand(field));
                }
                result.Add(new(conjunction.ToArray()));
                if (!Take(',')) break;
                Space();
            }
            return result;
        }

        private JobNodeFilterOperand ReadOperand(string field)
        {
            if (position == text.Length || text[position] is ',' or '+' or ':' || char.IsWhiteSpace(text[position]))
                throw Error("target_filter_operand_required", "Every property and operator needs an operand.");
            var kind = JobNodeFilterOperandKind.Literal;
            var quoted = text[position] is '\'' or '"';
            string value;
            if (quoted) value = ReadDelimited(text[position], regex: false);
            else if (text[position] == '/') { kind = JobNodeFilterOperandKind.Regex; value = ReadDelimited('/', regex: true); }
            else
            {
                var start = position;
                while (position < text.Length && !char.IsWhiteSpace(text[position]) && text[position] is not (',' or '+' or ':')) position++;
                value = text[start..position];
            }
            if (value.Length == 0 || value.Length > MaximumLiteralLength) throw Error("target_filter_operand_invalid", "Literal operands require 1–512 characters.");
            if (!quoted && kind == JobNodeFilterOperandKind.Literal && value.StartsWith("@option.", StringComparison.OrdinalIgnoreCase))
            {
                if (!value.EndsWith('@') || value.Length < 10 || value[8..^1].Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '_' or '-')))
                    throw Error("target_parameter_invalid", "A parameter operand uses the existing @option.name@ notation.");
                kind = JobNodeFilterOperandKind.Parameter; value = value[8..^1];
            }
            if (kind == JobNodeFilterOperandKind.Regex)
            {
                if (field is "id" or "all") throw Error("target_regex_not_supported", "Regex is not supported for this property.");
                _ = CompileRegex(value);
            }
            if (++operands > MaximumOperands) throw Error("target_filter_too_complex", "The filter exceeds 128 operands.");
            return new(kind, value);
        }

        private string ReadDelimited(char delimiter, bool regex)
        {
            position++; var value = new StringBuilder();
            while (position < text.Length)
            {
                var c = text[position++];
                if (c == delimiter) return value.ToString();
                if (c == '\\')
                {
                    if (position == text.Length) break;
                    var next = text[position++];
                    if (regex && next != '/') value.Append('\\');
                    value.Append(next);
                }
                else value.Append(c);
            }
            throw Error("target_filter_unterminated", regex ? "The regex delimiter is not closed." : "The quoted literal is not closed.");
        }
        private string? ReadField()
        {
            var start = position;
            while (position < text.Length && (char.IsAsciiLetterOrDigit(text[position]) || text[position] is '_' or '-')) position++;
            return position == start ? null : text[start..position];
        }
        private bool Take(char c) { if (position < text.Length && text[position] == c) { position++; return true; } return false; }
        private void Space() { while (position < text.Length && char.IsWhiteSpace(text[position])) position++; }
        private static string CanonicalField(string field) => field.ToLowerInvariant() switch
        {
            "id" or "agentid" or "clientid" => "id",
            "hostname" => "hostname",
            "name" => "name",
            "displayname" or "friendlyname" => "displayname",
            "tags" or "tag" => "tags",
            "os" => "os",
            "architecture" or "arch" => "architecture",
            "all" => "all",
            _ => throw Error("target_filter_unknown_property", "The filter contains an unsupported property; use the published field catalogue.")
        };
    }
}
