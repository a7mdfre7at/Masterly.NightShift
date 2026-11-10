// ReSharper disable InvokeAsExtensionMethod
// ReSharper disable AssignNullToNotNullAttribute
// ReSharper disable UnusedTypeParameter

using System.Reflection;
using TypeExtensions = Masterly.NightShift.Common.TypeExtensions;

public class ClassWithoutNamespace
{
}

namespace Masterly.NightShift.Core.Tests.Common
{
    public class TypeExtensionsFacts
    {
        [Fact]
        public void ToGenericTypeString_PrintsNonGenericNestedClassName_WithDot()
        {
            Assert.Equal("NonGenericClass", typeof(NonGenericClass).ToGenericTypeString());
            Assert.Equal("NonGenericClass.NestedNonGenericClass", typeof(NonGenericClass.NestedNonGenericClass).ToGenericTypeString());
            Assert.Equal("NonGenericClass.NestedNonGenericClass.DoubleNestedNonGenericClass", typeof(NonGenericClass.NestedNonGenericClass.DoubleNestedNonGenericClass).ToGenericTypeString());
        }

        [Fact]
        public void ToGenericTypeString_PrintsOpenGenericNestedClassName_WithGenericParameters()
        {
            Assert.Equal("NonGenericClass.NestedGenericClass<T1,T2>", typeof(NonGenericClass.NestedGenericClass<,>).ToGenericTypeString());
            Assert.Equal("GenericClass<T1>", typeof(GenericClass<>).ToGenericTypeString());
            Assert.Equal("GenericClass<T1>.NestedNonGenericClass", typeof(GenericClass<>.NestedNonGenericClass).ToGenericTypeString());
            Assert.Equal("GenericClass<T1>.NestedNonGenericClass.DoubleNestedGenericClass<T2,T3,T4>", typeof(GenericClass<>.NestedNonGenericClass.DoubleNestedGenericClass<,,>).ToGenericTypeString());
        }

        [Fact]
        public void ToGenericTypeString_PrintsClosedGenericNestedClassName_WithGivenTypes()
        {
            Assert.Equal("NonGenericClass.NestedGenericClass<Assert,List<Assert>>", typeof(NonGenericClass.NestedGenericClass<Assert, List<Assert>>).ToGenericTypeString());
            Assert.Equal("GenericClass<Assert>", typeof(GenericClass<Assert>).ToGenericTypeString());
            Assert.Equal("GenericClass<List<Assert>>.NestedNonGenericClass", typeof(GenericClass<List<Assert>>.NestedNonGenericClass).ToGenericTypeString());
            Assert.Equal("GenericClass<List<GenericClass<List<Assert>>.NestedNonGenericClass.DoubleNestedGenericClass<Assert,List<Assert>,Stack<Assert>>>>.NestedNonGenericClass.DoubleNestedGenericClass<Assert,List<Assert>,Stack<Assert>>", typeof(GenericClass<List<GenericClass<List<Assert>>.NestedNonGenericClass.DoubleNestedGenericClass<Assert, List<Assert>, Stack<Assert>>>>.NestedNonGenericClass.DoubleNestedGenericClass<Assert, List<Assert>, Stack<Assert>>).ToGenericTypeString());
        }

        [Fact]
        public void ToGenericTypeString_CorrectlyHandlesTypesWithoutNamespace()
        {
            Assert.Equal("ClassWithoutNamespace", typeof(ClassWithoutNamespace).ToGenericTypeString());
        }

        [Fact]
        public void GetNonOpenMatchingMethod_ThrowsAnException_WhenTypeIsNull()
        {
            ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
                () => TypeExtensions.GetNonOpenMatchingMethod(null, "Method", []));

            Assert.Equal("type", exception.ParamName);
        }

        [Fact]
        public void GetNonOpenMatchingMethod_ThrowsAnException_WhenNameIsNull()
        {
            ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
                () => TypeExtensions.GetNonOpenMatchingMethod(typeof(NonGenericClass), null, []));

            Assert.Equal("name", exception.ParamName);
        }

        [Fact]
        public void GetNonOpenMatchingMethod_ReturnsCorrectMethod()
        {
            MethodInfo? method = TypeExtensions.GetNonOpenMatchingMethod(typeof(NonGenericClass), "Method", []);

            Assert.Equal("Method", method.Name);
            Assert.Equal(typeof(NonGenericClass), method.DeclaringType);
        }

        [Fact]
        public void GetNonOpenMatchingMethod_ReturnsCorrectMethodWithNoParameter_WhenParameterTypesIsNull()
        {
            MethodInfo? method = TypeExtensions.GetNonOpenMatchingMethod(typeof(NonGenericClass), "Method", null);

            Assert.Equal("Method", method.Name);
            Assert.Equal(typeof(NonGenericClass), method.DeclaringType);
        }

        [Fact]
        public void GetNonOpenMatchingMethod_ReturnsCorrectMethodWithOneParameter()
        {
            MethodInfo? method = TypeExtensions.GetNonOpenMatchingMethod(typeof(NonGenericClass), "Method",
                [typeof(int)]);

            Assert.Equal("Method", method.Name);
            Assert.Equal(typeof(NonGenericClass), method.DeclaringType);
            Assert.Single(method.GetParameters());
            Assert.Equal(typeof(int), method.GetParameters()[0].ParameterType);
        }

        [Fact]
        public void GetNonOpenMatchingMethod_ReturnsCorrectMethodWithManyParameters()
        {
            MethodInfo? method = TypeExtensions.GetNonOpenMatchingMethod(typeof(NonGenericClass), "Method",
                [typeof(int), typeof(int)]);

            Assert.Equal("Method", method.Name);
            Assert.Equal(typeof(NonGenericClass), method.DeclaringType);
            Assert.Equal(2, method.GetParameters().Length);
            Assert.Equal(typeof(int), method.GetParameters()[0].ParameterType);
            Assert.Equal(typeof(int), method.GetParameters()[1].ParameterType);
        }

        [Fact]
        public void GetNonOpenMatchingMethod_ReturnsCorrectMethod_WhenTypeIsInterface()
        {
            MethodInfo? method = TypeExtensions.GetNonOpenMatchingMethod(typeof(IParent), "Method", []);
            Assert.Equal("Method", method.Name);
        }

        [Fact]
        public void GetNonOpenMatchingMethod_HandlesMethodDefinedInBaseInterface()
        {
            MethodInfo? method = TypeExtensions.GetNonOpenMatchingMethod(typeof(IChild), "Method", []);
            Assert.Equal("Method", method.Name);
        }

        [Fact]
        public void GetNonOpenMatchingMethod_ReturnsCorrectGenericMethod()
        {
            MethodInfo? method = TypeExtensions.GetNonOpenMatchingMethod(typeof(NonGenericClass), "TrivialGenericMethod",
                [typeof(int), typeof(string), typeof(object)]);

            Assert.Equal("TrivialGenericMethod", method.Name);
            Assert.Equal(typeof(NonGenericClass), method.DeclaringType);
            Assert.True(method.IsGenericMethod);
            Assert.False(method.ContainsGenericParameters);
        }

        [Fact]
        public void GetNonOpenMatchingMethod_ReturnsNull_WhenMethodCouldNotBeFound()
        {
            MethodInfo? method = TypeExtensions.GetNonOpenMatchingMethod(typeof(NonGenericClass), "NonExistingMethod", []);

            Assert.Null(method);
        }

        [Fact]
        public void GetNonOpenMatchingMethod_ReturnsNull_WhenOveroladedMethodCouldNotBeFound()
        {
            MethodInfo? method = TypeExtensions.GetNonOpenMatchingMethod(typeof(NonGenericClass), "Method",
                [typeof(object), typeof(int)]);

            Assert.Null(method);
        }

        [Fact]
        public void GetNonOpenMatchingMethod_HandlesMethodNameIsCaseSensitive()
        {
            MethodInfo? method = TypeExtensions.GetNonOpenMatchingMethod(typeof(NonGenericClass), "method", []);

            Assert.Null(method);
        }

        [Fact]
        public void GetNonOpenMatchingMethod_ReturnsNull_WhenMethodParameterTypeIsAssignableFromPassedType()
        {
            MethodInfo? method = TypeExtensions.GetNonOpenMatchingMethod(typeof(NonGenericClass), "Method",
                [typeof(NonGenericClass)]);

            Assert.Null(method);
        }

        [Fact]
        public void GetNonOpenMatchingMethod_HandlesMethodHasParameterWhoseTypeContainsGenericParameter()
        {
            MethodInfo? method = TypeExtensions.GetNonOpenMatchingMethod(typeof(NonGenericClass), "OtherGenericMethod",
                [typeof(IEnumerable<int>)]);

            Assert.Equal("OtherGenericMethod", method.Name);
            Assert.Single(method.GetParameters());
            Assert.Equal(typeof(IEnumerable<int>), method.GetParameters()[0].ParameterType);
        }

        [Fact]
        public void GetNonOpenMatchingMethod_HandlesMethodHasParameterWhoseTypeContainsGenericParameterAndIsComplicated()
        {
            MethodInfo? method = TypeExtensions.GetNonOpenMatchingMethod(typeof(NonGenericClass), "OtherGenericMethod",
                [typeof(List<IEnumerable<int>>)]);

            Assert.Equal("OtherGenericMethod", method.Name);
            Assert.Single(method.GetParameters());
            Assert.Equal(typeof(List<IEnumerable<int>>), method.GetParameters()[0].ParameterType);
        }

        [Fact]
        public void GetNonOpenMatchingMethod_HandlesMethodHasParameterWhoseTypeIsGenericAndContainsTwoGenericParameters()
        {
            MethodInfo? method = TypeExtensions.GetNonOpenMatchingMethod(typeof(NonGenericClass), "OtherGenericMethod",
                [typeof(Tuple<int, double>)]);

            Assert.Equal("OtherGenericMethod", method.Name);
            Assert.Single(method.GetParameters());
            Assert.Equal(typeof(Tuple<int, double>), method.GetParameters()[0].ParameterType);
        }

        [Fact]
        public void GetNonOpenMatchingMethod_HandlesNonTrivialOrderOfUsingMethodGenericParametersInMethodParameterTypes()
        {
            MethodInfo? method = TypeExtensions.GetNonOpenMatchingMethod(typeof(NonGenericClass), "OneMoreGenericMethod",
                [typeof(Tuple<int, double, float>)]);

            Assert.Equal("OneMoreGenericMethod", method.Name);
            Assert.Single(method.GetParameters());
            Assert.Equal(typeof(Tuple<int, double, float>), method.GetParameters()[0].ParameterType);
        }

        [Fact]
        public void GetNonOpenMatchingMethod_HandlesMethodHasSomeParametersOfTheSameTypeWhichIsMethodGenericParameter()
        {
            MethodInfo? method = TypeExtensions.GetNonOpenMatchingMethod(typeof(NonGenericClass), "GenericMethod",
                [typeof(int), typeof(int)]);

            Assert.Equal("GenericMethod", method.Name);
            Assert.Equal(2, method.GetParameters().Length);
            Assert.Equal(typeof(int), method.GetParameters()[0].ParameterType);
            Assert.Equal(typeof(int), method.GetParameters()[1].ParameterType);
        }

        [Fact]
        public void GetNonOpenMatchingMethod_HandlesMethodHasGenericAndNonGenericParameters()
        {
            MethodInfo? method = TypeExtensions.GetNonOpenMatchingMethod(typeof(NonGenericClass), "GenericMethod",
                [typeof(int), typeof(NonGenericClass), typeof(double)]);

            Assert.Equal("GenericMethod", method.Name);
            Assert.Equal(3, method.GetParameters().Length);
            Assert.Equal(typeof(int), method.GetParameters()[0].ParameterType);
            Assert.Equal(typeof(NonGenericClass), method.GetParameters()[1].ParameterType);
            Assert.Equal(typeof(double), method.GetParameters()[2].ParameterType);
        }

        [Fact]
        public void GetNonOpenMatchingMethod_HandlesMethodHasParameterOfGenericTypeWhichContainsMe()
        {
            MethodInfo? method = TypeExtensions.GetNonOpenMatchingMethod(typeof(NonGenericClass), "GenericMethod",
                [typeof(Tuple<double, List<int>>)]);

            Assert.Equal("GenericMethod", method.Name);
            Assert.Single(method.GetParameters());
            Assert.Equal(typeof(Tuple<double, List<int>>), method.GetParameters()[0].ParameterType);
        }

        [Fact]
        public void GetNonOpenMatchingMethod_HandlesMethodHasSomeParametersWhoseTypesContainsTheSameGenericParameter()
        {
            MethodInfo? method = TypeExtensions.GetNonOpenMatchingMethod(typeof(NonGenericClass), "GenericMethod",
                [typeof(int), typeof(double)]);

            Assert.Null(method);
        }

        [Fact]
        public void GetNonOpenMatchingMethod_ReturnsNull_WhenParameterTypeIsMatchedByGenericTypeAndNotMatchedByGenericArguments()
        {
            MethodInfo? method = TypeExtensions.GetNonOpenMatchingMethod(typeof(NonGenericClass), "OtherGenericMethod",
                [typeof(List<int>)]);

            Assert.Null(method);
        }

        [Fact]
        public void GetNonOpenMatchingMethod_ReturnsCorrectMethod_WhenParameterTypeIsGenericArray()
        {
            MethodInfo? method = TypeExtensions.GetNonOpenMatchingMethod(typeof(NonGenericClass), "GenericMethod",
                [typeof(string[])]);

            Assert.Equal("GenericMethod", method.Name);
            Assert.Equal(typeof(string[]), method.GetParameters()[0].ParameterType);
        }

        [Fact]
        public void GetNonOpenMatchingMethod_ReturnsCorrectMethod_WhenParameterTypeIsComplicatedGenericArray()
        {
            MethodInfo? method = TypeExtensions.GetNonOpenMatchingMethod(typeof(NonGenericClass), "GenericMethod",
                [typeof(List<int>[])]);

            Assert.Equal("GenericMethod", method.Name);
            Assert.Equal(typeof(List<int>[]), method.GetParameters()[0].ParameterType);
        }

        [Fact]
        public void GetNonOpenMatchingMethod_ReturnsNull_WhenMatchingGenricMethodNotBeFound()
        {
            MethodInfo? method = TypeExtensions.GetNonOpenMatchingMethod(typeof(NonGenericClass), "GenericMethod",
                [typeof(Tuple<List<int>, string>)]);

            Assert.Null(method);
        }

        [Fact]
        public void GetNonOpenMatchingMethod_HandlesMethodHasNoParametersOrTypes()
        {
            MethodInfo? method = TypeExtensions.GetNonOpenMatchingMethod(typeof(NonGenericClass), "GenericMethod",
                []);

            Assert.Null(method);
        }

        [Fact]
        public void GetNonOpenMatchingMethod_HandlesMethodHasNoParametersOrTypes2()
        {
            // public void GenericMethod<T0, T1>(T0 arg) { }
            MethodInfo? method = TypeExtensions.GetNonOpenMatchingMethod(typeof(NonGenericClass), "GenericMethod",
                [typeof(string)]);

            Assert.Null(method);
        }

        [Theory]
        [InlineData(typeof(OverloadsBaseFirst))]
        [InlineData(typeof(OverloadsDerivedFirst))]
        public void GetNonOpenMatchingMethod_ReturnsTheExactOverload_WhateverTheDeclarationOrder(Type type)
        {
            MethodInfo? method = TypeExtensions.GetNonOpenMatchingMethod(type, "Handle", [typeof(OverloadDerived)]);

            Assert.Equal(typeof(OverloadDerived), method.GetParameters()[0].ParameterType);
        }

        [Theory]
        [InlineData(typeof(OverloadsBaseFirst))]
        [InlineData(typeof(OverloadsDerivedFirst))]
        public void GetNonOpenMatchingMethod_ReturnsTheExactOverload_ForTheLessDerivedType(Type type)
        {
            MethodInfo? method = TypeExtensions.GetNonOpenMatchingMethod(type, "Handle", [typeof(OverloadBase)]);

            Assert.Equal(typeof(OverloadBase), method.GetParameters()[0].ParameterType);
        }

        [Theory]
        [InlineData(typeof(OverloadsBaseFirst))]
        [InlineData(typeof(OverloadsDerivedFirst))]
        public void GetNonOpenMatchingMethod_PrefersTheExactOverload_OverAnInterfaceOne(Type type)
        {
            MethodInfo? method = TypeExtensions.GetNonOpenMatchingMethod(type, "Process", [typeof(OverloadMessage)]);

            Assert.Equal(typeof(OverloadMessage), method.GetParameters()[0].ParameterType);
        }

        [Fact]
        public void GetNonOpenMatchingMethod_StillReturnsAnAssignableOverload_WhenNoneIsExact()
        {
            MethodInfo? method = TypeExtensions.GetNonOpenMatchingMethod(typeof(OverloadsBaseFirst), "Process", [typeof(OtherOverloadMessage)]);

            Assert.Equal(typeof(IOverloadMessage), method.GetParameters()[0].ParameterType);
        }

        [Fact]
        public void GetNonOpenMatchingMethod_PrefersANonGenericExactOverload_OverAGenericOne()
        {
            MethodInfo? method = TypeExtensions.GetNonOpenMatchingMethod(typeof(OverloadsBaseFirst), "Run", [typeof(OverloadDerived)]);

            Assert.False(method.IsGenericMethod);
            Assert.Equal(typeof(OverloadDerived), method.GetParameters()[0].ParameterType);
        }

        [Fact]
        public void GetNonOpenMatchingMethod_PrefersAGenericMethodWithExactParameterTypes_OverAnAssignableOverload()
        {
            MethodInfo? method = TypeExtensions.GetNonOpenMatchingMethod(typeof(OverloadsBaseFirst), "Run", [typeof(OverloadMoreDerived)]);

            Assert.True(method.IsGenericMethod);
            Assert.Equal([typeof(OverloadMoreDerived)], method.GetGenericArguments());
        }
    }

    public class OverloadBase { }

    public class OverloadDerived : OverloadBase { }

    public class OverloadMoreDerived : OverloadDerived { }

    public interface IOverloadMessage { }

    public class OverloadMessage : IOverloadMessage { }

    public class OtherOverloadMessage : IOverloadMessage { }

    public class OverloadsBaseFirst
    {
        public void Handle(OverloadBase value) { }

        public void Handle(OverloadDerived value) { }

        public void Process(IOverloadMessage message) { }

        public void Process(OverloadMessage message) { }

        public void Run(OverloadBase value) { }

        public void Run<T>(T value) { }

        public void Run(OverloadDerived value) { }
    }

    public class OverloadsDerivedFirst
    {
        public void Handle(OverloadDerived value) { }

        public void Handle(OverloadBase value) { }

        public void Process(OverloadMessage message) { }

        public void Process(IOverloadMessage message) { }
    }

    public class GenericClass<T1>
    {
        public class NestedNonGenericClass
        {
            public class DoubleNestedGenericClass<T2, T3, T4>
            {

            }
        }
    }

    public interface IParent
    {
        void Method();
    }

    public interface IChild : IParent { }

    public class NonGenericClass
    {
        public void Method() { }

        public void Method(int arg) { }

        public void Method(int arg0, int arg1) { }

        public void Method(IParent arg) { }

        public void Method(object arg) { }

        public void TrivialGenericMethod<T0, T1, T2>(T0 arg0, T1 arg1, T2 arg2) { }

        public void OtherGenericMethod<T>(IEnumerable<T> arg0) { }

        public void OtherGenericMethod<T>(List<IEnumerable<T>> arg0) { }

        public void OtherGenericMethod<T0, T1>(Tuple<T0, T1> arg0) { }

        public void OneMoreGenericMethod<T0, T1, T2>(Tuple<T2, T0, T1> arg0) { }

        public void GenericMethod<T0>() { }

        public void GenericMethod<T0, T1>(T0 arg) { }

        public void GenericMethod<T>(T arg0, T arg1) { }

        public void GenericMethod<T>(int arg0, T arg1, double arg2) { }

        public void GenericMethod<T>(Tuple<T, List<int>> arg) { }

        public void GenericMethod<T>(T[] arg) { }

        public void GenericMethod<T>(List<T>[] arg) { }

        public class NestedNonGenericClass
        {
            public class DoubleNestedNonGenericClass
            {

            }
        }

        public class NestedGenericClass<T1, T2>
        {
        }
    }
}
