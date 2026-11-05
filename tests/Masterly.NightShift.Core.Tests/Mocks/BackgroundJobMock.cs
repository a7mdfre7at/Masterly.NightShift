using System;
using Masterly.NightShift.Common;

namespace Masterly.NightShift.Core.Tests
{
    public class BackgroundJobMock
    {
        private readonly Lazy<BackgroundJob> _object;

        public BackgroundJobMock()
        {
            Id = "JobId";
            Job = Job.FromExpression(() => SomeMethod());
            CreatedAt = DateTime.UtcNow;

            _object = new Lazy<BackgroundJob>(
                () => ParametersSnapshot != null ? new BackgroundJob(Id, Job, CreatedAt, ParametersSnapshot) : new BackgroundJob(Id, Job, CreatedAt));
        }

        public string Id { get; set; }
        public Job Job { get; set; }
        public DateTime CreatedAt { get; set; }
        public System.Collections.Generic.IReadOnlyDictionary<string, string> ParametersSnapshot { get; set; }

        public BackgroundJob Object => _object.Value;

        public static void SomeMethod() { }
    }
}
