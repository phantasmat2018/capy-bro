namespace CapyBro.Services;

public interface IInputSimulator
{
    Task SendCopyAsync(CancellationToken ct = default);

    Task SendPasteAsync(CancellationToken ct = default);

    /// <summary>
    /// Synthesises <c>Shift+Left × charCount</c> through <c>SendInput</c>
    /// so the just-pasted text ends up re-selected in the foreground
    /// editor.  Caller passes the number of UTF-16 code units (i.e.
    /// <c>string.Length</c>) of the text that was pasted; the
    /// implementation translates that to native key events that virtually
    /// every Windows text surface (Notepad / Word / Office / browsers /
    /// VS / VSCode / WinUI controls) interprets as "extend selection
    /// backwards by N characters."  No-op when <paramref name="charCount"/>
    /// is zero or negative.  Implementation may cap very large counts to
    /// keep the input queue from saturating; the trade-off is partial
    /// selection on multi-thousand-char pastes vs. a freeze of the same
    /// duration.
    /// </summary>
    Task SendSelectBackwardAsync(int charCount, CancellationToken ct = default);
}
