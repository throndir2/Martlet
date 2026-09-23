using System.Reflection;
using static Martlet.Perception.Tests.CaptureFixtures;

namespace Martlet.Perception.Tests;

public sealed class ProductionBoundaryTests
{
    [Fact]
    public async Task ProductionConstructionIsInertAndFailsBeforeConsentConsumption()
    {
        var clock = new ManualCaptureClock();
        var authorization = new SourceEnumerationAuthorization(
            new(
                SessionId,
                Guid.NewGuid(),
                ConfigurationRevision,
                ConfigurationRevision),
            clock.GetUtcNow().AddSeconds(10),
            selectedWindowEnumerationRequested: true,
            clock);

        await using var capture = new SelectedWindowCapture(
            SessionId,
            ConfigurationRevision,
            Options(),
            clock);

        Assert.False(capture.Availability.Eligible);
        Assert.Equal(
            PerceptionFailureCode.NativePrivacyUnqualified,
            capture.Availability.Failure.Code);
        Assert.Equal(
            PerceptionFailureCode.NativePrivacyUnqualified,
            Failure(() => capture.EnumerateSourcesAsync(authorization)
                .GetAwaiter()
                .GetResult()));
        Assert.False(authorization.IsConsumed);
    }

    [Fact]
    public async Task InjectedFactoryIsStillOffUntilExplicitEnumeration()
    {
        var factory = new ControlledNativeFactory();
        var clock = new ManualCaptureClock();
        await using var capture = Owner(factory, clock, Options());

        Assert.Equal(0, factory.Enumerations);
        Assert.Equal(0, factory.Opens);
        Assert.Equal(
            "Fixture selected window",
            Enumerate(capture, clock).WindowTitle);
        Assert.Equal(1, factory.Enumerations);
        Assert.Equal(0, factory.Opens);
    }

    [Fact]
    public void PublicSurfaceContainsNoSinkPersistenceInferenceOrStartupHook()
    {
        var publicMethods = new[]
            {
                typeof(SelectedWindowCapture),
                typeof(WindowCaptureOperation)
            }
            .SelectMany(type => type.GetMethods(
                BindingFlags.Public |
                BindingFlags.Instance |
                BindingFlags.Static |
                BindingFlags.DeclaredOnly))
            .ToArray();

        Assert.DoesNotContain(publicMethods, method =>
            method.Name.Contains("Upload", StringComparison.OrdinalIgnoreCase) ||
            method.Name.Contains("Persist", StringComparison.OrdinalIgnoreCase) ||
            method.Name.Contains("Save", StringComparison.OrdinalIgnoreCase) ||
            method.Name.Contains("Log", StringComparison.OrdinalIgnoreCase) ||
            method.Name.Contains("Ocr", StringComparison.OrdinalIgnoreCase) ||
            method.Name.Contains("Vlm", StringComparison.OrdinalIgnoreCase) ||
            method.Name.Contains("Provider", StringComparison.OrdinalIgnoreCase) ||
            method.Name.Contains("Startup", StringComparison.OrdinalIgnoreCase) ||
            method.GetParameters().Any(parameter =>
                parameter.ParameterType == typeof(Stream) ||
                parameter.ParameterType == typeof(Uri)));
    }
}
