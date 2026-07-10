using FlowEncode.Application;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FlowEncode.Domain.Tests;

[TestClass]
public sealed class VapourSynthEditorBindingStateTests
{
    [TestMethod]
    public void BeginLoad_InvalidatesPreviouslyConfirmedBinding()
    {
        var state = new VapourSynthEditorBindingState();
        var first = state.BeginLoad("tab-a");
        Assert.IsTrue(state.TryConfirm(first, first));

        var second = state.BeginLoad("tab-b");

        Assert.IsFalse(state.IsConfirmed(first));
        Assert.AreEqual(second, state.PendingBinding);
        Assert.IsNull(state.ConfirmedBinding);
    }

    [TestMethod]
    public void TryConfirm_RejectsOutOfOrderLoadAcknowledgement()
    {
        var state = new VapourSynthEditorBindingState();
        var first = state.BeginLoad("tab-a");
        var second = state.BeginLoad("tab-b");

        Assert.IsFalse(state.TryConfirm(first, first));
        Assert.IsTrue(state.TryConfirm(second, second));
        Assert.AreEqual(second, state.ConfirmedBinding);
    }

    [TestMethod]
    public void TryConfirm_RejectsMismatchedAcknowledgement()
    {
        var state = new VapourSynthEditorBindingState();
        var expected = state.BeginLoad("tab-a");
        var mismatched = expected with { TabId = "tab-b" };

        Assert.IsFalse(state.TryConfirm(expected, mismatched));
        Assert.IsNull(state.ConfirmedBinding);
        Assert.AreEqual(expected, state.PendingBinding);
    }

    [TestMethod]
    public void Invalidate_RejectsLateEventsFromConfirmedDocument()
    {
        var state = new VapourSynthEditorBindingState();
        var binding = state.BeginLoad("tab-a");
        Assert.IsTrue(state.TryConfirm(binding, binding));

        state.Invalidate();

        Assert.IsFalse(state.IsConfirmed(binding));
        Assert.IsNull(state.PendingBinding);
        Assert.IsNull(state.ConfirmedBinding);
    }
}
