using System;
using System.Collections.Generic;
using Masterly.NightShift.Common;
using Masterly.NightShift.Server;
using Masterly.NightShift.States;
using Xunit;

namespace Masterly.NightShift.Core.Tests.Common
{
    public class TypeHelperFacts
    {
        [Fact]
        public void DefaultTypeResolver_ResolvesTheCurrentName()
        {
            var type = TypeHelper.DefaultTypeResolver(typeof(PerformContext).AssemblyQualifiedName);

            Assert.Equal(typeof(PerformContext), type);
        }

        [Theory]
        [InlineData("Hangfire.Server.PerformContext, Hangfire.Core")]
        [InlineData("Hangfire.Server.PerformContext, Hangfire.Core, Version=1.8.25.0, Culture=neutral, PublicKeyToken=null")]
        [InlineData("Hangfire.Server.PerformContext, HANGFIRE.CORE")]
        public void DefaultTypeResolver_ResolvesTypesStoredBeforeTheRename(string typeName)
        {
            var type = TypeHelper.DefaultTypeResolver(typeName);

            Assert.Equal(typeof(PerformContext), type);
        }

        [Fact]
        public void DefaultTypeResolver_ResolvesGenericArguments_StoredBeforeTheRename()
        {
            var type = TypeHelper.DefaultTypeResolver(
                "System.Collections.Generic.List`1[[Hangfire.States.EnqueuedState, Hangfire.Core]], mscorlib");

            Assert.Equal(typeof(List<EnqueuedState>), type);
        }

        [Fact]
        public void IgnoredAssemblyVersionTypeResolver_ResolvesTypesStoredBeforeTheRename()
        {
            var type = TypeHelper.IgnoredAssemblyVersionTypeResolver(
                "Hangfire.IJobCancellationToken, Hangfire.Core, Version=1.7.0.0, Culture=neutral, PublicKeyToken=null");

            Assert.Equal(typeof(IJobCancellationToken), type);
        }

        [Fact]
        public void DefaultTypeResolver_DoesNotRenameTypes_FromOtherAssemblies()
        {
            Assert.ThrowsAny<Exception>(() => TypeHelper.DefaultTypeResolver("Hangfire.Server.PerformContext, mscorlib"));
        }
    }
}
