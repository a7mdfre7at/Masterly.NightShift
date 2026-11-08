using System;

namespace Masterly.NightShift.Dashboard.Pages
{
    partial class BatchDetailsPage
    {
        public BatchDetailsPage(string id)
        {
            Id = RouteValues.Decode(id);
        }

        public string Id { get; }
    }
}
