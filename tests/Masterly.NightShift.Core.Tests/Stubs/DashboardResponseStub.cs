using System;
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

        public override void SetExpire(DateTimeOffset? value)
        {
            throw new NotImplementedException();
        }

        public override Task WriteAsync(string text)
        {
            _text.Append(text);
            return Task.CompletedTask;
        }
    }
}
