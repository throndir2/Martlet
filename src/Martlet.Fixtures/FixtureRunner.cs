namespace Martlet.Fixtures;

public static class FixtureRunner
{
    public static FixtureTrace Run(FixtureScenario scenario, CancellationToken cancellationToken = default)
    {
        using var cursor = new FixtureCursor(scenario, cancellationToken);
        while (cursor.NextMilliseconds is not null && !cancellationToken.IsCancellationRequested)
            cursor.Advance();
        return cursor.Finish();
    }
}
