using Microsoft.Extensions.Logging;

namespace OpenMoca.Mcp;

/// <summary>Writes log lines to standard error as <c>moca-mcp: …</c>; standard output is the MCP protocol.</summary>
internal sealed class StderrLogger : ILogger
{
    public static readonly StderrLogger Instance = new();

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (IsEnabled(logLevel))
        {
            Console.Error.WriteLine($"moca-mcp: {formatter(state, exception)}");
        }
    }
}
