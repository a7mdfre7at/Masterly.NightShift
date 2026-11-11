namespace Masterly.NightShift.Core.Tests;

public sealed class ArgumentCompressionFacts : IDisposable
{
    public void Dispose()
    {
        ArgumentCompression.MinLength = null;
    }

    [Fact, StaticLock]
    public void Compress_LeavesArguments_WhenCompressionIsOff()
    {
        string arguments = "[\"" + new string('a', 5000) + "\"]";

        Assert.Same(arguments, ArgumentCompression.Compress(arguments));
    }

    [Fact, StaticLock]
    public void Compress_AndDecompress_RoundTripLongArguments()
    {
        ArgumentCompression.MinLength = 100;
        string arguments = "[\"" + new string('a', 5000) + "\"]";

        string compressed = ArgumentCompression.Compress(arguments);

        Assert.StartsWith(ArgumentCompression.Prefix, compressed, StringComparison.Ordinal);
        Assert.True(compressed.Length < arguments.Length);
        Assert.Equal(arguments, ArgumentCompression.Decompress(compressed));
    }

    [Fact, StaticLock]
    public void Compress_KeepsShortArguments_AndOnesThatDoNotShrink()
    {
        ArgumentCompression.MinLength = 10;

        Assert.Equal("[\"1\"]", ArgumentCompression.Compress("[\"1\"]"));
        string random = "[\"" + Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(30)) + "\"]";
        Assert.Equal(random, ArgumentCompression.Compress(random));
    }

    [Fact, StaticLock]
    public void Decompress_ReadsCompressedArguments_EvenWhenCompressionIsOff()
    {
        ArgumentCompression.MinLength = 10;
        string compressed = ArgumentCompression.Compress("[\"" + new string('x', 500) + "\"]");
        ArgumentCompression.MinLength = null;

        Assert.Equal("[\"" + new string('x', 500) + "\"]", ArgumentCompression.Decompress(compressed));
        Assert.Null(ArgumentCompression.Decompress(null));
        Assert.Equal("[]", ArgumentCompression.Decompress("[]"));
    }

    [Fact, StaticLock]
    public void InvocationData_StoresCompressedArguments_AndReadsThemBack()
    {
        new Mock<IGlobalConfiguration>().Object.UseArgumentCompression(50);
        string text = new('z', 2000);

        InvocationData data = InvocationData.SerializeJob(Job.FromExpression(() => Method(text)));
        InvocationData fromPayload = InvocationData.DeserializePayload(data.SerializePayload()!);

        Assert.StartsWith(ArgumentCompression.Prefix, data.Arguments, StringComparison.Ordinal);
        Assert.Equal([text], data.DeserializeJob().Args);
        Assert.Equal([text], fromPayload.DeserializeJob().Args);
    }

    [Fact, StaticLock]
    public void UseArgumentCompression_RefusesALengthBelowOne()
    {
        Assert.Throws<ArgumentOutOfRangeException>("value", () => new Mock<IGlobalConfiguration>().Object.UseArgumentCompression(0));
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "xUnit1013:Public method should be marked as test", Justification = "Should be public to be a job")]
    public static void Method(string text)
    {
        _ = text;
    }
}
