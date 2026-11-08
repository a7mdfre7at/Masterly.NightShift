# Tags and notes

Tags let you find jobs by what they are about ("every job for customer Contoso", "all billing jobs") instead of by ID or method. Notes let people leave findings on a job ("partner API down, retry after 15:00") and acknowledge a failure so the team knows someone is on it. Both are stored as job parameters, so they work with every storage.

## Quick example

```csharp
using Masterly.NightShift;

[Tag("billing")]
public class InvoiceJobs
{
    [Tag("customer:{0}")]
    [Tag("month:{1:yyyy-MM}")]
    public Task SendAsync(string customer, DateTime month) => Task.CompletedTask;

    public Task RemindAsync(string customer) => Task.CompletedTask;
}

jobs.Enqueue<InvoiceJobs>(x => x.SendAsync("Contoso", new DateTime(2026, 11, 1)));
// tagged "billing", "customer:contoso" and "month:2026-11"
```

The dashboard's Tags page lists the tags in name order with their job counts, a page at a time; open a tag to see its jobs, newest first. The job details page shows a job's tags, and the command palette finds them (`tag:contoso`).

## How it works

### Tags

`[Tag]` is a client filter. When a job is created, each `[Tag]` on the method or its class produces one tag:

- A fixed word (`"billing"`) is used as is.
- A template is formatted with the job's arguments, like `String.Format` with the invariant culture: `{0}` is the first argument, `{1:yyyy-MM}` the second with a format.
- The result is trimmed, runs of whitespace become one space, and it is lower-cased.
- When nothing is left (`[Tag("{0}")]` with a `null` or blank argument), the job gets no tag from that attribute.
- When the result is longer than 95 characters (`JobTags.MaxLength`), it is cut to its first 78 characters, followed by `~` and 16 hexadecimal digits of a hash of the whole result. The same value always gives the same tag, and different long values give different tags.

So an argument never makes job creation fail. The tags are stored in the job's `Tags` parameter (a JSON array, sorted), the tag name is added to the `tags` set, and the job ID is added to the `tags:{tag}` set, scored by creation time. Jobs created by recurring jobs are tagged the same way, every time they run.

The `Tags` parameter is stored with the job. The two sets are written just after the job is created; when that write fails (a deadlock, a timeout), the failure is logged as a warning and the job is created anyway, because failing then would make the caller create the job a second time. Such a job keeps its tags but is missing from the tag's list. To list it, call `JobTags.Remove` and then `JobTags.Add` for that tag (`Add` alone skips a tag the job already has).

The `Tags` parameter is written with the library's internal serializer options, so your serializer settings (a source-generated resolver that doesn't know `string[]`, for example) can't break tagging. Tags written with your settings before are still read. The `Notes` parameter is written with options of its own in the same way.

`JobTags.Add` tags a job that already exists, in the same way, except that it throws `ArgumentException` for an empty tag or one longer than 95 characters instead of skipping or shortening it. The limit keeps the set key `tags:{tag}` within the 100 characters SQL Server allows for keys.

`JobTags.Add` and `JobTags.Remove` read the `Tags` parameter, change it and write it back under a distributed lock on the job's tags (`job:{id}:tags-lock`), so two changes made at the same time don't lose each other's tags. `JobNotes.Add` does the same with `job:{id}:notes-lock`. These locks are separate from the job's state lock, so tagging a job while it changes state, or from a state filter, doesn't wait for it. When another change holds the lock for more than 15 seconds, the call throws `DistributedLockTimeoutException` and writes nothing; the dashboard shows the request as failed.

The Tags page reads the tag names (one query), applies its filter to them, then counts the jobs of the tags on the current page only. The command palette filters the names by the text you type, names starting with it first, and counts the 10 it shows. Both stay fast with thousands of tags.

Tags don't expire with their jobs. A tag keeps listing a job ID after the job expired. The tag's page hides such IDs and says how many it hid; its **Remove them from the tag** button (not shown on read-only dashboards) forgets the ones on that page, like `JobTags.Forget`. Counts include expired jobs until then.

### Notes

A note (`JobNote`) has a text, an author, a time and an `Acknowledges` flag. A job's notes are kept, oldest first, in its `Notes` job parameter as a JSON array, at most 50 per job (older ones are dropped). The job details page has a Notes section where people write notes and press **Acknowledge**; the author is the signed-in dashboard user. A failed job with an acknowledging note shows a badge on the Failed jobs page.

## API

### TagAttribute

| Member | Meaning |
|---|---|
| `[Tag(string template)]` | Applies to methods and classes; may be repeated; inherited by derived classes. Throws `ArgumentException` for an empty template |
| `Template` | The word or template |
| `GetTag(Job job)` | The tag the template gives for a job (`string?`): `null` when nothing is left, shortened when longer than `MaxLength`. Throws `FormatException` when the template refers to a missing argument |

### JobTags

| Member | Meaning |
|---|---|
| `Add(string jobId, params string[] tags)` | Tags an existing job in `JobStorage.Current` |
| `Add(IStorageConnection connection, string jobId, IEnumerable<string> tags)` | Tags an existing job. Tags it already has are skipped. Throws `DistributedLockTimeoutException` when another change to the job's tags holds the lock for more than 15 seconds |
| `Remove(IStorageConnection connection, string jobId, string tag)` | Removes one tag from a job. Takes the same lock as `Add` |
| `GetTags(IStorageConnection connection, string jobId)` | A job's tags |
| `GetAll(IStorageConnection connection)` | Every tag with its job count (`JobTagSummary`: `Tag`, `Count`), most used first. Runs one count query per tag |
| `GetNames(IStorageConnection connection)` | Every tag name, in ordinal order, without counting jobs: one query |
| `Count(IStorageConnection connection, string tag)` | How many job IDs a tag lists |
| `GetJobIds(IStorageConnection connection, string tag, int from, int count)` | Job IDs carrying a tag, newest first |
| `Forget(IStorageConnection connection, string tag, IEnumerable<string> jobIds)` | Removes job IDs from a tag's list (the jobs are untouched) |
| `Delete(IStorageConnection connection, string tag)` | Deletes a tag and its list. The jobs are untouched, but keep the tag in their `Tags` parameter |
| `Normalize(string tag)` | Trims, collapses whitespace, lower-cases and validates a tag. Throws `ArgumentException` when it is empty or longer than `MaxLength` |
| `GetKey(string tag)` | The storage set of a tag, `tags:{tag}` |
| `ParameterName` (`"Tags"`), `IndexKey` (`"tags"`), `MaxLength` (`95`) | Constants |

Every method normalizes the tags you pass, so `"Customer:Contoso"` and `"customer:contoso"` are the same tag. `Add` rejects tags longer than `MaxLength`; the methods that read or remove tags accept them and simply don't find them.

On a storage without the extended API (`JobStorageFeatures.ExtendedApi`), counts and job lists read the tag's whole set.

### JobNotes

| Member | Meaning |
|---|---|
| `Add(IStorageConnection connection, string jobId, string text, string? user = null, bool acknowledges = false)` | Adds a note and returns it. The text is trimmed and cut to 2000 characters. Empty text is allowed only when `acknowledges` is `true`. Throws `DistributedLockTimeoutException` when another note on the job holds the lock for more than 15 seconds |
| `Read(IStorageConnection connection, string jobId)` | A job's notes, oldest first |
| `Parse(string? serialized)` | Reads the value of the `Notes` parameter; invalid JSON gives an empty list |
| `GetAcknowledgement(IReadOnlyList<JobNote> notes)` | The latest acknowledging note, or `null` |
| `ParameterName` (`"Notes"`), `MaxNotes` (`50`), `MaxLength` (`2000`) | Constants |

### JobNote

| Property | Type | Meaning |
|---|---|---|
| `Text` | `string` | The note |
| `User` | `string?` | Who wrote it, when known |
| `Timestamp` | `long` | When, as Unix seconds |
| `WrittenAt` | `DateTime` | When, in UTC |
| `Acknowledges` | `bool` | Whether the note acknowledges the job's failure |

## More examples

### Tag a job after creating it

```csharp
using Masterly.NightShift.Storage;

string? jobId = jobs.Enqueue<InvoiceJobs>(x => x.RemindAsync("contoso"));
if (jobId != null)
{
    using IStorageConnection connection = storage.GetConnection();
    JobTags.Add(connection, jobId, new[] { "priority:high", "customer:contoso" });
}
```

`storage` is the `JobStorage` registered by `AddNightShift`. Without DI: `JobTags.Add(jobId, "priority:high");`.

### Find a customer's jobs

```csharp
app.MapGet("/customers/{customer}/jobs", (string customer, JobStorage storage) =>
{
    using IStorageConnection connection = storage.GetConnection();
    string tag = $"customer:{customer}";
    long total = JobTags.Count(connection, tag);
    IReadOnlyList<string> newest = JobTags.GetJobIds(connection, tag, from: 0, count: 50);
    return Results.Ok(new { total, newest });
});
```

Load each job with the monitoring API if you need its state, see [Managing jobs](managing-jobs.md#querying-jobs).

### Clean up tags

```csharp
using IStorageConnection connection = storage.GetConnection();

IReadOnlyList<string> tags = JobTags.GetTags(connection, jobId);
JobTags.Remove(connection, jobId, "priority:high");

foreach (string tag in JobTags.GetNames(connection).Where(name => name.StartsWith("month:", StringComparison.Ordinal)))
{
    Console.WriteLine($"{tag}: {JobTags.Count(connection, tag)}");
}

JobTags.Delete(connection, "month:2025-01");
```

### Notes from an incident bot

```csharp
using IStorageConnection connection = storage.GetConnection();

JobNotes.Add(connection, jobId, "Partner API down since 14:10, see INC-2291.", user: "incident-bot");
JobNotes.Add(connection, jobId, "Known issue, retry after the fix ships.", user: "ana", acknowledges: true);

IReadOnlyList<JobNote> notes = JobNotes.Read(connection, jobId);
JobNote? ack = JobNotes.GetAcknowledgement(notes);
if (ack != null)
{
    Console.WriteLine($"Acknowledged by {ack.User} at {ack.WrittenAt:u}: {ack.Text}");
}
```

## Things to know

- **A bad template fails job creation.** A template that refers to a missing argument (`{3}` on a method with two parameters) throws `FormatException` from the filter, and `Enqueue` throws `BackgroundJobClientException`. Argument values don't: an empty result adds no tag and a long one is shortened.
- **Tag values come from arguments.** Avoid templates over arguments that hold personal data; tags are shown on the dashboard. A `[Redacted]` argument is replaced with a hash of its value (16 hexadecimal digits), but a short or guessable value can be guessed back from its hash.
- **Many distinct tags cost space.** Each tag is a set in storage that lives until it is deleted. Prefer tags with a bounded number of values (customer, region, month) over one tag per job.
- **Notes need write access.** On a read-only dashboard the Notes section can't add notes.
- **Writing notes or tags isn't atomic with other writers.** Two writers adding to the same job at the same moment can lose one update; this is rare in practice, since notes are written by people.

## See also

- [Managing jobs](managing-jobs.md)
- [Dashboard pages](../dashboard/pages.md)
- [Behavior filters](../filters/behavior.md)
- [Audit log and activity](../monitoring/audit-log-and-activity.md)
