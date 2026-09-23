using System.Collections.Concurrent;
using System.Net;
using Grpc.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NvidiaAce.Services.A2FController.V1;
using Input = NvidiaAce.Controller.V1.AudioStream;
using Output = NvidiaAce.Controller.V1.AnimationDataStream;

namespace Martlet.Avatar.Audio2Face.Tests;

public sealed class ProtocolFixture : IAsyncDisposable
{
    private readonly WebApplication app;
    internal ConcurrentQueue<Input> Requests { get; } = new();
    internal TaskCompletionSource Stopped { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource ReceivedAudio { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal Func<IServerStreamWriter<Output>, ServerCallContext, Task> Respond { get; }
    internal Uri Endpoint { get; private set; } = null!;
    internal bool RespondBeforeUpload { get; private init; }

    private ProtocolFixture(WebApplication app, Func<IServerStreamWriter<Output>, ServerCallContext, Task> respond)
    {
        this.app = app;
        Respond = respond;
    }

    internal static async Task<ProtocolFixture> StartAsync(
        Func<IServerStreamWriter<Output>, ServerCallContext, Task> respond, bool respondBeforeUpload = false)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, 0,
            endpoint => endpoint.Protocols = HttpProtocols.Http2));
        builder.Services.AddGrpc();
        var holder = new Holder();
        builder.Services.AddSingleton(holder);
        var app = builder.Build();
        var fixture = new ProtocolFixture(app, respond) { RespondBeforeUpload = respondBeforeUpload };
        holder.Fixture = fixture;
        app.MapGrpcService<Service>();
        await app.StartAsync();
        fixture.Endpoint = new Uri(app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.Single());
        return fixture;
    }

    public async ValueTask DisposeAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await app.StopAsync(timeout.Token);
        await app.DisposeAsync();
    }

    public sealed class Holder
    {
        internal ProtocolFixture Fixture { get; set; } = null!;
    }

    public sealed class Service(Holder holder) : A2FControllerService.A2FControllerServiceBase
    {
        public override async Task ProcessAudioStream(IAsyncStreamReader<Input> requestStream,
            IServerStreamWriter<Output> responseStream, ServerCallContext context)
        {
            var fixture = holder.Fixture;
            try
            {
                await foreach (var input in requestStream.ReadAllAsync(context.CancellationToken))
                {
                    fixture.Requests.Enqueue(input);
                    if (fixture.RespondBeforeUpload && fixture.Requests.Count == 1)
                        await fixture.Respond(responseStream, context);
                }
                fixture.ReceivedAudio.TrySetResult();
                if (!fixture.RespondBeforeUpload)
                    await fixture.Respond(responseStream, context);
            }
            finally
            {
                fixture.Stopped.TrySetResult();
            }
        }
    }
}
