using System.Data;

// ReSharper disable AssignNullToNotNullAttribute

namespace Masterly.NightShift.SqlServer.Tests;

public class SqlServerTransactionJobFacts
{
    private const string JobId = "id";
    private const string Queue = "queue";

    private readonly Mock<IDbConnection> _connection;
    private readonly Mock<IDbTransaction> _transaction;
    private readonly Mock<SqlServerStorage> _storage;

    public SqlServerTransactionJobFacts()
    {
        _connection = new Mock<IDbConnection>();
        _transaction = new Mock<IDbTransaction>();
        _storage = new Mock<SqlServerStorage>(ConnectionUtils.GetConnectionString());
    }

    [Fact]
    public void Ctor_ThrowsAnException_WhenStorageIsNull()
    {
        ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
            () => new SqlServerTransactionJob(null, _connection.Object, _transaction.Object, JobId, Queue));

        Assert.Equal("storage", exception.ParamName);
    }

    [Fact]
    public void Ctor_ThrowsAnException_WhenConnectionIsNull()
    {
        ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
            () => new SqlServerTransactionJob(_storage.Object, null, _transaction.Object, JobId, Queue));

        Assert.Equal("connection", exception.ParamName);
    }

    [Fact]
    public void Ctor_ThrowsAnException_WhenTransactionIsNull()
    {
        ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
            () => new SqlServerTransactionJob(_storage.Object, _connection.Object, null, JobId, Queue));

        Assert.Equal("transaction", exception.ParamName);
    }

    [Fact]
    public void Ctor_ThrowsAnException_WhenJobIdIsNull()
    {
        ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
            () => new SqlServerTransactionJob(_storage.Object, _connection.Object, _transaction.Object, null, Queue));

        Assert.Equal("jobId", exception.ParamName);
    }

    [Fact]
    public void Ctor_ThrowsAnException_WhenQueueIsNull()
    {
        ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
            () => new SqlServerTransactionJob(_storage.Object, _connection.Object, _transaction.Object, JobId, null));

        Assert.Equal("queue", exception.ParamName);
    }

    [Fact]
    public void Ctor_CorrectlySets_AllInstanceProperties()
    {
        SqlServerTransactionJob fetchedJob = new(_storage.Object, _connection.Object, _transaction.Object, JobId, Queue);

        Assert.Equal(JobId, fetchedJob.JobId);
        Assert.Equal(Queue, fetchedJob.Queue);
    }

    [Fact]
    public void RemoveFromQueue_CommitsTheTransaction()
    {
        // Arrange
        SqlServerTransactionJob processingJob = CreateFetchedJob("1", "default");

        // Act
        processingJob.RemoveFromQueue();

        // Assert
        _transaction.Verify(x => x.Commit());
    }

    [Fact]
    public void Requeue_RollsbackTheTransaction()
    {
        // Arrange
        SqlServerTransactionJob processingJob = CreateFetchedJob("1", "default");

        // Act
        processingJob.Requeue();

        // Assert
        _transaction.Verify(x => x.Rollback());
    }

    [Fact]
    public void Dispose_DisposesTheTransactionAndConnection()
    {
        SqlServerTransactionJob processingJob = CreateFetchedJob("1", "queue");

        // Act
        processingJob.Dispose();

        // Assert
        _transaction.Verify(x => x.Dispose());
        _connection.Verify(x => x.Dispose());
    }

    private SqlServerTransactionJob CreateFetchedJob(string jobId, string queue)
    {
        return new SqlServerTransactionJob(_storage.Object, _connection.Object, _transaction.Object, jobId, queue);
    }
}
