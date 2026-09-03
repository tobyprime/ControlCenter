using DevicePanel.Agent;

var options = AgentOptions.Parse(args, Environment.GetEnvironmentVariable);
var runner = new AgentRunner(options, Console.Out);

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cts.Cancel();
};

var exitCode = await runner.RunAsync(cts.Token);
return exitCode;
