using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Masterly.NightShift.Dashboard;

namespace Masterly.NightShift.Core.Tests.Stubs
{
    class DashboardResponseStub : DashboardResponse
    {
        private readonly StringBuilder _text = new StringBuilder();

        public override string ContentType { get; set; }
        public override int StatusCode { get; set; }
        public override Stream Body { get; }

        public string Text => _text.ToString();

        // Each WriteAsync call, to check what is sent as it's produced
        public List<string> Writes { get; } = new List<string>();

        public override void SetExpire(DateTimeOffset? value)
        {
            throw new NotImplementedException();
        }

        public override Task WriteAsync(string text)
        {
            _text.Append(text);
            Writes.Add(text);
            return Task.CompletedTask;
        }
    }
}
