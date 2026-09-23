namespace Martlet.Avatar.Hosting;

public sealed class RendererFailureLatch
{
    private int failed;
    public bool Failed => Volatile.Read(ref failed) != 0;
    public bool Fail() => Interlocked.Exchange(ref failed, 1) == 0;
    public void ThrowIfFailed()
    {
        if (Failed) throw new InvalidDataException("Renderer failed; a fresh inspection is required.");
    }
}
