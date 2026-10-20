// Standalone FakeGG for the scripts in /scripts: runs GgHost on a fixed port and
// publishes Sonar's address in %LOCALAPPDATA%\SonarLite\sonar.json.

using Microsoft.Extensions.Logging;
using SonarLite.Core;

var port = args.Length > 0 ? int.Parse(args[0]) : 6399;
var discoveryFile = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SonarLite", "sonar.json");

using var loggerFactory = LoggerFactory.Create(b => b
    .SetMinimumLevel(LogLevel.Debug)
    .AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; }));

await using var host = new GgHost(port, loggerFactory, discoveryFile);
await host.StartAsync();

var stop = new TaskCompletionSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.TrySetResult(); };
AppDomain.CurrentDomain.ProcessExit += (_, _) => stop.TrySetResult();
await stop.Task;
