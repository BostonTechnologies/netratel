using System.CommandLine;

internal sealed class CliInvocationContext(ParseResult parseResult, CancellationToken cancellationToken)
{
    public ParseResult ParseResult { get; } = parseResult;

    public int ExitCode { get; set; }

    public CancellationToken GetCancellationToken() => cancellationToken;
}

internal static class CommandLineCompatibility
{
    public static void Add(this Command command, Symbol symbol)
    {
        switch (symbol)
        {
            case Command childCommand:
                command.Subcommands.Add(childCommand);
                break;
            case Option option:
                command.Options.Add(option);
                break;
            case Argument argument:
                command.Arguments.Add(argument);
                break;
            default:
                throw new ArgumentException("Only commands, options, and arguments can be children of a command.", nameof(symbol));
        }
    }

    public static void AddCommand(this Command command, Command childCommand) => command.Subcommands.Add(childCommand);

    public static void AddOption(this Command command, Option option) => command.Options.Add(option);

    public static void AddGlobalOption(this Command command, Option option) => command.Options.Add(option);

    public static void AddArgument(this Command command, Argument argument) => command.Arguments.Add(argument);

    public static void SetHandler(this Command command, Func<CliInvocationContext, Task> handler)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(handler);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var context = new CliInvocationContext(parseResult, cancellationToken);
            await handler(context).ConfigureAwait(false);
            return context.ExitCode;
        });
    }

    public static T? GetValueForOption<T>(this ParseResult parseResult, Option<T> option) =>
        parseResult.GetValue(option);

    public static object? GetValueForOption(this ParseResult parseResult, Option option) =>
        parseResult.GetResult(option)?.GetValueOrDefault<object?>();

    public static T? GetValueForArgument<T>(this ParseResult parseResult, Argument<T> argument) =>
        parseResult.GetValue(argument);

    public static object? GetValueForArgument(this ParseResult parseResult, Argument argument) =>
        parseResult.GetResult(argument)?.GetValueOrDefault<object?>();
}
