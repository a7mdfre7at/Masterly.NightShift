using Masterly.NightShift.Server;

namespace Masterly.NightShift.Core.Tests.Common;

public class TypeHelperFacts
{
    [Fact]
    public void DefaultTypeResolver_ResolvesTheCurrentName()
    {
        Type type = TypeHelper.DefaultTypeResolver(typeof(PerformContext).AssemblyQualifiedName);

        Assert.Equal(typeof(PerformContext), type);
    }

    [Theory]
    [InlineData("Hangfire.Server.PerformContext, Hangfire.Core")]
    [InlineData("Hangfire.Server.PerformContext, Hangfire.Core, Version=1.8.25.0, Culture=neutral, PublicKeyToken=null")]
    [InlineData("Hangfire.Server.PerformContext, HANGFIRE.CORE")]
    public void DefaultTypeResolver_ResolvesTypesStoredBeforeTheRename(string typeName)
    {
        Type type = TypeHelper.DefaultTypeResolver(typeName);

        Assert.Equal(typeof(PerformContext), type);
    }

    [Fact]
    public void DefaultTypeResolver_Throws_ForAPartialNameOfAMissingAssembly()
    {
        Assert.ThrowsAny<FileNotFoundException>(
            () => TypeHelper.DefaultTypeResolver("Some.Type, Masterly.NightShift.NoSuchAssembly"));
    }

    [Fact]
    public void DefaultTypeResolver_ResolvesGenericArguments_StoredBeforeTheRename()
    {
        Type type = TypeHelper.DefaultTypeResolver(
            "System.Collections.Generic.List`1[[Hangfire.States.EnqueuedState, Hangfire.Core]], mscorlib");

        Assert.Equal(typeof(List<EnqueuedState>), type);
    }

    [Fact]
    public void IgnoredAssemblyVersionTypeResolver_ResolvesTypesStoredBeforeTheRename()
    {
        Type type = TypeHelper.IgnoredAssemblyVersionTypeResolver(
            "Hangfire.IJobCancellationToken, Hangfire.Core, Version=1.7.0.0, Culture=neutral, PublicKeyToken=null");

        Assert.Equal(typeof(IJobCancellationToken), type);
    }

    [Fact]
    public void DefaultTypeResolver_DoesNotRenameTypes_FromOtherAssemblies()
    {
        Assert.ThrowsAny<Exception>(() => TypeHelper.DefaultTypeResolver("Hangfire.Server.PerformContext, mscorlib"));
    }
}
