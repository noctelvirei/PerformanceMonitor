namespace PerformanceMonitor.Headless.Mcp;

internal static class CentralMcpInstructions
{
    public const string Text = """
        You are connected to the central repository mode of SQL Server Performance Monitor.

        This MCP server is read-only. It can only return data that the central collector has already gathered from monitored SQL Server instances. It cannot execute arbitrary SQL, kill sessions, change configuration, or install objects on monitored servers.

        Use list_servers first when you are unsure which SQL Server to inspect. Most tools accept server_name; if only one server is configured it is selected automatically. Results are snapshot-based, so mention collector freshness when data is missing or stale.

        The central repository may be DuckDB or SQL Server. Tool behavior is the same because tools read through the central telemetry interface.
        """;
}
