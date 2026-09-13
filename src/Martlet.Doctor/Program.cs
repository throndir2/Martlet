using Martlet.Doctor;

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancellation.Cancel();
};
return await DoctorCommand.RunAsync(args, Console.Out, cancellation.Token);
