namespace WriteableFileProviders.AspNetCore.Tests;

/// <summary>A clock the test sets.</summary>
/// <param name="now">The time it starts at.</param>
internal sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}
