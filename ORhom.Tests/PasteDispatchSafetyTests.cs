namespace ORhom.Tests;

public sealed class PasteDispatchSafetyTests
{
    [Fact]
    public void FullyAcceptedSendInputIsTheOnlyConfirmedSuccess()
    {
        var result = new PasteShortcutDispatchResult(
            AcceptedInputCount: 4,
            RequestedInputCount: 4,
            CleanupAttempted: false,
            CleanupSucceeded: true);

        Assert.Equal(
            PasteDispatchDecision.ConfirmedSuccess,
            PasteDispatchSafetyPolicy.Decide(result));
    }

    [Theory]
    [InlineData(0, false, true, false)]
    [InlineData(1, true, true, false)]
    [InlineData(2, true, true, true)]
    [InlineData(3, true, true, true)]
    [InlineData(3, true, false, true)]
    public void IncompleteSendInputDistinguishesSafeFallbackFromPossibleDelivery(
        uint acceptedInputCount,
        bool cleanupAttempted,
        bool cleanupSucceeded,
        bool mayHaveReachedTarget)
    {
        var result = new PasteShortcutDispatchResult(
            acceptedInputCount,
            RequestedInputCount: 4,
            cleanupAttempted,
            cleanupSucceeded);

        Assert.Equal(
            mayHaveReachedTarget
                ? PasteDispatchDecision.UnconfirmedMayHaveReachedTarget
                : PasteDispatchDecision.FailWithClipboardFallback,
            PasteDispatchSafetyPolicy.Decide(result));
    }

    [Fact]
    public void PartialSendInputReleasesControlBeforeTheNonModifier()
    {
        var calls = new List<Input[]>();
        var sendResults = new Queue<uint>([2, 1, 1]);

        var result = NativeMethods.SendPasteShortcutCore(
            inputs =>
            {
                calls.Add(inputs);
                return sendResults.Dequeue();
            });

        Assert.Equal((uint)2, result.AcceptedInputCount);
        Assert.True(result.CleanupAttempted);
        Assert.True(result.CleanupSucceeded);
        Assert.Equal(3, calls.Count);
        AssertKeyUp(calls[1], Keys.ControlKey);
        AssertKeyUp(calls[2], Keys.V);
    }

    [Fact]
    public void ModifierReleaseIsStillAttemptedWhenNonModifierCleanupWouldFail()
    {
        var calls = new List<Input[]>();
        var sendResults = new Queue<uint>([2, 1, 0, 0, 0]);

        var result = NativeMethods.SendPasteShortcutCore(
            inputs =>
            {
                calls.Add(inputs);
                return sendResults.Dequeue();
            });

        Assert.True(result.CleanupAttempted);
        Assert.False(result.CleanupSucceeded);
        AssertKeyUp(calls[1], Keys.ControlKey);
        Assert.All(
            calls.Skip(2),
            call => AssertKeyUp(call, Keys.V));
    }

    [Fact]
    public void RejectedSendInputDoesNotInjectUnnecessaryKeyUps()
    {
        var callCount = 0;

        var result = NativeMethods.SendPasteShortcutCore(
            _ =>
            {
                callCount++;
                return 0;
            });

        Assert.Equal(1, callCount);
        Assert.False(result.CleanupAttempted);
        Assert.True(result.CleanupSucceeded);
        Assert.Equal(
            PasteDispatchDecision.FailWithClipboardFallback,
            PasteDispatchSafetyPolicy.Decide(result));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    [InlineData(4, true)]
    public void ClipboardRestoreDelayReflectsWhetherPasteMayHaveReachedTheTarget(
        uint acceptedInputCount,
        bool expected)
    {
        var result = new PasteShortcutDispatchResult(
            acceptedInputCount,
            RequestedInputCount: 4,
            CleanupAttempted: acceptedInputCount is > 0 and < 4,
            CleanupSucceeded: true);

        Assert.Equal(expected, PasteDispatchSafetyPolicy.MayHaveReachedTarget(result));
    }

    private static void AssertKeyUp(Input[] inputs, Keys expectedKey)
    {
        var input = Assert.Single(inputs);
        Assert.Equal((uint)1, input.Type);
        Assert.Equal((ushort)expectedKey, input.Data.Keyboard.VirtualKey);
        Assert.Equal((uint)0x0002, input.Data.Keyboard.Flags);
    }
}
