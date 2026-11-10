using System.Diagnostics;
using System.Text.RegularExpressions;
using NetRatel.Shared.Contracts.Jobs;

namespace NetRatel.Application.Jobs;

/// <summary>Evaluates complete authorized inventory. A bound or regex failure never returns partial targets.</summary>
public static class JobNodeFilterEvaluator
{
    public const int MaximumCandidates = 10_000;
    public const int MaximumTargets = 10_000;
    public static readonly TimeSpan EvaluationBudget = TimeSpan.FromMilliseconds(500);

    public static IReadOnlyList<JobNodeInventoryDto> Resolve(JobNodeFilterExpression expression,
        IReadOnlyList<JobNodeInventoryDto> inventory, CancellationToken cancellationToken = default)
    {
        if (inventory.Count > MaximumCandidates) throw Error("target_inventory_capacity_exceeded", "The authorized inventory exceeds the selector's 10000-node evaluation bound.");
        var timer = Stopwatch.StartNew();
        var regexes = expression.Clauses.SelectMany(x => x.Alternatives).SelectMany(x => x.Operands)
            .Where(x => x.Kind == JobNodeFilterOperandKind.Regex).Select(x => x.Value).Distinct(StringComparer.Ordinal)
            .ToDictionary(x => x, JobNodeFilterParser.CompileRegex, StringComparer.Ordinal);
        if (expression.Clauses.SelectMany(x => x.Alternatives).SelectMany(x => x.Operands)
            .Any(x => x.Kind == JobNodeFilterOperandKind.Parameter))
            throw Error("target_parameter_required", "Bind all declared target parameters before evaluation.");
        foreach (var operand in expression.Clauses.Where(x => x.Field == "node" && !x.Excluded)
            .SelectMany(x => x.Alternatives).SelectMany(x => x.Operands).Where(x => x.Kind == JobNodeFilterOperandKind.Literal))
        {
            foreach (var literal in LiteralValues(operand))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (timer.Elapsed > EvaluationBudget) throw Error("target_filter_budget_exhausted", "The complete filter evaluation exceeded its 500ms budget.");
                if (inventory.Count(node => Equal(node.Hostname, literal) || Equal(node.Name, literal)) > 1)
                    throw Error("target_name_ambiguous", "An exact node name identifies more than one authorized client; select stable IDs instead.");
            }
        }
        var includes = expression.Clauses.Where(x => !x.Excluded).GroupBy(x => x.Field, StringComparer.Ordinal).ToArray();
        var exclusions = expression.Clauses.Where(x => x.Excluded).ToArray();
        var selected = new List<JobNodeInventoryDto>();
        try
        {
            foreach (var node in inventory)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (timer.Elapsed > EvaluationBudget) throw Error("target_filter_budget_exhausted", "The complete filter evaluation exceeded its 500ms budget.");
                if (includes.All(group => group.Any(clause => MatchClause(node, clause))) && !exclusions.Any(clause => MatchClause(node, clause)))
                {
                    selected.Add(node);
                    if (selected.Count > MaximumTargets) throw Error("target_result_capacity_exceeded", "The resolved target set exceeds 10000 nodes; narrow the selector.");
                }
            }
        }
        catch (RegexMatchTimeoutException) { throw Error("target_filter_budget_exhausted", "Regex evaluation exceeded its bounded evaluation budget."); }
        cancellationToken.ThrowIfCancellationRequested();
        if (timer.Elapsed > EvaluationBudget) throw Error("target_filter_budget_exhausted", "The complete filter evaluation exceeded its 500ms budget.");
        if (selected.Count == 0) throw Error("target_no_matches", "The selector matches no authorized nodes.");
        return selected.DistinctBy(x => x.AgentId).OrderBy(x => JobTargetNode.SortKey(x.Hostname, x.Name, x.DisplayName), StringComparer.OrdinalIgnoreCase).ThenBy(x => x.AgentId).ToArray();

        bool MatchClause(JobNodeInventoryDto node, JobNodeFilterClause clause) => clause.Field == "all" ||
            clause.Alternatives.Any(term => term.Operands.All(operand => MatchOperand(node, clause.Field, operand)));

        bool MatchOperand(JobNodeInventoryDto node, string field, JobNodeFilterOperand operand)
        {
            var properties = Values(node, field);
            return operand.Kind == JobNodeFilterOperandKind.Regex
                ? properties.Any(value => regexes[operand.Value].IsMatch(value))
                : properties.Any(value => LiteralValues(operand).Any(literal => Equal(value, literal)));
        }
    }

    public static bool MatchesSearch(JobNodeInventoryDto node, string? search) => string.IsNullOrWhiteSpace(search) ||
        new[] { node.Hostname, node.DisplayName, node.Name, node.OperatingSystem, node.Architecture, node.AgentId.ToString("D") }
            .Concat(node.Tags).Any(value => value?.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase) == true);

    private static IEnumerable<string> Values(JobNodeInventoryDto node, string field) => field switch
    {
        "id" => [node.AgentId.ToString("D")],
        "hostname" => Present(node.Hostname),
        "name" => Present(node.Name),
        "node" => Present(node.Hostname).Concat(Present(node.Name)),
        "displayname" => Present(node.DisplayName),
        "tags" => node.Tags,
        "os" => Present(node.OperatingSystem),
        "architecture" => Present(node.Architecture),
        _ => throw Error("target_filter_unknown_property", "The AST contains an unsupported property.")
    };
    private static IEnumerable<string> Present(string? value) => string.IsNullOrWhiteSpace(value) ? [] : [value];
    private static IReadOnlyList<string> LiteralValues(JobNodeFilterOperand operand) => operand.BoundValues ?? [operand.Value];
    private static bool Equal(string? left, string right) => left is not null && string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    private static JobTargetValidationException Error(string code, string message) => new(code, message);
}
