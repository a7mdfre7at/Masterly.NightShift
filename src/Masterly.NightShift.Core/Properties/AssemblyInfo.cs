using System;
using System.Reflection;
using System.Resources;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

[assembly: AssemblyTitle("Masterly.NightShift")]
[assembly: AssemblyDescription("Core classes of Masterly.NightShift that are independent of any framework.")]
[assembly: Guid("4deecd4f-19f6-426b-aa87-6cd1a03eaa48")]
[assembly: CLSCompliant(true)]
[assembly: InternalsVisibleTo("Masterly.NightShift.Core.Tests")]
[assembly: NeutralResourcesLanguage("en")]

// Allow the generation of mocks for internal types
[assembly: InternalsVisibleTo("DynamicProxyGenAssembly2")]