using CapyBro.Services;

using FluentAssertions;

using Xunit;

namespace CapyBro.Tests.Services;

public class PrivacyRedactorTests
{
    private readonly PrivacyRedactor _sut = new();

    [Fact]
    public void Redact_PlainText_NoChange()
    {
        var result = _sut.Redact("Привіт, як справи?");

        result.RedactedText.Should().Be("Привіт, як справи?");
        result.Mapping.Should().BeEmpty();
        result.HasRedactions.Should().BeFalse();
    }

    [Fact]
    public void Redact_Email_ReplacedWithPlaceholder()
    {
        var result = _sut.Redact("Напиши на john.doe@company.com якнайшвидше");

        result.RedactedText.Should().Be("Напиши на <<EMAIL_1>> якнайшвидше");
        result.Mapping.Should().ContainKey("<<EMAIL_1>>");
        result.Mapping["<<EMAIL_1>>"].Should().Be("john.doe@company.com");
    }

    [Fact]
    public void Redact_DuplicateEmails_ShareSinglePlaceholder()
    {
        // Coreference preservation: the model should see "same email
        // mentioned twice" rather than two distinct placeholders.
        var result = _sut.Redact("Email john@a.com again at john@a.com please");

        result.RedactedText.Should().Be("Email <<EMAIL_1>> again at <<EMAIL_1>> please");
        result.Mapping.Should().HaveCount(1);
    }

    [Fact]
    public void Redact_TwoDifferentEmails_GetSeparatePlaceholders()
    {
        var result = _sut.Redact("From alice@x.com to bob@y.com");

        result.RedactedText.Should().Be("From <<EMAIL_1>> to <<EMAIL_2>>");
        result.Mapping.Should().HaveCount(2);
        result.Mapping["<<EMAIL_1>>"].Should().Be("alice@x.com");
        result.Mapping["<<EMAIL_2>>"].Should().Be("bob@y.com");
    }

    [Fact]
    public void Redact_Url_HttpAndHttps_BothCaught()
    {
        var result = _sut.Redact("See http://a.example/ and https://b.example/path?q=1");

        result.RedactedText.Should().Be("See <<URL_1>> and <<URL_2>>");
    }

    [Fact]
    public void Redact_Url_BareWww_AlsoCaught()
    {
        var result = _sut.Redact("Visit www.openrouter.ai for details");

        result.RedactedText.Should().Be("Visit <<URL_1>> for details");
    }

    [Fact]
    public void Redact_UrlWithEmailLikeQuery_DoesNotDoubleRedact()
    {
        // URL pass runs FIRST so the email-shaped substring inside the
        // URL gets consumed as part of the URL — without this ordering,
        // the email regex would carve out the @ portion and corrupt the
        // URL.
        var result = _sut.Redact("API https://api.x.com/?contact=john@a.com end");

        result.RedactedText.Should().Be("API <<URL_1>> end");
        result.Mapping.Should().HaveCount(1);
        result.Mapping["<<URL_1>>"].Should().Contain("john@a.com");
    }

    [Fact]
    public void Redact_Iban_Replaced()
    {
        var result = _sut.Redact("IBAN: DE89370400440532013000 — confirm");

        result.RedactedText.Should().Be("IBAN: <<IBAN_1>> — confirm");
        result.Mapping["<<IBAN_1>>"].Should().Be("DE89370400440532013000");
    }

    [Fact]
    public void Redact_PhoneInternational_Replaced()
    {
        var result = _sut.Redact("Call +380 67 123 4567 today");

        result.RedactedText.Should().Be("Call <<PHONE_1>> today");
    }

    [Fact]
    public void Redact_PhoneWithDashes_Replaced()
    {
        var result = _sut.Redact("Phone 067-123-4567 ASAP");

        result.RedactedText.Should().Be("Phone <<PHONE_1>> ASAP");
    }

    [Fact]
    public void Redact_ContiguousNumber_NotMatchedAsPhone()
    {
        // Bare numbers without separators are too ambiguous (could be
        // ISBN, UPC, ID number) — phone regex requires at least one
        // separator. We accept this limitation to avoid false positives.
        var result = _sut.Redact("Order 5551234567 was shipped");

        result.RedactedText.Should().Be("Order 5551234567 was shipped");
        result.Mapping.Should().BeEmpty();
    }

    [Fact]
    public void Restore_SimpleRoundTrip_RecoversOriginal()
    {
        var original = "Email me at jane@x.com or call +1 555 123 4567";
        var redacted = _sut.Redact(original);

        var restored = _sut.Restore(redacted.RedactedText, redacted.Mapping);

        restored.Should().Be(original);
    }

    [Fact]
    public void Restore_AiPreservedPlaceholders_MultipleCategories_AllRestored()
    {
        // Simulate what we'd actually receive from the model: it
        // rephrases the surrounding text but keeps placeholders verbatim.
        var redacted = _sut.Redact("Send to alice@x.com via https://api.x.com/upload");
        var aiOutput = "Будь ласка, надішли на <<EMAIL_1>> через <<URL_1>>.";

        var restored = _sut.Restore(aiOutput, redacted.Mapping);

        restored.Should().Be("Будь ласка, надішли на alice@x.com через https://api.x.com/upload.");
    }

    [Fact]
    public void Restore_PlaceholderDroppedByAi_LeavesOriginalPlaceholderInOutput()
    {
        // Edge case: the model deleted our placeholder. The restore can't
        // recover what the model dropped — the output simply omits the
        // PII (which is, in fact, fine — better than leaking it).
        var redacted = _sut.Redact("Send to alice@x.com");
        var aiOutput = "Send the message."; // no placeholder

        var restored = _sut.Restore(aiOutput, redacted.Mapping);

        restored.Should().Be("Send the message.");
        // No PII leaked.
        restored.Should().NotContain("alice");
    }

    [Fact]
    public void Restore_EmptyMapping_PassesThroughUnchanged()
    {
        var restored = _sut.Restore("plain text", new Dictionary<string, string>());

        restored.Should().Be("plain text");
    }

    [Fact]
    public void Redact_EmptyInput_NoCrash()
    {
        var result = _sut.Redact(string.Empty);

        result.RedactedText.Should().Be(string.Empty);
        result.Mapping.Should().BeEmpty();
    }

    [Fact]
    public void Redact_AllPatternsTogether_AllReplaced()
    {
        var input = "Contact alice@x.com or http://x.com, IBAN DE89370400440532013000, phone +380 67 123 4567";

        var result = _sut.Redact(input);

        result.RedactedText.Should().NotContain("alice@x.com");
        result.RedactedText.Should().NotContain("http://x.com");
        result.RedactedText.Should().NotContain("DE89370400440532013000");
        result.RedactedText.Should().NotContain("+380 67 123 4567");
        result.Mapping.Should().HaveCount(4);
    }

    // ------- Unicode / IDN coverage (regression: old regex was ASCII-only) -------
    [Fact]
    public void Redact_CyrillicLocalPart_Replaced()
    {
        var result = _sut.Redact("напиши на ваня@example.com сьогодні");

        result.RedactedText.Should().Be("напиши на <<EMAIL_1>> сьогодні");
        result.Mapping["<<EMAIL_1>>"].Should().Be("ваня@example.com");
    }

    [Fact]
    public void Redact_CyrillicDomain_Replaced()
    {
        var result = _sut.Redact("пиши на test@почта.рф будь ласка");

        result.RedactedText.Should().Be("пиши на <<EMAIL_1>> будь ласка");
        result.Mapping["<<EMAIL_1>>"].Should().Be("test@почта.рф");
    }

    [Fact]
    public void Redact_IdnPunycodeEmail_NotTruncated()
    {
        // Old regex stopped at the second `--` in the punycode TLD,
        // leaving `--p1ai` as visible plaintext.  The Unicode-aware
        // regex now consumes the entire address.
        var result = _sut.Redact("пиши test@xn--80a1acny.xn--p1ai зараз");

        result.RedactedText.Should().Be("пиши <<EMAIL_1>> зараз");
        result.Mapping["<<EMAIL_1>>"].Should().Be("test@xn--80a1acny.xn--p1ai");
    }

    // ------- IBAN with spaces (regression: PHONE regex used to shred it) -------
    [Fact]
    public void Redact_IbanWithSpaces_TreatedAsSingleIban()
    {
        var result = _sut.Redact("IBAN DE89 3704 0044 0532 0130 00 ось");

        result.RedactedText.Should().Be("IBAN <<IBAN_1>> ось");
        result.Mapping["<<IBAN_1>>"].Should().Be("DE89 3704 0044 0532 0130 00");
    }

    [Fact]
    public void Redact_IbanWithSpaces_RoundTrip()
    {
        var original = "Send to IBAN DE89 3704 0044 0532 0130 00 today";
        var redacted = _sut.Redact(original);

        var restored = _sut.Restore(redacted.RedactedText, redacted.Mapping);

        restored.Should().Be(original);
    }

    // ------- Credit card patterns -------
    [Fact]
    public void Redact_CreditCard16ContiguousDigits_Replaced()
    {
        var result = _sut.Redact("картка 4111111111111111 діє");

        result.RedactedText.Should().Be("картка <<CARD_1>> діє");
        result.Mapping["<<CARD_1>>"].Should().Be("4111111111111111");
    }

    [Fact]
    public void Redact_CreditCardSpacedGroups_NotShreddedByPhoneRegex()
    {
        // Pre-fix bug: PHONE regex matched the first 8 digits as a phone
        // ("4111 1111") and left the last 8 digits as plaintext.
        var result = _sut.Redact("картка 4111 1111 1111 1111 діє");

        result.RedactedText.Should().Be("картка <<CARD_1>> діє");
        result.RedactedText.Should().NotContain("1111");
        result.Mapping["<<CARD_1>>"].Should().Be("4111 1111 1111 1111");
    }

    [Fact]
    public void Redact_CreditCardAmex_4_6_5_Grouping_Replaced()
    {
        var result = _sut.Redact("amex 3782 822463 10005 valid");

        result.RedactedText.Should().Be("amex <<CARD_1>> valid");
        result.Mapping["<<CARD_1>>"].Should().Be("3782 822463 10005");
    }

    [Fact]
    public void Redact_CreditCardWithDashes_Replaced()
    {
        var result = _sut.Redact("card 4111-1111-1111-1111 stored");

        result.RedactedText.Should().Be("card <<CARD_1>> stored");
    }

    // Regression: pre-fix, the "13-19 digits" CC fallback regex matched
    // every long integer in the text — order numbers, ISBNs, employee IDs,
    // accounting balances — and replaced them with <<CARD_n>>. After
    // adding a Luhn check, only numbers that pass the mod-10 checksum
    // (i.e. real PAN candidates) are redacted.
    [Theory]
    [InlineData("Замовлення 1234567890123 готове", "1234567890123")]
    [InlineData("ISBN-13: 9780131103627 в каталозі", "9780131103627")]
    [InlineData("Order id 9999888877776666 listed", "9999888877776666")]
    public void Redact_LongIntegerThatFailsLuhn_LeftAsPlaintext(string input, string expectedSurvivor)
    {
        var result = _sut.Redact(input);

        result.RedactedText.Should().Contain(
            expectedSurvivor,
            "Luhn-failing digit sequences are not credit cards and must not be substituted with <<CARD_n>>");
        result.Mapping.Values.Should().NotContain(expectedSurvivor);
    }

    [Theory]
    [InlineData("4242424242424242")]
    [InlineData("5555555555554444")]
    [InlineData("378282246310005")]
    [InlineData("6011111111111117")]
    public void Redact_KnownTestCardsPassLuhn_AreRedacted(string panString)
    {
        var input = $"plate {panString} stored";

        var result = _sut.Redact(input);

        result.RedactedText.Should().Contain(
            "<<CARD_1>>",
            "{0} is a Luhn-valid PAN and must be substituted",
            panString);
        result.Mapping.Values.Should().Contain(panString);
    }

    // ------- URL trailing-punctuation peel-off (data integrity) -------
    [Fact]
    public void Redact_UrlTrailingDot_PunctuationStaysInText()
    {
        // Pre-fix bug: regex was greedy, swallowed the period, Restore
        // returned the URL with a stray "." appended, breaking copy/paste.
        var result = _sut.Redact("перейди на https://example.com.");

        result.RedactedText.Should().Be("перейди на <<URL_1>>.");
        result.Mapping["<<URL_1>>"].Should().Be("https://example.com");
    }

    [Fact]
    public void Redact_UrlTrailingComma_PunctuationStaysInText()
    {
        var result = _sut.Redact("https://example.com, далі текст");

        result.RedactedText.Should().Be("<<URL_1>>, далі текст");
        result.Mapping["<<URL_1>>"].Should().Be("https://example.com");
    }

    [Fact]
    public void Redact_UrlInsideParens_ParenStaysInText()
    {
        var result = _sut.Redact("(see https://example.com) for details");

        result.RedactedText.Should().Be("(see <<URL_1>>) for details");
        result.Mapping["<<URL_1>>"].Should().Be("https://example.com");
    }

    [Fact]
    public void Redact_UrlWithQueryAndPath_NotTrimmed()
    {
        var result = _sut.Redact("see https://example.com/api?q=1&n=2 then ok");

        result.RedactedText.Should().Be("see <<URL_1>> then ok");
        result.Mapping["<<URL_1>>"].Should().Be("https://example.com/api?q=1&n=2");
    }
}
