using System;
using System.Text.Json;
using Masterly.NightShift.States;
using Masterly.NightShift.Storage;
using Xunit;

namespace Masterly.NightShift.SqlServer.Tests
{
    /// <summary>
    /// An application whose serializer options only know its own types, like a source-generated context, at every
    /// compatibility level: the library's own data (the job's arguments list, state data, continuations, a deleted
    /// job's exception) must still be stored.
    /// </summary>
    public class RestrictiveSerializerSettingsFacts
    {
        [Theory, CleanDatabase, CleanSerializerSettings]
        [InlineData(CompatibilityLevel.Version_110)]
        [InlineData(CompatibilityLevel.Version_170)]
        [InlineData(CompatibilityLevel.Version_180)]
        public void Jobs_AreCreated_AndChangeState_WhenTheApplicationsOptionsOnlyKnowTheArgumentTypes(CompatibilityLevel level)
        {
            GlobalConfiguration.Configuration.SetDataCompatibilityLevel(level);
            GlobalConfiguration.Configuration.UseSerializerSettings(new JsonSerializerOptions
            {
                TypeInfoResolver = new KnownTypesResolver(typeof(string), typeof(int))
            });

            try
            {
                var storage = new SqlServerStorage(ConnectionUtils.GetConnectionString(), new SqlServerStorageOptions { PrepareSchemaIfNecessary = false });
                var client = new BackgroundJobClient(storage);

                var parent = client.Create(() => RestrictedJobs.Run(5, "a"), new EnqueuedState("default"));
                var continuation = client.ContinueJobWith(parent, () => RestrictedJobs.Run(6, "b"));
                var deleted = client.Create(() => RestrictedJobs.Run(7, "c"), new EnqueuedState());

                Assert.True(client.ChangeState(parent, new SucceededState(null, 1, 1), EnqueuedState.StateName));
                Assert.True(client.ChangeState(deleted, new DeletedState(new ExceptionInfo(new InvalidOperationException("boom"))), EnqueuedState.StateName));

                using (var connection = storage.GetConnection())
                {
                    var job = connection.GetJobData(parent);
                    Assert.Null(job.LoadException);
                    Assert.Equal(new object[] { 5, "a" }, job.Job.Args);
                    Assert.Equal(EnqueuedState.StateName, connection.GetStateData(continuation).Name);
                    Assert.Equal("default", connection.GetStateData(continuation).Data["Queue"]);
                    Assert.Equal(DeletedState.StateName, connection.GetStateData(deleted).Name);
                }
            }
            finally
            {
                GlobalConfiguration.Configuration.SetDataCompatibilityLevel(CompatibilityLevel.Version_110);
            }
        }
    }

    public static class RestrictedJobs
    {
        // ReSharper disable once UnusedParameter.Global
        public static void Run(int number, string text)
        {
        }
    }
}
