# Plan: replace RazorGenerator for dashboard pages

> **Status: done.** Option A is implemented in `src/Masterly.NightShift.Dashboard.Generator`. All 30 captured dashboard pages (every list page, a details page for every job state, and pagination) render byte for byte the same as before, apart from values that change on every request. Implementing it found three behavior differences in modern Razor, all now handled in the generator:
>
> - Attributes that contain C# are written through `BeginWriteAttribute`/`WriteAttributeValue`/`EndWriteAttribute`. `RazorPage` implements these as plain text, without ASP.NET Core's conditional-attribute rules.
> - A line that holds only a Razor comment is removed entirely, line break included. RazorGenerator kept the line's whitespace and break, so the generator strips only the comment.
> - The output follows the checkout's line endings. The generator normalizes to `\r\n`, as RazorGenerator produced, so builds on Linux render the same HTML.

## Why

The 23 dashboard pages (`src/Masterly.NightShift.Core/Dashboard/Pages/*.cshtml`) are compiled to C# by RazorGenerator.MsBuild 2.5.0. That's its last release, and the tool has these limits:

- It's a .NET Framework MSBuild task. It only runs on Windows with the .NET Framework MSBuild (`Framework64\v4.0.30319\MSBuild.exe`). The .NET 10 SDK's own `MSBuild.exe` can't load it.
- It isn't part of `dotnet build`. It's restored into `packages/` by `build.bat`, and when it's missing the build only warns, so the checked-in `.cshtml.cs` files can silently go stale.
- Linux CI and contributors without Windows can't change a page.

## What the generated code needs

Very little. The generated classes derive from Masterly.NightShift's own `Dashboard/RazorPage.cs` and only call `Write(...)` (481 calls), `WriteLiteral(...)` (916 calls) and `RenderBody()` (1 call), from an `Execute()` override. Each page's first line sets the class visibility (22 internal, **1 public**) and naming (`GeneratePrettyNames`, `TrimLeadingUnderscores`). Class names and visibility are part of the public API, so package validation will catch any change.

## Options

| Option | Result | Verdict |
|---|---|---|
| **A. Roslyn source generator using `Microsoft.AspNetCore.Razor.Language`** | `.cshtml` files stay the source of truth. C# is generated during every `dotnet build`, on every OS, and no generated files are checked in. | **Recommended** |
| B. Stop generating, and edit the checked-in `.cshtml.cs` as source | Simplest, but every markup change means editing hundreds of `WriteLiteral` calls by hand. | No |
| C. Rewrite pages as C# builders or another template engine | Large rewrite of every page, with a high risk of visual regressions. | No |

## Option A in detail

1. **Generator project.** Add `src/Masterly.NightShift.Dashboard.Generator` (netstandard2.0, an `IIncrementalGenerator`). `Masterly.NightShift.Core` references it with `OutputItemType="Analyzer" ReferenceOutputAssembly="false"`, so it's a build-time dependency only and is never shipped.
2. **Razor engine.** Use `Microsoft.AspNetCore.Razor.Language` 6.0.x, the last version published as a standalone netstandard2.0 package. Configure its `RazorProjectEngine` to:
   - generate classes in `Masterly.NightShift.Dashboard.Pages`, deriving from `Masterly.NightShift.Dashboard.RazorPage`;
   - emit `public override void Execute()` instead of the default `async Task ExecuteAsync()`, through a small document classifier pass;
   - honor each page's `@* Generator: ... *@` header for visibility and naming.
3. **Pages as inputs.** Pass the pages as `AdditionalFiles`, then delete the checked-in `*.cshtml.cs` files, the `GenerateRazorClasses` target, `Razor.build`, and RazorGenerator from `.nuget/packages.config`.
4. **Prove the output is identical.** Before switching, render every page against a fixed data set and save the HTML as golden files. After switching, render again and require byte-for-byte equality. Package validation confirms the page classes' public API didn't change.

## Risks

- The generated methods must keep exactly the same `Write` and `WriteLiteral` order and the same HTML encoding. The golden-file comparison catches any difference.
- `Microsoft.AspNetCore.Razor.Language` 6.0 is no longer updated. As a build-time-only dependency it's never shipped, and the source generator can be pinned and tested.

## Effort

About 1–2 days: the generator with its classifier pass, the golden-file harness, and removing the old pieces.
