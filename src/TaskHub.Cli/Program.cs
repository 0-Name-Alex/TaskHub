using System.Text;
using TaskHub.Cli;

Console.OutputEncoding = Encoding.UTF8;
// MCP clients (Claude Desktop …) show up in TaskHub's events as "api:mcp".
var clientName = args.Length > 0 && args[0] == "mcp" ? "mcp" : "cli";
var app = new CliApp(() => ApiClient.FromDiscovery(clientName), Console.Out, Console.Error);
return await app.RunAsync(args);
