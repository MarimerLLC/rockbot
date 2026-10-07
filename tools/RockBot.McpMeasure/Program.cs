using RockBot.McpMeasure;
using Spectre.Console.Cli;

var app = new CommandApp<MeasureCommand>();
app.Configure(config => config.SetApplicationName("rockbot-mcp-measure"));
return await app.RunAsync(args);
