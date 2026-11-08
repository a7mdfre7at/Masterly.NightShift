using System;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Masterly.NightShift.Common;

namespace Masterly.NightShift.Dashboard
{
    /// <summary>
    /// <c>GET /recurring/export</c>: the <see cref="RecurringJobManifest"/> of every recurring job as JSON.
    /// <c>POST /recurring/import</c> with form fields <c>manifest</c> (the JSON) and <c>prune</c>
    /// (<c>true</c> to remove recurring jobs the manifest doesn't mention): applies it and answers
    /// <c>200 { added, updated, removed, errors: [{ id, message }] }</c>, <c>422 { error }</c> for a document
    /// that can't be read. Import accepts only the methods the application already uses as jobs, unless
    /// <see cref="DashboardOptions.AllowAnyMethodInManifestImport"/> is set.
    /// </summary>
    internal sealed class RecurringJobManifestDispatcher : IDashboardDispatcher
    {
        private readonly bool _import;

        public RecurringJobManifestDispatcher(bool import)
        {
            _import = import;
        }

        public Task Dispatch(DashboardContext context)
        {
            return _import ? ImportAsync(context) : ExportAsync(context);
        }

        private static Task ExportAsync(DashboardContext context)
        {
            string manifest;
            using (var connection = context.Storage.GetReadOnlyConnection())
            {
                // Read-only users don't see the values of [Redacted] parameters
                manifest = RecurringJobManifest.Export(connection, maskRedacted: context.IsReadOnly);
            }

            context.Response.StatusCode = 200;
            context.Response.ContentType = "application/json";
            return context.Response.WriteAsync(manifest);
        }

        private static async Task ImportAsync(DashboardContext context)
        {
            if (context.IsReadOnly)
            {
                context.Response.StatusCode = 401;
                return;
            }

            if (!"POST".Equals(context.Request.Method, StringComparison.OrdinalIgnoreCase))
            {
                context.Response.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
                return;
            }

            var manifest = (await context.Request.GetFormValuesAsync("manifest").ConfigureAwait(false))?.FirstOrDefault();
            var pruneValue = (await context.Request.GetFormValuesAsync("prune").ConfigureAwait(false))?.FirstOrDefault();
            var prune = pruneValue is "true" or "on" or "1";

            if (String.IsNullOrWhiteSpace(manifest))
            {
                AuditLog.Record(context, null, "no manifest", 422);
                await DashboardJson.WriteAsync(context, 422, new { error = "A manifest is required." }).ConfigureAwait(false);
                return;
            }

            try
            {
                RecurringJobManifestResult result;
                using (var connection = context.Storage.GetConnection())
                {
                    result = RecurringJobManifest.Apply(context.GetRecurringJobManager(), connection, manifest, prune, context.Options.AllowAnyMethodInManifestImport);
                }

                AuditLog.Record(context, result.Added.Concat(result.Updated).Concat(result.Removed),
                    $"{result.Added.Count} added, {result.Updated.Count} updated, {result.Removed.Count} removed, {result.Errors.Count} failed{(prune ? ", pruned" : String.Empty)}", 200);

                await DashboardJson.WriteAsync(context, 200, new
                {
                    added = result.Added,
                    updated = result.Updated,
                    removed = result.Removed,
                    errors = result.Errors.Select(static e => new { id = e.Id, message = e.Message }).ToArray(),
                }).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is FormatException || ex is ArgumentException || ex is NotSupportedException)
            {
                // The message stays out of the audit log: it can quote the manifest, job arguments included
                AuditLog.Record(context, null, $"rejected{(prune ? ", prune" : String.Empty)}", 422);
                await DashboardJson.WriteAsync(context, 422, new { error = ex.Message }).ConfigureAwait(false);
            }
        }
    }
}
