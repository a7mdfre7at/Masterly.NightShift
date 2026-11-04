Include "packages\Hangfire.Build.0.5.0\tools\psake-common.ps1"

Task Default -Depends Pack

Task Merge -Depends Compile -Description "Run ILRepack /internalize to merge required assemblies." {
    Repack-Assembly @("Hangfire.Core", "netstandard2.0") @("Cronos")
    Repack-Assembly @("Hangfire.Core", "net8.0") @("Cronos")
    Repack-Assembly @("Hangfire.Core", "net10.0") @("Cronos")
    Repack-Assembly @("Hangfire.SqlServer", "netstandard2.0") @("Dapper")
    Repack-Assembly @("Hangfire.SqlServer", "net8.0") @("Dapper")
    Repack-Assembly @("Hangfire.SqlServer", "net10.0") @("Dapper")
}

Task Test -Depends Merge -Description "Run unit and integration tests against merged assemblies." {
    # Dependencies shouldn't be re-built, because we need to run tests against merged assemblies to test
    # the same assemblies that are distributed to users. Since the `dotnet test` command doesn't support
    # the `--no-dependencies` command directly, we need to re-build tests themselves first.
    Exec { ls "tests\**\*.csproj" | % { dotnet build -c Release --no-restore --no-dependencies $_.FullName } }

    # We are running unit test project one by one, because pipelined version like the line above does not
    # support halting the whole execution pipeline when "dotnet test" command fails due to a failed test,
    # silently allowing build process to continue its execution even with failed tests.
    Exec { dotnet test -c Release --no-build "tests\Hangfire.Core.Tests" }
    Exec { dotnet test -c Release --no-build "tests\Hangfire.SqlServer.Tests" }
}

Task Collect -Depends Test -Description "Copy all artifacts to the build folder." {
    Collect-Assembly "Hangfire.Core" "netstandard2.0"
    Collect-Assembly "Hangfire.SqlServer" "netstandard2.0"
    Collect-Assembly "Hangfire.NetCore" "netstandard2.0"
    Collect-Assembly "Hangfire.AspNetCore" "netstandard2.0"

    Collect-Assembly "Hangfire.Core" "net8.0"
    Collect-Assembly "Hangfire.SqlServer" "net8.0"
    Collect-Assembly "Hangfire.NetCore" "net8.0"
    Collect-Assembly "Hangfire.AspNetCore" "net8.0"

    Collect-Assembly "Hangfire.Core" "net10.0"
    Collect-Assembly "Hangfire.SqlServer" "net10.0"
    Collect-Assembly "Hangfire.NetCore" "net10.0"
    Collect-Assembly "Hangfire.AspNetCore" "net10.0"

    Collect-Tool "src\Hangfire.SqlServer\DefaultInstall.sql"

    Collect-Localizations "Hangfire.Core" "netstandard2.0"
    Collect-Localizations "Hangfire.Core" "net8.0"
    Collect-Localizations "Hangfire.Core" "net10.0"

    Collect-File "README.md"
    Collect-File "LICENSE.md"
    Collect-File "NOTICES"
    Collect-File "COPYING.LESSER"
    Collect-File "COPYING"
    Collect-File "LICENSE_STANDARD"
    Collect-File "LICENSE_ROYALTYFREE"
}

Task Pack -Depends Collect -Description "Create NuGet packages and archive files." {
    $version = Get-PackageVersion

    Create-Package "Hangfire" $version
    Create-Package "Hangfire.Core" $version
    Create-Package "Hangfire.SqlServer" $version
    Create-Package "Hangfire.AspNetCore" $version
    Create-Package "Hangfire.NetCore" $version

    Create-Archive "Hangfire-$version"
}

Task Sign -Depends Pack -Description "Sign artifacts." {
    $version = Get-PackageVersion
    Sign-ArchiveContents "Hangfire-$version" "hangfire"
}
