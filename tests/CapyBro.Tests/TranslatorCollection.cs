using System.Diagnostics.CodeAnalysis;

using Xunit;

namespace CapyBro.Tests;

/// <summary>
/// xUnit collection that serializes every test class which mutates the
/// process-wide <see cref="CapyBro.Services.Translator.Instance"/>
/// singleton (e.g. via <c>SetLanguage</c>).  xUnit runs test classes in
/// different collections in parallel by default; without this, two tests
/// in different classes can race on the singleton's language field, with
/// one test flipping the language mid-way through another's
/// <c>LoadAsync</c> / assertion path.
///
/// Symptom pre-fix:
/// <c>EditingDefaultPromptText_DoesNotLeakLanguageNameIntoOtherLocalesAsync</c>
/// would intermittently fail because its UA-keyed
/// <c>SelectedKey = "Перекласти на українську"</c> path saw an EN-flavoured
/// active map after a sibling test set <c>Translator.Instance</c> to
/// English.  Setting the language back inside the test wasn't enough — the
/// map is captured at <c>LoadAsync</c> time.
///
/// Trade-off: every Translator-aware test now runs strictly sequentially
/// against every other Translator-aware test.  That costs roughly the
/// duration of the slowest singleton-touching test (~2s wall) compared to
/// fully parallel execution; in exchange the suite stops flaking on CI
/// and on parallel-friendly developer machines.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
[SuppressMessage(
    "Naming",
    "CA1711:Identifiers should not have incorrect suffix",
    Justification = "xUnit's [CollectionDefinition] convention names the marker class with a Collection suffix.")]
public sealed class TranslatorCollection
{
    public const string Name = "TranslatorSingleton";
}
