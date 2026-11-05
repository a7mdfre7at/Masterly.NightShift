using System;
using System.Collections.Generic;
using System.Threading;
using Masterly.NightShift.Profiling;
using Masterly.NightShift.States;
using Masterly.NightShift.Storage;
using Moq;

namespace Masterly.NightShift.Core.Tests
{
    public class StateChangeContextMock
    {
        private readonly Lazy<StateChangeContext> _context;

        public StateChangeContextMock()
        {
            Storage = new Mock<JobStorage>();
            Connection = new Mock<IStorageConnection>();
            BackgroundJobId = "JobId";
            NewState = new Mock<IState>();
            ExpectedStates = null;
            DisableFilters = false;
            CancellationToken = CancellationToken.None;

            _context = new Lazy<StateChangeContext>(
                () => new StateChangeContext(
                    Storage.Object,
                    Connection.Object,
                    Transaction?.Object,
                    BackgroundJobId,
                    NewState.Object,
                    ExpectedStates,
                    DisableFilters,
                    CompleteJob?.Object,
                    CancellationToken,
                    EmptyProfiler.Instance,
                    ServerId,
                    CustomData));
        }

        public Mock<JobStorage> Storage { get; set; }
        public Mock<IStorageConnection> Connection { get; set; }
        public Mock<JobStorageTransaction> Transaction { get; set; }
        public Mock<IFetchedJob> CompleteJob { get; set; }
        public string BackgroundJobId { get; set; }
        public Mock<IState> NewState { get; set; }
        public IEnumerable<string> ExpectedStates { get; set; }
        public bool DisableFilters { get; set; }
        public CancellationToken CancellationToken { get; set; }
        public string ServerId { get; set; }
        public IReadOnlyDictionary<string, object> CustomData { get; set; }
        
        public StateChangeContext Object => _context.Value;
    }
}
