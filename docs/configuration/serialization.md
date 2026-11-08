# Serialization

Everything Masterly.NightShift stores about a job (its arguments, parameters, return value and state data) is written as JSON with System.Text.Json. This page explains what is serialized with which options, how to change the options for your own data, what the reader accepts from data written by older versions, and how to write argument types that survive the round trip.

## Quick example

```csharp
using Masterly.NightShift;

public record InvoiceRequest(int CustomerId, decimal Amount, string Currency);

public class InvoiceService
{
    public void Send(InvoiceRequest request) { /* ... */ }
}

// In a controller or service that has IBackgroundJobClient injected
jobs.Enqueue<InvoiceService>(x => x.Send(new InvoiceRequest(42, 99.50m, "EUR")));
```

The job is stored as its type and method name plus one JSON string per argument:

```
Type:       InvoiceService, MyApp
Method:     Send
Arguments:  ["{\"CustomerId\":42,\"Amount\":99.5,\"Currency\":\"EUR\"}"]
```

When a server picks the job up, it reads the JSON back into an `InvoiceRequest` and calls `Send` with it. Nothing else is needed for a type like this one: public properties, a public constructor whose parameters match them.

## How it works

### What is serialized

| Data | Written with | Notes |
|---|---|---|
| Job type and method | The type serializer (`UseTypeSerializer`, `UseSimpleAssemblyNameTypeSerializer`) | Type name, method name and the method's parameter types. These are names, not JSON options. |
| Arguments | User options | Each argument is serialized on its own, then the strings are stored as a JSON array. An argument whose parameter is declared as an interface or abstract class gets a leading `"$type"` naming its actual type. `CancellationToken` arguments are stored as `null`. The values of `CancellationToken`, `IJobCancellationToken`, `PerformContext` and `IJobConsole` parameters are supplied by the server when the job runs. |
| Job parameters | User options | Values set with `SetJobParameter` by filters and by you: captured culture, retry count, notes, trace context. |
| Return value | User options | Stored in the Succeeded state as `Result`. A value that can't be serialized is stored as the text `Can not serialize the return value`. |
| State data | Plain strings per field; nested states with `TypedInternal` | Dates, queue names, reasons, exception details. A state stored inside another state (the next state of a continuation) is written with a `"$type"`. |
| Internal records | Internal options | The job payload, exception info, continuation lists and similar library data. Some of them are written with the internal options at every compatibility level, so your options can't rename or break them: the job payload, the `Tags`, `BatchId` and `BatchContinuationOf` job parameters and the fan-in children lists. Any other library record your options can't write is written with the internal options too (see [below](#three-sets-of-options)). Values written with your options before are still read. |

The JSON the dashboard's own endpoints return is a separate thing and isn't affected by any of these settings.

### Three sets of options

`SerializationHelper` (namespace `Masterly.NightShift.Common`) chooses the options by a `SerializationOption`:

| `SerializationOption` | Used for | Can you change it? |
|---|---|---|
| `Internal` | The library's own data | No. Isolated options, with `"$type"` kept for values declared as `object` and `null` members left out. |
| `TypedInternal` | States, which must be read back as `IState` | No. Same options; a state is written with a leading `"$type"`. |
| `User` | Arguments, job parameters, return values | Yes, through `UseSerializerSettings` or `UseRecommendedSerializerSettings`. |

Below compatibility level `Version_170`, `Internal` data is also written with the user options, the way older versions did, except for the records listed in the table above. See [Compatibility levels](compatibility-levels.md).

When your options can't write a piece of the library's own data, it is written with the internal options instead, at every level. That happens with a resolver that only knows your own types, such as a source-generated `JsonSerializerContext`: System.Text.Json throws `NotSupportedException` for the `string[]` of a job's arguments, the `Dictionary<string, string>` of SQL Server's state data, the continuation list or a job console entry. It also covers the library's own types stored as user data, such as the `ExceptionInfo` of a deleted job. Your own data isn't rerouted: an argument or return value whose type your resolver doesn't know still throws `NotSupportedException`.

When internal data can't be read with the internal options, the reader tries the user options before giving up, because older data was written with them. If both fail, you see the first error.

### The default options

Both the internal options and the default user options start from the same base, built to read everything the earlier Newtonsoft.Json-based versions wrote:

| Setting | Value | Why |
|---|---|---|
| `PropertyNameCaseInsensitive` | `true` | Older data may use `customerId` or `CustomerId`. |
| `NumberHandling` | `AllowReadingFromString` | Older data may have `"42"` where a number is expected. |
| `IncludeFields` | `true` | Public fields were serialized before. |
| `AllowTrailingCommas`, `ReadCommentHandling` | `true`, `Skip` | Hand-edited or older JSON still reads. |
| `Encoder` | `JavaScriptEncoder.UnsafeRelaxedJsonEscaping` | Non-ASCII text stays readable in the storage. |
| `MaxDepth` | 128 | |
| `NumberHandling` | Also `AllowNamedFloatingPointLiterals` | `double.NaN` and the infinities are written as `"NaN"`, `"Infinity"` and `"-Infinity"`, as older versions did, instead of failing. |
| `[DefaultValue]` members | Always written; set to their default when missing from the JSON | Older versions left such members out when they equaled their default (`DefaultValueHandling.IgnoreAndPopulate`); reading puts the value back. See [below](#defaultvalue-members). |

And these converters, which add what System.Text.Json doesn't do by itself:

| Type | Written as | Read from |
|---|---|---|
| Enums | Numbers | Numbers or names, ignoring case |
| `System.Type` | Its name, through the configured type serializer | A name, through the configured type resolver |
| Values declared as `object` | Their JSON; with type names kept, objects get a leading `"$type"` | `bool`, `long`, `double`, `DateTime` (ISO 8601 text), `string`, or a `JsonElement` for objects and arrays; with type names kept and a `"$type"` present, the named type |
| Values declared as an interface or abstract class | Every member of the actual type, after a leading `"$type"` | The type named in `"$type"`, which must implement or derive from the declared type; `"$type"` is required |
| `IState` | An object with a leading `"$type"` | The same; `"$type"` is required |
| Arrays (except `byte[]`) | Plain JSON arrays | Plain arrays or `{"$type": ..., "$values": [...]}`. Not used when your options set a `ReferenceHandler` (see below) |
| Other collections and dictionaries (the argument itself, its members and items) | As System.Text.Json writes them | As System.Text.Json reads them; also a collection wrapped as `{"$type": ..., "$values": [...]}` and a dictionary whose first entry is a `"$type"` naming a dictionary type that fits the declared one, which Hangfire wrote with type names on. The values are read as the declared type, and the stored type name is ignored. A leading `"$type"` entry that doesn't name such a type (`{"$type": "user-value"}`) is an ordinary key and is kept |
| `CultureInfo` | Its name, such as `"en-US"` | A name |
| `XElement` | XML text | XML text, or the JSON form older data used (`{"name": {"@attr": "1", "#text": "text"}}`) |

The default user options differ from the internal ones in two ways: they don't write `"$type"` for `object` values, and they write `null` members.

### What the reader adds for data Newtonsoft.Json could read

System.Text.Json alone loses some data that Newtonsoft.Json read back. These additions change only reading, so they apply to every set of options, also to the options you pass to `UseSerializerSettings`:

- **Structs set only by their constructor.** A struct without `[JsonConstructor]` that has exactly one public constructor, whose parameters each match a get-only property by name (ignoring case) and type, is created through that constructor. System.Text.Json would use the implicit parameterless constructor and leave every such property at its default. A property missing from the JSON gets the parameter's default value, and properties with a setter are set after the constructor ran, as are the items of a collection property without a setter that the constructor creates.
- **Collection properties without a setter.** The stored items are added to the collection the property already holds (a dictionary sets its keys), as Newtonsoft.Json did. Nothing happens when the property is `null` or holds a read-only or fixed-size collection, such as an array. Properties that System.Text.Json is told to populate itself (`[JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]`, or `PreferredObjectCreationHandling = Populate` in your options) are left to it.
- **Collections and dictionaries stored with their type name**, as in the converter table above. Members that already have a converter, on the member or in your options for that type, are left alone.

None of these change how data is written: they are only in the copies of the options used to read.

With a `ReferenceHandler` in your options (`ReferenceHandler.Preserve` or `ReferenceHandler.IgnoreCycles`), collections, dictionaries and arrays are left entirely to System.Text.Json, so it keeps one reference scope for the whole value: cycles and shared references through `List<T>` or array members are written and read exactly as `JsonSerializer` does, and Preserve's own `{"$id": ..., "$values": [...]}` and `{"$ref": ...}` are read back. Collections that Hangfire wrapped as `{"$type": ..., "$values": [...]}` aren't unwrapped under those options.

### Writing follows your options, reading is always lenient

User data is written exactly as the configured user options say. It is read with a copy of them that also has the lenient settings of the first table: property names in any case, numbers from strings, `"NaN"` and `"Infinity"`, public fields, trailing commas and comments. So jobs already in storage keep binding their arguments when you change the options, for example from PascalCase to camelCase names. Enums are read as names or numbers unless one of your converters handles them.

### `[DefaultValue]` members

An argument or return value is stored with every member, also one equal to its `[DefaultValue]`, so the job gets exactly the values it was created with. When the JSON has no value for a member with `[DefaultValue]`, the default, the internal and the recommended options set it to that value. That is how data written by older versions with `DefaultValueHandling.IgnoreAndPopulate` (Hangfire's recommended settings) reads back as it was written:

```csharp
using System.ComponentModel;

public class ReportRequest
{
    [DefaultValue(30)]
    public int Days { get; set; }

    [DefaultValue(0)]
    public int Limit { get; set; } = 100;
}

// new ReportRequest { Days = 30, Limit = 0 } is stored as {"Days":30,"Limit":0}.
// {} (written by Hangfire's recommended settings) is read as Days = 30, Limit = 0.
```

- A member that a constructor parameter sets keeps what the constructor does: give the parameter a default value (`int days = 30`) instead.
- A member without `[DefaultValue]` that is missing from the JSON keeps the value its initializer or constructor gives it. Hangfire's recommended settings also left out members equal to `0`, `false` or `null`; such a member comes back right only when its initializer gives that same value.
- With your own options (`UseSerializerSettings`), `[DefaultValue]` does nothing, as System.Text.Json does by default. Add a `JsonTypeInfo` modifier to your resolver if you need it.
- The library's own types keep their compact format: the `Order` of a filter attribute, for example, is left out when it equals its `[DefaultValue]`.

## Options / API

### Configuration methods

| Method | Parameters | Default | Meaning |
|---|---|---|---|
| `UseSerializerSettings(settings)` | `JsonSerializerOptions?` | Default user options | Your options for user data. A copy is taken, so your instance isn't changed, and the converters above are appended after yours, so your converters win for the same type. Data is written as your options say and read with the lenient settings added (see [above](#writing-follows-your-options-reading-is-always-lenient)). `null` restores the defaults. |
| `UseRecommendedSerializerSettings()` | | | User data is written with a copy of the internal options: `"$type"` for `object` values, `null` members left out, lenient reading. |
| `UseRecommendedSerializerSettings(configure)` | `Action<JsonSerializerOptions>?` | | Same, and `configure` can change the copy before it is used. Converters it adds come before the built-in ones, so they win for the same type. |
| `UseTypeSerializer`, `UseTypeResolver`, `UseSimpleAssemblyNameTypeSerializer`, `UseIgnoredAssemblyVersionTypeResolver` | | Assembly-qualified names | How type names are written and read. See [Global configuration](global-configuration.md#serialization-and-type-names). |

### `SerializationHelper`

Use it when a filter or extension stores its own values next to the job, so they are written the same way the library writes its data.

| Method | Returns | Meaning |
|---|---|---|
| `Serialize<T>(value)` | `string` | Serializes with `Internal`. Returns `null` for `null`. |
| `Serialize<T>(value, option)` | `string` | Serializes with the given option. |
| `Serialize(value, type, option)` | `string` | Same, with the declared type given explicitly. Pass `typeof(object)` to get `"$type"` under options that keep type names. An interface or abstract class always gets `"$type"`. |
| `Deserialize<T>(value)` | `T` | Reads with `Internal`. `null`, empty or white-space text gives `default`. |
| `Deserialize<T>(value, option)` | `T` | Reads with the given option. |
| `Deserialize(value, type)` | `object` | Reads with `Internal`. |
| `Deserialize(value, type, option)` | `object` | Reads with the given option. When `type` is an interface or abstract class, reads the type named in `"$type"`. Throws `ArgumentNullException` when `type` is `null`. |

## Rules for argument types

Arguments follow System.Text.Json rules, not Newtonsoft.Json rules:

- **Properties need a public setter or `[JsonInclude]`.** A property with a private setter and no `[JsonInclude]` is written but not read back: the job runs with the default value, and nothing fails. A collection property without a setter is the exception: its items are added to the collection it already holds (see [above](#what-the-reader-adds-for-data-newtonsoftjson-could-read)).
- **Constructors:** a public parameterless constructor, a single public constructor whose parameter names match the properties, or the constructor marked `[JsonConstructor]`. Records work as they are. A struct always has a parameterless constructor, which System.Text.Json uses; the reader uses the struct's own constructor instead when it is the only public one and every parameter matches a get-only property. A struct with several public constructors and get-only properties needs `[JsonConstructor]`, or those properties come back with their default values.
- **Public fields are included.**
- **Use `[JsonIgnore]`** for computed properties you don't want stored, and `[JsonPropertyName]` to rename a property. Newtonsoft attributes such as `[JsonProperty]` are ignored, and so is `JsonConvert.DefaultSettings`.
- **Interface and abstract parameters keep the actual type.** An argument declared as an interface or abstract class is written with a leading `"$type"` naming its actual type, with any options, and the job receives an instance of that type. With `[JsonPolymorphic]` or `[JsonDerivedType]` on the declared type, System.Text.Json's own type discriminator is used instead.
- **Only the argument itself gets `"$type"`.** A property, array element or collection item declared as an interface or abstract class is written without its type and can't be read back. Declare concrete types there.
- **A parameter declared as a concrete base class** is read back as that class. The argument is written with every member of its actual type, but the properties of a derived class are lost when the job runs. Declare the type the job needs.
- **Parameters declared as `object`** come back as a primitive or a `JsonElement`, not as the type you passed. Under `UseRecommendedSerializerSettings`, a value written as a JSON object (a class, a struct, a dictionary) keeps a `"$type"` and comes back as its original type. Everything else is read as this table says, with any options:

  | Value passed | Comes back as |
  |---|---|
  | A whole number (`int`, `long`, ...), an enum, a `double` or `decimal` without a fraction (`2.0`) | `long` |
  | A `double`, `float` or `decimal` with a fraction | `double` |
  | `bool` | `bool` |
  | `string`, `char`, `Guid`, `TimeSpan` | `string`, except text in ISO 8601 date form (`"2024-01-01"`), which comes back as `DateTime` |
  | `DateTime` | `DateTime` from `Version_170`; below it, the round-trip text as a `string`, because the argument is stored without JSON quotes, as Hangfire did |
  | A list, an array or another collection | `JsonElement` |
  | A class, a struct or a dictionary | `JsonElement`; under `UseRecommendedSerializerSettings`, its original type |
- **Enums are stored as numbers.** Renaming a member is safe; changing its value or reordering members without explicit values changes what queued jobs mean. Add `JsonStringEnumConverter` to your options to store names instead; both forms are always readable.
- **Keep arguments small.** Pass an ID and load the data in the job when you can. Every argument is stored with the job and shown in the dashboard.

## Reading data written by older versions

Jobs stored before the move to System.Text.Json, or by Hangfire, stay readable:

- property names in either case, quoted numbers and enum names, which Newtonsoft settings such as camelCase naming and `StringEnumConverter` produced;
- states, `object` values and arguments declared as an interface or abstract class with a `"$type"` property, which `TypeNameHandling` produced;
- arrays and other collections wrapped as `{"$type": ..., "$values": [...]}`, and dictionaries with a leading `"$type"` naming their type, which Hangfire wrote with type names on when the actual type differed from the declared one (an `IReadOnlyList<T>` holding a `List<T>`, an `IEnumerable<int>` holding an `int[]`), also nested in objects and collections;
- structs that Newtonsoft.Json created through their constructor, and collection properties without a setter that it filled (see [above](#what-the-reader-adds-for-data-newtonsoftjson-could-read));
- XML elements in Newtonsoft's JSON form;
- date arguments in the older formats (`2019-03-05T13:20:04.5932150Z` without JSON quotes, or `MM/dd/yyyy HH:mm:ss.ffff`);
- type names of the earlier library: `Hangfire.*` types in the `Hangfire.Core`, `Hangfire.SqlServer`, `Hangfire.NetCore` and `Hangfire.AspNetCore` assemblies resolve to the matching `Masterly.NightShift.*` types, and `System.Private.CoreLib` and `mscorlib` are treated as the same assembly.

This works with the default options, the recommended ones and your own, because user data is always read leniently.

## How `"$type"` is handled

- **States** are always written with `"$type"` first, and reading a state without one throws `JsonException`.
- **Values declared as `object`** get `"$type"` under the internal and recommended options only. A primitive (number, string, boolean) never gets one.
- **Values declared as an interface or abstract class** always get `"$type"`, with any options, because they can't be read back without it. A value that isn't written as a JSON object (a collection, or a type whose converter writes a string) doesn't get one. Reading one without `"$type"` throws `JsonException`, and so does a `"$type"` naming a type that doesn't implement or derive from the declared type.
- **Type names in `"$type"`** come from the configured type serializer, with `Version`, `Culture` and `PublicKeyToken` removed, for example `"MyApp.Payload, MyApp"`.
- **When the declared type is concrete**, a `"$type"` in the data is ignored and the value is read as the declared type.
- **In a collection or dictionary** that Hangfire stored with its type name, the `"$type"` is ignored and the values are read as the declared type, also when that is an interface such as `IReadOnlyList<T>` (System.Text.Json creates its usual type for it, here `List<T>`). Stored data can't make a server create the collection type it names. A dictionary's first `"$type"` entry counts as a type name only when the type resolver resolves it to a dictionary type assignable to the declared one, or of the same generic type; any other value, such as a key your own code added, stays in the dictionary.
- **When reading**, `"$type"` is resolved through the configured type resolver. Anyone who can write to your job storage can therefore make a server load a type by name. Protect the storage the way you protect the application itself.

## More examples

### Recommended settings in ASP.NET Core

```csharp
using Masterly.NightShift;

services.AddNightShift(config => config
    .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
    .UseRecommendedSerializerSettings()
    .UseSqlServerStorage("Server=.;Database=NightShift;Integrated Security=True;TrustServerCertificate=True;"));
```

The same call works on `GlobalConfiguration.Configuration` in apps without DI.

### camelCase names and a custom converter

Start from the recommended options, then change what you need. Converters you add win over the built-in ones:

```csharp
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Masterly.NightShift;

GlobalConfiguration.Configuration.UseRecommendedSerializerSettings(options =>
{
    options.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.Converters.Add(new MoneyJsonConverter());
});

public readonly struct Money
{
    public Money(decimal amount, string currency)
    {
        Amount = amount;
        Currency = currency;
    }

    public decimal Amount { get; }
    public string Currency { get; }

    public override string ToString() => $"{Amount.ToString(CultureInfo.InvariantCulture)} {Currency}";

    public static Money Parse(string text)
    {
        var parts = text.Split(' ');
        return new Money(decimal.Parse(parts[0], CultureInfo.InvariantCulture), parts[1]);
    }
}

public sealed class MoneyJsonConverter : JsonConverter<Money>
{
    public override Money Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return Money.Parse(reader.GetString()!);
    }

    public override void Write(Utf8JsonWriter writer, Money value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.ToString());
    }
}
```

Jobs already queued with PascalCase names still read, because user data is always read case-insensitively.

### Your own options

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;
using Masterly.NightShift;

var options = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    Converters = { new JsonStringEnumConverter() }
};

GlobalConfiguration.Configuration.UseSerializerSettings(options);

// Back to the defaults
GlobalConfiguration.Configuration.UseSerializerSettings(null);
```

New arguments are written with camelCase names, and enums as names, because your converter comes before the built-in one. Arguments already stored with PascalCase names or quoted numbers still read: reading is lenient whatever your options say.

### An argument declared as an interface

```csharp
using Masterly.NightShift;

public interface INotification
{
    string Recipient { get; }
}

public record EmailNotification(string Recipient, string Subject) : INotification;

public record SmsNotification(string Recipient, string Text) : INotification;

public class NotificationService
{
    public void Send(INotification notification) { /* ... */ }
}

// In a controller or service that has IBackgroundJobClient injected
jobs.Enqueue<NotificationService>(x => x.Send(new EmailNotification("ana@example.com", "Welcome")));
```

The argument is stored with its actual type, and `Send` receives an `EmailNotification`:

```
Arguments:  ["{\"$type\":\"EmailNotification, MyApp\",\"Recipient\":\"ana@example.com\",\"Subject\":\"Welcome\"}"]
```

### Making a type serializable

A class with private setters needs `[JsonInclude]` on them:

```csharp
using System.Text.Json.Serialization;

public class Shipment
{
    public Shipment(string trackingNumber)
    {
        TrackingNumber = trackingNumber;
    }

    [JsonInclude]
    public string TrackingNumber { get; private set; }

    [JsonInclude]
    public DateTimeOffset? ShippedAt { get; private set; }

    public void MarkShipped(DateTimeOffset at) => ShippedAt = at;
}
```

A class with several public constructors needs `[JsonConstructor]` on the one to use:

```csharp
using System.Text.Json.Serialization;

public class Customer
{
    public Customer(int id) : this(id, null) { }

    [JsonConstructor]
    public Customer(int id, string? email)
    {
        Id = id;
        Email = email;
    }

    public int Id { get; }
    public string? Email { get; }

    [JsonIgnore]
    public bool HasEmail => Email != null;
}
```

A plain class with public setters, or a record, needs nothing:

```csharp
public class OrderMessage
{
    public int OrderId { get; set; }
    public List<string> Lines { get; set; } = new List<string>();
}

public record InvoiceRequest(int CustomerId, decimal Amount, string Currency);
```

### Reading an argument declared as `object`

Without the recommended settings, an object argument comes back as a primitive or a `JsonElement`:

```csharp
using System.Text.Json;

public class ArchiveService
{
    public void Archive(object payload)
    {
        if (payload is JsonElement element)
        {
            var id = element.GetProperty("Id").GetInt32();
        }
        else if (payload is long number)
        {
            // Whole numbers come back as long
        }
    }
}
```

### Storing your own data the same way

```csharp
using Masterly.NightShift.Common;

string json = SerializationHelper.Serialize(new InvoiceRequest(42, 99.50m, "EUR"), SerializationOption.User);
var request = SerializationHelper.Deserialize<InvoiceRequest>(json, SerializationOption.User);
```

### A job type that moved to another assembly

Map old type names to new ones with a type resolver:

```csharp
using Masterly.NightShift;
using Masterly.NightShift.Common;

GlobalConfiguration.Configuration.UseTypeResolver(typeName =>
    TypeHelper.DefaultTypeResolver(typeName.Replace("OldCompany.Billing", "NewCompany.Billing", StringComparison.Ordinal)));
```

## Things to know

- **Every client and server must use the same settings.** A server that reads with different options than the client wrote with can bind arguments wrongly without an error, for example when a custom converter writes a format the other side doesn't know.
- **The recommended settings leave `null` members out** of arguments. A property whose constructor or initializer sets a non-null default keeps that default when the stored value was `null`.
- **Hangfire servers may not run jobs with interface or abstract arguments.** The `"$type"` is written at every compatibility level, and a Hangfire server reads it only with `UseRecommendedSerializerSettings()`. During a migration, create such jobs once every server runs Masterly.NightShift.
- **Changing an argument type while jobs are queued** (renaming or removing properties, changing types) is a breaking change for those jobs. Add new properties as optional and keep old ones until the queue drains.
- **`UseRecommendedSerializerSettings` versus `UseSerializerSettings`:** call only one of them. The last call wins.

## See also

- [Job arguments](../jobs/job-arguments.md)
- [Global configuration](global-configuration.md)
- [Compatibility levels](compatibility-levels.md)
- [Migrating from Hangfire](../migrating-from-hangfire.md)
- [Source: SerializationHelper.cs](../../src/Masterly.NightShift.Core/Common/SerializationHelper.cs), [JsonConverters.cs](../../src/Masterly.NightShift.Core/Common/JsonConverters.cs)
