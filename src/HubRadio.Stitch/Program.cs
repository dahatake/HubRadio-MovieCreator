using HubRadio.Infrastructure.Rendering;

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};

return await BookendStitchCommand.RunAsync(args, Console.Out, Console.Error,
    Path.Combine(AppContext.BaseDirectory, "Tools"), cancellation.Token);
