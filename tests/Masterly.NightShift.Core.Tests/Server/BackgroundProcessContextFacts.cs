using Masterly.NightShift.Server;

// ReSharper disable AssignNullToNotNullAttribute

namespace Masterly.NightShift.Core.Tests.Server;

public class BackgroundProcessContextFacts
{
    private readonly Mock<JobStorage> _storage;
    private readonly Dictionary<string, object> _properties;
    private readonly Guid _executionId;

    public BackgroundProcessContextFacts()
    {
        _storage = new Mock<JobStorage>();
        _properties = new Dictionary<string, object> { { "key", "value" } };
        _executionId = Guid.NewGuid();
    }

    [Fact]
    public void Ctor_ThrowsAnException_WhenServerIdIsNull()
    {
        var exception = Assert.Throws<ArgumentNullException>(
            () => new BackgroundProcessContext(null, _storage.Object, _properties, _executionId,
                CancellationToken.None, CancellationToken.None, CancellationToken.None));

        Assert.Equal("serverId", exception.ParamName);
    }

    [Fact]
    public void Ctor_ThrowsAnException_WhenStorageIsNull()
    {
        var exception = Assert.Throws<ArgumentNullException>(
            () => new BackgroundProcessContext("server", null, _properties, _executionId,
                CancellationToken.None, CancellationToken.None, CancellationToken.None));

        Assert.Equal("storage", exception.ParamName);
    }

    [Fact]
    public void Ctor_ThrowsAnException_WhenPropertiesIsNull()
    {
        var exception = Assert.Throws<ArgumentNullException>(
            () => new BackgroundProcessContext("server", _storage.Object, null, _executionId,
                CancellationToken.None, CancellationToken.None, CancellationToken.None));

        Assert.Equal("properties", exception.ParamName);
    }

    [Fact]
    public void Ctor_CopiesAllTheValues()
    {
        using var stopping = new CancellationTokenSource();
        using var stopped = new CancellationTokenSource();
        using var shutdown = new CancellationTokenSource();

        var context = new BackgroundProcessContext("server", _storage.Object, _properties, _executionId,
            stopping.Token, stopped.Token, shutdown.Token);

        Assert.Equal("server", context.ServerId);
        Assert.Same(_storage.Object, context.Storage);
        Assert.Equal(_executionId, context.ExecutionId);
        Assert.Equal(stopping.Token, context.StoppingToken);
        Assert.Equal(stopped.Token, context.StoppedToken);
        Assert.Equal(shutdown.Token, context.ShutdownToken);
    }

    [Fact]
    public void Properties_AreACaseInsensitiveCopy()
    {
        var context = CreateContext(CancellationToken.None, CancellationToken.None, CancellationToken.None);
        _properties["other"] = "added later";

        Assert.Equal("value", context.Properties["KEY"]);
        Assert.False(context.Properties.ContainsKey("other"));
    }

    [Fact]
    public void IsStoppingAndIsStopped_AreFalse_WhenNoTokenIsCanceled()
    {
        var context = CreateContext(CancellationToken.None, CancellationToken.None, CancellationToken.None);

        Assert.False(context.IsStopping);
        Assert.False(context.IsStopped);
    }

    [Fact]
    public void IsStopping_IsTrue_ButIsStoppedIsFalse_WhenOnlyStoppingTokenIsCanceled()
    {
        var context = CreateContext(new CancellationToken(true), CancellationToken.None, CancellationToken.None);

        Assert.True(context.IsStopping);
        Assert.False(context.IsStopped);
    }

    [Fact]
    public void IsStoppingAndIsStopped_AreTrue_WhenStoppedTokenIsCanceled()
    {
        var context = CreateContext(CancellationToken.None, new CancellationToken(true), CancellationToken.None);

        Assert.True(context.IsStopping);
        Assert.True(context.IsStopped);
    }

    [Fact]
    public void IsStoppingAndIsStopped_AreTrue_WhenShutdownTokenIsCanceled()
    {
        var context = CreateContext(CancellationToken.None, CancellationToken.None, new CancellationToken(true));

        Assert.True(context.IsStopping);
        Assert.True(context.IsStopped);
    }

    private BackgroundProcessContext CreateContext(
        CancellationToken stoppingToken, CancellationToken stoppedToken, CancellationToken shutdownToken)
    {
        return new BackgroundProcessContext("server", _storage.Object, _properties, _executionId,
            stoppingToken, stoppedToken, shutdownToken);
    }
}
