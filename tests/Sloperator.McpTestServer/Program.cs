using Sloperator.McpTestServer;
using ModelContextProtocol.Server;

// The test MCP server over stdio, the way Sloperator starts real local servers.
await using var server = McpServer.Create(new StdioServerTransport("bb-test"), TestTools.ServerOptions());
await server.RunAsync();
