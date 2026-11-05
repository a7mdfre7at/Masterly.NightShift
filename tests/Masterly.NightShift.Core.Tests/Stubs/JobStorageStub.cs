using System;
using Masterly.NightShift.Storage;

namespace Masterly.NightShift.Core.Tests.Stubs
{
    class JobStorageStub : JobStorage
    {
        public override IMonitoringApi GetMonitoringApi()
        {
            throw new NotImplementedException();
        }

        public override IStorageConnection GetConnection()
        {
            throw new NotImplementedException();
        }
    }
}
