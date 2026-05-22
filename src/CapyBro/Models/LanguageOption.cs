namespace CapyBro.Models;

// H23 (FZ4-F2) fix: language-picker dropdown items used to display the
// raw enum names ("English / Ukrainian / Russian") regardless of UI
// locale.  Wrapping the value with a translator-sourced DisplayName
// lets the ComboBox render autonyms ("English / Українська / Русский")
// without coupling the XAML to the Translator singleton in markup.
// SelectedValuePath="Value" keeps the bound VM property as the raw
// Language enum, so the wire format and storage layer are unchanged.
public sealed record LanguageOption(Language Value, string DisplayName);
