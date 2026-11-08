using System;
using Xunit;

namespace Masterly.NightShift.SqlServer.Tests
{
    public class StorageOptionsFacts
    {
        [Fact]
        public void Ctor_SetsTheDefaultOptions()
        {
            var options = new SqlServerStorageOptions();

            Assert.True(options.QueuePollInterval == TimeSpan.Zero);
#pragma warning disable 618
            Assert.True(options.InvisibilityTimeout > TimeSpan.Zero);
#pragma warning restore 618
            Assert.True(options.JobExpirationCheckInterval > TimeSpan.Zero);
            Assert.Equal(TimeSpan.FromMinutes(5), options.CountersAggregateInterval);
            Assert.Equal("HangFire", options.SchemaName);
            Assert.True(options.PrepareSchemaIfNecessary);
        }

        [Fact]
        public void Set_QueuePollInterval_DoesNotThrow_WhenGivenIntervalIsEqualToZero()
        {
            var options = new SqlServerStorageOptions();
            options.QueuePollInterval = TimeSpan.Zero;
        }

        [Fact]
        public void Set_QueuePollInterval_ShouldThrowAnException_WhenGivenIntervalIsNegative()
        {
            var options = new SqlServerStorageOptions();
            Assert.Throws<ArgumentException>(
                () => options.QueuePollInterval = TimeSpan.FromSeconds(-1));
        }

        [Fact]
        public void Set_QueuePollInterval_SetsTheValue()
        {
            var options = new SqlServerStorageOptions { QueuePollInterval = TimeSpan.FromSeconds(1) };
            Assert.Equal(TimeSpan.FromSeconds(1), options.QueuePollInterval);
        }

        [Fact]
        public void Ctor_SetsTheDefaults_OfSchemaDependentOptions()
        {
            var options = new SqlServerStorageOptions();

            Assert.True(options.UseRecommendedIsolationLevel);
            Assert.False(options.DisableGlobalLocks);
            Assert.False(options.UseIgnoreDupKeyOption);
            Assert.Equal(-1, options.DeleteExpiredBatchSize);
            Assert.True(options.TryAutoDetectSchemaDependentOptions);
        }

        [Theory]
        [InlineData(null), InlineData(""), InlineData("   ")]
        public void Set_SchemaName_ThrowsAnException_WhenValueIsNullOrWhiteSpace(string schemaName)
        {
            var options = new SqlServerStorageOptions();

            var exception = Assert.Throws<ArgumentException>(() => options.SchemaName = schemaName);

            Assert.Equal("value", exception.ParamName);
            Assert.Equal("HangFire", options.SchemaName);
        }

        [Fact]
        public void Set_SchemaName_SetsTheValue()
        {
            var options = new SqlServerStorageOptions { SchemaName = "NightShift" };
            Assert.Equal("NightShift", options.SchemaName);
        }

        [Fact]
        public void Set_SchemaName_ThrowsAnException_WhenSetAgainToAnEmptyValue()
        {
            var options = new SqlServerStorageOptions { SchemaName = "NightShift" };

            Assert.Throws<ArgumentException>(() => options.SchemaName = "");
            Assert.Equal("NightShift", options.SchemaName);
        }

        [Theory]
        [InlineData(0), InlineData(-1)]
        public void Set_CountersAggregateInterval_ThrowsAnException_WhenValueIsNotPositive(int seconds)
        {
            var options = new SqlServerStorageOptions();

            var exception = Assert.Throws<ArgumentException>(
                () => options.CountersAggregateInterval = TimeSpan.FromSeconds(seconds));

            Assert.Equal("value", exception.ParamName);
        }

        [Fact]
        public void Set_CountersAggregateInterval_ThrowsAnException_WhenValueIsGreaterThanIntMaxValueMilliseconds()
        {
            var options = new SqlServerStorageOptions();

            Assert.Throws<ArgumentOutOfRangeException>(
                () => options.CountersAggregateInterval = TimeSpan.FromMilliseconds(int.MaxValue + 1L));
        }

        [Fact]
        public void Set_CountersAggregateInterval_SetsTheValue()
        {
            var options = new SqlServerStorageOptions { CountersAggregateInterval = TimeSpan.FromSeconds(30) };
            Assert.Equal(TimeSpan.FromSeconds(30), options.CountersAggregateInterval);
        }

        [Theory]
        [InlineData("Jobs]")]
        [InlineData("O'Brien")]
        [InlineData("Line\nBreak")]
        [InlineData("Tab\tStop")]
        public void Set_SchemaName_ThrowsAnException_WhenValueContainsCharactersTheInstallScriptCantHandle(string schemaName)
        {
            var options = new SqlServerStorageOptions();

            var exception = Assert.Throws<ArgumentException>(() => options.SchemaName = schemaName);

            Assert.Equal("value", exception.ParamName);
            Assert.Equal("HangFire", options.SchemaName);
        }

        [Fact]
        public void Set_SchemaName_ThrowsAnException_WhenValueIsLongerThan128Characters()
        {
            var options = new SqlServerStorageOptions();

            var exception = Assert.Throws<ArgumentException>(() => options.SchemaName = new string('s', 129));

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
            var options = new SqlServerStorageOptions { SchemaName = schemaName };
            Assert.Equal(schemaName, options.SchemaName);
        }

        [Fact]
        public void Set_SchemaName_AcceptsA128CharacterName()
        {
            var schemaName = new string('s', 128);
            var options = new SqlServerStorageOptions { SchemaName = schemaName };
            Assert.Equal(schemaName, options.SchemaName);
        }

        [Theory]
        [InlineData("Jobs]")]
        [InlineData("O'Brien")]
        public void GetInstallScript_ThrowsAnException_ForASchemaNameThatWouldNeedEscaping(string schemaName)
        {
            var exception = Assert.Throws<ArgumentException>(() => SqlServerObjectsInstaller.GetInstallScript(schemaName, false));

            Assert.Equal("schema", exception.ParamName);
        }

        [Fact]
        public void GetInstallScript_UsesTheDefaultSchema_WhenNoneIsGiven()
        {
            var script = SqlServerObjectsInstaller.GetInstallScript(null, false);

            Assert.Contains("[HangFire].[Job]", script);
            Assert.DoesNotContain("$(HangFireSchema)", script);
        }

        [Theory]
        [InlineData(0), InlineData(500), InlineData(999), InlineData(-1000)]
        public void Set_SlidingInvisibilityTimeout_ThrowsAnException_WhenValueIsShorterThanOneSecond(int milliseconds)
        {
            var options = new SqlServerStorageOptions();

            var exception = Assert.Throws<ArgumentOutOfRangeException>(
                () => options.SlidingInvisibilityTimeout = TimeSpan.FromMilliseconds(milliseconds));

            Assert.Equal("value", exception.ParamName);
            Assert.Equal(TimeSpan.FromMinutes(5), options.SlidingInvisibilityTimeout);
        }

        [Fact]
        public void Set_SlidingInvisibilityTimeout_AcceptsOneSecond_AndNull()
        {
            var options = new SqlServerStorageOptions { SlidingInvisibilityTimeout = TimeSpan.FromSeconds(1) };
            Assert.Equal(TimeSpan.FromSeconds(1), options.SlidingInvisibilityTimeout);

            options.SlidingInvisibilityTimeout = null;
            Assert.Null(options.SlidingInvisibilityTimeout);
        }

        [Theory]
        [InlineData(0), InlineData(500), InlineData(-5000)]
        public void Set_CommandTimeout_ThrowsAnException_WhenValueIsShorterThanOneSecond(int milliseconds)
        {
            var options = new SqlServerStorageOptions();

            var exception = Assert.Throws<ArgumentOutOfRangeException>(
                () => options.CommandTimeout = TimeSpan.FromMilliseconds(milliseconds));

            Assert.Equal("value", exception.ParamName);
            Assert.Null(options.CommandTimeout);
        }

        [Fact]
        public void Set_CommandTimeout_AcceptsOneSecond_AndNull()
        {
            var options = new SqlServerStorageOptions { CommandTimeout = TimeSpan.FromSeconds(1) };
            Assert.Equal(TimeSpan.FromSeconds(1), options.CommandTimeout);

            options.CommandTimeout = null;
            Assert.Null(options.CommandTimeout);
        }

        [Theory]
        [InlineData(0), InlineData(500), InlineData(-5000)]
        public void Set_CommandBatchMaxTimeout_ThrowsAnException_WhenValueIsShorterThanOneSecond(int milliseconds)
        {
            var options = new SqlServerStorageOptions();

            var exception = Assert.Throws<ArgumentOutOfRangeException>(
                () => options.CommandBatchMaxTimeout = TimeSpan.FromMilliseconds(milliseconds));

            Assert.Equal("value", exception.ParamName);
            Assert.Equal(TimeSpan.FromMinutes(5), options.CommandBatchMaxTimeout);
        }

        [Fact]
        public void Set_CommandBatchMaxTimeout_AcceptsOneSecond_AndNull()
        {
            var options = new SqlServerStorageOptions { CommandBatchMaxTimeout = TimeSpan.FromSeconds(1) };
            Assert.Equal(TimeSpan.FromSeconds(1), options.CommandBatchMaxTimeout);

            options.CommandBatchMaxTimeout = null;
            Assert.Null(options.CommandBatchMaxTimeout);
        }
    }
}
