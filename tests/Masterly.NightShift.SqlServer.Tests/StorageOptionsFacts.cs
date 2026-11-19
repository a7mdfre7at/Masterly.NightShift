namespace Masterly.NightShift.SqlServer.Tests;

public class StorageOptionsFacts
{
    [Fact]
    public void Ctor_SetsTheDefaultOptions()
    {
        SqlServerStorageOptions options = new();

        Assert.True(options.QueuePollInterval == TimeSpan.Zero);
        Assert.True(options.InvisibilityTimeout > TimeSpan.Zero);
        Assert.True(options.JobExpirationCheckInterval > TimeSpan.Zero);
        Assert.Equal(TimeSpan.FromMinutes(5), options.CountersAggregateInterval);
        Assert.Equal("HangFire", options.SchemaName);
        Assert.True(options.PrepareSchemaIfNecessary);
    }

    [Fact]
    public void Ctor_DefaultsSqlClientFactory_ToMicrosoftDataSqlClient()
    {
        SqlServerStorageOptions options = new();

        Assert.Same(Microsoft.Data.SqlClient.SqlClientFactory.Instance, options.SqlClientFactory);
    }

    [Fact]
    public void Set_SqlClientFactory_ThrowsAnException_WhenValueIsNull()
    {
        SqlServerStorageOptions options = new();

        ArgumentNullException exception = Assert.Throws<ArgumentNullException>(() => options.SqlClientFactory = null);

        Assert.Equal("value", exception.ParamName);
    }

    [Fact]
    public void Set_QueuePollInterval_DoesNotThrow_WhenGivenIntervalIsEqualToZero()
    {
        Exception? exception = Record.Exception(static () => new SqlServerStorageOptions { QueuePollInterval = TimeSpan.Zero });

        Assert.Null(exception);
    }

    [Fact]
    public void Set_QueuePollInterval_ShouldThrowAnException_WhenGivenIntervalIsNegative()
    {
        SqlServerStorageOptions options = new();
        Assert.Throws<ArgumentException>(
            () => options.QueuePollInterval = TimeSpan.FromSeconds(-1));
    }

    [Fact]
    public void Set_QueuePollInterval_SetsTheValue()
    {
        SqlServerStorageOptions options = new() { QueuePollInterval = TimeSpan.FromSeconds(1) };
        Assert.Equal(TimeSpan.FromSeconds(1), options.QueuePollInterval);
    }

    [Theory]
    [InlineData(null), InlineData(""), InlineData("   ")]
    public void Set_SchemaName_ThrowsAnException_WhenValueIsNullOrWhiteSpace(string schemaName)
    {
        SqlServerStorageOptions options = new();

        ArgumentException exception = Assert.Throws<ArgumentException>(() => options.SchemaName = schemaName);

        Assert.Equal("value", exception.ParamName);
        Assert.Equal("HangFire", options.SchemaName);
    }

    [Fact]
    public void Set_SchemaName_SetsTheValue()
    {
        SqlServerStorageOptions options = new() { SchemaName = "NightShift" };
        Assert.Equal("NightShift", options.SchemaName);
    }

    [Fact]
    public void Set_SchemaName_ThrowsAnException_WhenSetAgainToAnEmptyValue()
    {
        SqlServerStorageOptions options = new() { SchemaName = "NightShift" };

        Assert.Throws<ArgumentException>(() => options.SchemaName = "");
        Assert.Equal("NightShift", options.SchemaName);
    }

    [Theory]
    [InlineData(0), InlineData(-1)]
    public void Set_CountersAggregateInterval_ThrowsAnException_WhenValueIsNotPositive(int seconds)
    {
        SqlServerStorageOptions options = new();

        ArgumentException exception = Assert.Throws<ArgumentException>(
            () => options.CountersAggregateInterval = TimeSpan.FromSeconds(seconds));

        Assert.Equal("value", exception.ParamName);
    }

    [Fact]
    public void Set_CountersAggregateInterval_ThrowsAnException_WhenValueIsGreaterThanIntMaxValueMilliseconds()
    {
        SqlServerStorageOptions options = new();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => options.CountersAggregateInterval = TimeSpan.FromMilliseconds(int.MaxValue + 1L));
    }

    [Fact]
    public void Set_CountersAggregateInterval_SetsTheValue()
    {
        SqlServerStorageOptions options = new() { CountersAggregateInterval = TimeSpan.FromSeconds(30) };
        Assert.Equal(TimeSpan.FromSeconds(30), options.CountersAggregateInterval);
    }

    [Theory]
    [InlineData("Jobs]")]
    [InlineData("O'Brien")]
    [InlineData("Line\nBreak")]
    [InlineData("Tab\tStop")]
    public void Set_SchemaName_ThrowsAnException_WhenValueContainsCharactersTheInstallScriptCantHandle(string schemaName)
    {
        SqlServerStorageOptions options = new();

        ArgumentException exception = Assert.Throws<ArgumentException>(() => options.SchemaName = schemaName);

        Assert.Equal("value", exception.ParamName);
        Assert.Equal("HangFire", options.SchemaName);
    }

    [Fact]
    public void Set_SchemaName_ThrowsAnException_WhenValueIsLongerThan128Characters()
    {
        SqlServerStorageOptions options = new();

        ArgumentException exception = Assert.Throws<ArgumentException>(() => options.SchemaName = new string('s', 129));

        Assert.Contains("128", exception.Message);
        Assert.Equal("HangFire", options.SchemaName);
    }

    [Theory]
    [InlineData("jobs-prod")]
    [InlineData("Night Shift")]
    [InlineData("[jobs")]
    [InlineData("app.v2_$#@")]
    public void Set_SchemaName_AcceptsNames_ThatWorkAsDelimitedIdentifiers(string schemaName)
    {
        SqlServerStorageOptions options = new() { SchemaName = schemaName };
        Assert.Equal(schemaName, options.SchemaName);
    }

    [Fact]
    public void Set_SchemaName_AcceptsA128CharacterName()
    {
        string schemaName = new('s', 128);
        SqlServerStorageOptions options = new() { SchemaName = schemaName };
        Assert.Equal(schemaName, options.SchemaName);
    }

    [Theory]
    [InlineData("Jobs]")]
    [InlineData("O'Brien")]
    public void GetInstallScript_ThrowsAnException_ForASchemaNameThatWouldNeedEscaping(string schemaName)
    {
        ArgumentException exception = Assert.Throws<ArgumentException>(() => SqlServerObjectsInstaller.GetInstallScript(schemaName, false));

        Assert.Equal("schema", exception.ParamName);
    }

    [Fact]
    public void GetInstallScript_UsesTheDefaultSchema_WhenNoneIsGiven()
    {
        string script = SqlServerObjectsInstaller.GetInstallScript(null, false);

        Assert.Contains("[HangFire].[Job]", script);
        Assert.DoesNotContain("$(HangFireSchema)", script);
    }

    [Theory]
    [InlineData(0), InlineData(500), InlineData(999), InlineData(-1000)]
    public void Set_SlidingInvisibilityTimeout_ThrowsAnException_WhenValueIsShorterThanOneSecond(int milliseconds)
    {
        SqlServerStorageOptions options = new();

        ArgumentOutOfRangeException exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => options.SlidingInvisibilityTimeout = TimeSpan.FromMilliseconds(milliseconds));

        Assert.Equal("value", exception.ParamName);
        Assert.Equal(TimeSpan.FromMinutes(5), options.SlidingInvisibilityTimeout);
    }

    [Fact]
    public void Set_SlidingInvisibilityTimeout_AcceptsOneSecond_AndNull()
    {
        SqlServerStorageOptions options = new() { SlidingInvisibilityTimeout = TimeSpan.FromSeconds(1) };
        Assert.Equal(TimeSpan.FromSeconds(1), options.SlidingInvisibilityTimeout);

        options.SlidingInvisibilityTimeout = null;
        Assert.Null(options.SlidingInvisibilityTimeout);
    }

    [Theory]
    [InlineData(0), InlineData(500), InlineData(-5000)]
    public void Set_CommandTimeout_ThrowsAnException_WhenValueIsShorterThanOneSecond(int milliseconds)
    {
        SqlServerStorageOptions options = new();

        ArgumentOutOfRangeException exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => options.CommandTimeout = TimeSpan.FromMilliseconds(milliseconds));

        Assert.Equal("value", exception.ParamName);
        Assert.Null(options.CommandTimeout);
    }

    [Fact]
    public void Set_CommandTimeout_AcceptsOneSecond_AndNull()
    {
        SqlServerStorageOptions options = new() { CommandTimeout = TimeSpan.FromSeconds(1) };
        Assert.Equal(TimeSpan.FromSeconds(1), options.CommandTimeout);

        options.CommandTimeout = null;
        Assert.Null(options.CommandTimeout);
    }

    [Theory]
    [InlineData(0), InlineData(500), InlineData(-5000)]
    public void Set_CommandBatchMaxTimeout_ThrowsAnException_WhenValueIsShorterThanOneSecond(int milliseconds)
    {
        SqlServerStorageOptions options = new();

        ArgumentOutOfRangeException exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => options.CommandBatchMaxTimeout = TimeSpan.FromMilliseconds(milliseconds));

        Assert.Equal("value", exception.ParamName);
        Assert.Equal(TimeSpan.FromMinutes(5), options.CommandBatchMaxTimeout);
    }

    [Fact]
    public void Set_CommandBatchMaxTimeout_AcceptsOneSecond_AndNull()
    {
        SqlServerStorageOptions options = new() { CommandBatchMaxTimeout = TimeSpan.FromSeconds(1) };
        Assert.Equal(TimeSpan.FromSeconds(1), options.CommandBatchMaxTimeout);

        options.CommandBatchMaxTimeout = null;
        Assert.Null(options.CommandBatchMaxTimeout);
    }

    [Fact]
    public void Ctor_SetsTheDefaults_OfSchemaDependentOptions()
    {
        SqlServerStorageOptions options = new();

        Assert.False(options.DisableGlobalLocks);
        Assert.False(options.UseIgnoreDupKeyOption);
        Assert.Equal(-1, options.DeleteExpiredBatchSize);
        Assert.True(options.TryAutoDetectSchemaDependentOptions);
    }
}
