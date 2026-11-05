# Contributing

Bug reports, ideas and pull requests are welcome.

## Reporting a problem

Open an [issue](https://github.com/a7mdfre7at/Masterly.NightShift/issues/new/choose) and include the package versions, the target framework, the storage you use and the steps that reproduce it. For a security problem, please follow [SECURITY.md](SECURITY.md) instead of opening a public issue.

## Building and testing

You need the .NET 10 SDK (`global.json`).

```
dotnet build Masterly.NightShift.slnx -c Release
dotnet test tests/Masterly.NightShift.Core.Tests -f net10.0
dotnet test tests/Masterly.NightShift.AspNetCore.Tests -f net10.0
dotnet test tests/Masterly.NightShift.SqlServer.Tests -f net10.0
```

The SQL Server tests need a reachable SQL Server. Set `NightShift_SqlServer_ConnectionStringTemplate` to a connection string with a `{0}` placeholder for the database name, for example `Server=(localdb)\MSSQLLocalDB;Database={0};Integrated Security=True;TrustServerCertificate=True;`.

Warnings are errors in `src/`, and the .NET analyzers run in `Recommended` mode, so a clean build matters.

## Pull requests

- Keep a change focused, and match the style of the code around it. `CLAUDE.md` describes the architecture and conventions.
- Add or update tests for what you change. Run the Core and AspNetCore test projects on both `net8.0` and `net10.0` before you open the pull request.
- Anything shared must still compile for `netstandard2.0`. Gate newer APIs with `#if NET8_0_OR_GREATER`.
- Don't add a member to a public storage interface: it breaks third-party storages. Extend through `JobStorage.HasFeature(...)` and the base classes.
- List behaviour changes that users will notice in `CHANGELOG.md`.

## License of contributions

New files and your changes to files without a license header are accepted under the MIT license in [LICENSE.txt](LICENSE.txt). Some files carry a header naming another license; changes to those stay under that license (see [NOTICE.md](NOTICE.md)), and the header must not be removed.
