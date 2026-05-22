using System.Reflection;

using CapyBro.Views;

using FluentAssertions;

using Xunit;

namespace CapyBro.Tests.Views;

public class ConfirmDialogTests
{
    [Fact]
    public void Ask_WhenAnotherDialogActive_ReturnsNullWithoutOpeningWindow()
    {
        // H21 (FZ2-F1) regression: a re-entrant Ask call (or any
        // overlap from cross-agent UIAutomation) must not stack
        // another modal on top of the first — that's how ESC dismisses
        // the wrong dialog (live functional QA repro).  We simulate
        // "another dialog active" by setting the static counter via
        // reflection; the counter is private to keep the API surface
        // minimal in production.
        var counterField = typeof(ConfirmDialog).GetField(
            "_activeDialogCount",
            BindingFlags.NonPublic | BindingFlags.Static);
        counterField.Should().NotBeNull("the modal-stack guard relies on this internal counter");
        counterField!.SetValue(null, 1);

        try
        {
            // Owner=null is fine because the guard short-circuits BEFORE
            // any ConfirmDialog instance is constructed; a real owner
            // would be required only if ShowDialog were reached.
            var result = ConfirmDialog.Ask(
                "title",
                "body",
                "OK",
                owner: null);

            result.Should().BeNull(
                "a stacked Ask must return null (the user-cancelled sentinel) rather than open a second window");
        }
        finally
        {
            ConfirmDialog.ResetActiveDialogCountForTests();
        }
    }
}
