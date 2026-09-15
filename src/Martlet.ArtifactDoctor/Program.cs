using Martlet.ArtifactDoctor;

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancellation.Cancel();
};
return await ArtifactDoctorCommand.RunAsync(args, Console.Out, cancellation.Token);
