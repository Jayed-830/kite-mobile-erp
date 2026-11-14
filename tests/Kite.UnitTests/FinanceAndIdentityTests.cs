using System.Security.Cryptography;
using System.Text;
using Kite.Accounting;
using Kite.Identity;
using Kite.Platform;
using Kite.Tax;
using Xunit;

namespace Kite.UnitTests;

public class TaxEngineTests
{
    [Fact]
    public void Tax_inclusive_price_extracts_vat()
    {
        var line = TaxEngine.ComputeLine(10.5m, 1, 0, 5m, pricesIncludeTax: true, Currency.OMR);
        Assert.Equal(10.500m, line.Gross.Amount);
        Assert.Equal(0.500m, line.Tax.Amount);
        Assert.Equal(10.000m, line.Net.Amount);
    }

    [Fact]
    public void Tax_exclusive_price_adds_vat()
    {
        var line = TaxEngine.ComputeLine(10m, 1, 0, 5m, pricesIncludeTax: false, Currency.OMR);
        Assert.Equal(0.500m, line.Tax.Amount);
        Assert.Equal(10.500m, line.Gross.Amount);
    }

    [Fact]
    public void Net_plus_tax_always_equals_gross_after_rounding()
    {
        // 1.005 × 5 / 105 = 0.047857... -> 0.048, net 0.957
        var line = TaxEngine.ComputeLine(1.005m, 1, 0, 5m, true, Currency.OMR);
        Assert.Equal(0.048m, line.Tax.Amount);
        Assert.Equal(line.Gross, line.Net + line.Tax);
    }

    [Fact]
    public void Quantity_and_discount_are_applied_before_tax()
    {
        var line = TaxEngine.ComputeLine(2.1m, 3, 0.3m, 5m, true, Currency.OMR);
        Assert.Equal(6.000m, line.Gross.Amount);
        Assert.Equal(0.286m, line.Tax.Amount);
    }

    [Fact]
    public void Discount_larger_than_the_line_is_refused()
    {
        Assert.Throws<KiteException>(() => TaxEngine.ComputeLine(1m, 1, 2m, 5m, true, Currency.OMR));
    }

    [Fact]
    public void Margin_vat_is_charged_on_the_profit_only()
    {
        // Margin 21.000 × 5 / 105 = 1.000 (blueprint 12.3 formula).
        Assert.Equal(1.000m, TaxEngine.MarginVat(Money.Omr(105m), Money.Omr(84m), 5m).Amount);
        Assert.Equal(0m, TaxEngine.MarginVat(Money.Omr(80m), Money.Omr(84m), 5m).Amount);
    }

    [Fact]
    public void Rates_are_chosen_by_date_and_overlaps_are_refused()
    {
        var code = new TaxCodeDefinition("T", "T", "T", TaxKind.Standard, true,
            [new(5m, new DateOnly(2021, 4, 16), new DateOnly(2027, 1, 1)), new(7m, new DateOnly(2027, 1, 1), null)]);
        Assert.Equal(5m, code.RateOn(new DateOnly(2026, 12, 31)));
        Assert.Equal(7m, code.RateOn(new DateOnly(2027, 1, 1)));
        Assert.Throws<KiteException>(() => code.RateOn(new DateOnly(2020, 1, 1)));
        var overlapping = code with { Rates = [new(5m, new DateOnly(2021, 1, 1), null), new(7m, new DateOnly(2027, 1, 1), null)] };
        Assert.Throws<KiteException>(() => overlapping.RateOn(new DateOnly(2027, 6, 1)));
    }

    [Fact]
    public void Oman_template_seeds_five_percent_standard_vat_from_april_2021_and_keeps_margin_scheme_off()
    {
        var std = OmanTaxTemplate.Codes.Single(c => c.Code == "VAT-STD");
        Assert.Equal(5m, std.RateOn(new DateOnly(2026, 11, 14)));
        Assert.Throws<KiteException>(() => std.RateOn(new DateOnly(2021, 4, 15)));
        Assert.False(OmanTaxTemplate.Codes.Single(c => c.Kind == TaxKind.Margin).ActiveByDefault);
    }
}

public class AccountingTests
{
    [Fact]
    public void A_balanced_journal_validates_and_its_reversal_mirrors_it()
    {
        var j = new JournalDraft(new DateOnly(2026, 11, 14), "sale", Guid.NewGuid(), "sale", Currency.OMR)
            .Debit("1100", 10.5m).Credit("4100", 10m).Credit("2200", 0.5m);
        j.Validate();
        var r = j.Reverse(new DateOnly(2026, 11, 15));
        r.Validate();
        Assert.Equal("sale.reversal", r.PostingKind);
        Assert.Equal(j.TotalDebit, r.TotalCredit);
        Assert.Equal(0.5m, r.Lines.Single(l => l.AccountCode == "2200").Debit);
    }

    [Fact]
    public void Unbalanced_or_single_line_journals_are_refused()
    {
        var oneLine = new JournalDraft(new DateOnly(2026, 1, 1), "x", Guid.NewGuid(), "x", Currency.OMR).Debit("1100", 1m);
        Assert.Throws<KiteException>(oneLine.Validate);
        var unbalanced = new JournalDraft(new DateOnly(2026, 1, 1), "x", Guid.NewGuid(), "x", Currency.OMR).Debit("1100", 1m).Credit("4100", 0.999m);
        Assert.Throws<KiteException>(unbalanced.Validate);
        Assert.Throws<KiteException>(() => unbalanced.Debit("1100", -1m));
    }

    [Fact]
    public void Chart_template_has_unique_codes_valid_parents_and_flags_control_accounts()
    {
        var codes = ChartTemplate.Accounts.Select(a => a.Code).ToArray();
        Assert.Equal(codes.Length, codes.Distinct().Count());
        foreach (var a in ChartTemplate.Accounts.Where(a => a.ParentCode is not null))
            Assert.True(Array.IndexOf(codes, a.ParentCode) < Array.IndexOf(codes, a.Code), $"{a.Code} parent must come first");
        Assert.True(ChartTemplate.Accounts.Single(a => a.Code == "1300").IsControl);
        Assert.True(ChartTemplate.Accounts.Single(a => a.Code == "2200").IsControl);
        Assert.All(ChartTemplate.Accounts, a => Assert.False(string.IsNullOrWhiteSpace(a.NameAr)));
    }

    [Theory]
    [InlineData(2026, 11, 14, 1, 2026, 11)]
    [InlineData(2026, 2, 10, 4, 2025, 11)]
    [InlineData(2026, 4, 1, 4, 2026, 1)]
    public void Fiscal_calendar_places_dates_in_the_right_year_and_period(int y, int m, int d, int startMonth, int fiscalYear, int period)
    {
        var periods = FiscalCalendar.YearContaining(new DateOnly(y, m, d), startMonth);
        Assert.Equal(12, periods.Count);
        Assert.All(periods, p => Assert.Equal(fiscalYear, p.FiscalYear));
        var date = new DateOnly(y, m, d);
        Assert.Equal(period, periods.Single(p => p.StartsOn <= date && date <= p.EndsOn).PeriodNo);
        for (var i = 1; i < periods.Count; i++) Assert.Equal(periods[i - 1].EndsOn.AddDays(1), periods[i].StartsOn);
    }
}

public class IdentityTests
{
    private readonly Argon2PasswordHasher _hasher = new(Argon2Options.ForTests);

    [Fact]
    public void Argon2id_hashes_verify_and_use_the_phc_format()
    {
        var hash = _hasher.Hash("correct horse battery");
        Assert.StartsWith("$argon2id$v=19$m=1024,t=1,p=1$", hash, StringComparison.Ordinal);
        Assert.True(_hasher.Verify("correct horse battery", hash));
        Assert.False(_hasher.Verify("correct horse batterY", hash));
        Assert.NotEqual(hash, _hasher.Hash("correct horse battery"));
    }

    [Fact]
    public void Hashes_made_with_older_settings_are_flagged_for_rehash()
    {
        var hash = _hasher.Hash("secret-secret");
        Assert.False(_hasher.NeedsRehash(hash));
        Assert.True(new Argon2PasswordHasher(new Argon2Options(MemoryKiB: 2048, Iterations: 1, Parallelism: 1)).NeedsRehash(hash));
        Assert.False(_hasher.Verify("x", "not-a-hash"));
    }

    [Theory]
    // RFC 6238 appendix B, SHA-1 secret "12345678901234567890"; 6-digit codes are the last 6 digits.
    [InlineData(59L, "287082")]
    [InlineData(1111111109L, "081804")]
    [InlineData(1234567890L, "005924")]
    [InlineData(2000000000L, "279037")]
    public void Totp_matches_rfc_6238_test_vectors(long unixSeconds, string expected)
    {
        var secret = Encoding.ASCII.GetBytes("12345678901234567890");
        Assert.Equal(expected, Totp.Code(secret, DateTimeOffset.FromUnixTimeSeconds(unixSeconds)));
    }

    [Fact]
    public void Totp_accepts_one_step_of_drift_but_not_more()
    {
        var secret = Totp.NewSecret();
        var now = DateTimeOffset.FromUnixTimeSeconds(1_763_114_400);
        Assert.True(Totp.Verify(secret, Totp.Code(secret, now.AddSeconds(-30)), now));
        Assert.True(Totp.Verify(secret, Totp.Code(secret, now.AddSeconds(30)), now));
        Assert.False(Totp.Verify(secret, Totp.Code(secret, now.AddSeconds(-90)), now));
        Assert.False(Totp.Verify(secret, "12a456", now));
    }

    [Fact]
    public void Totp_reports_which_time_step_a_code_belongs_to_so_it_can_be_refused_next_time()
    {
        // RFC 6238 test secret, so the codes for neighbouring steps are fixed and distinct.
        var secret = System.Text.Encoding.ASCII.GetBytes("12345678901234567890");
        var now = DateTimeOffset.FromUnixTimeSeconds(1_763_114_400);
        var step = now.ToUnixTimeSeconds() / 30;
        Assert.Equal(step, Totp.MatchStep(secret, Totp.CodeForStep(secret, step), now));
        Assert.Equal(step - 1, Totp.MatchStep(secret, Totp.CodeForStep(secret, step - 1), now));
        Assert.Equal(step + 1, Totp.MatchStep(secret, Totp.CodeForStep(secret, step + 1), now));
        Assert.Null(Totp.MatchStep(secret, Totp.CodeForStep(secret, step + 2), now));
        Assert.Null(Totp.MatchStep(secret, "", now));
    }

    [Fact]
    public void Base32_round_trips_and_builds_an_enrolment_uri()
    {
        var data = RandomNumberGenerator.GetBytes(20);
        Assert.Equal(data, Base32.Decode(Base32.Encode(data)));
        Assert.Equal("MZXW6YTBOI", Base32.Encode("foobar"u8));
        Assert.StartsWith("otpauth://totp/KITE%20MOBILE%20ERP:owner?secret=", Totp.EnrolmentUri(data, "owner"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(4, null)]
    [InlineData(5, 15)]
    [InlineData(6, 30)]
    [InlineData(7, 60)]
    [InlineData(30, 1440)]
    public void Lockout_starts_at_five_failures_and_doubles_up_to_a_day(int failures, int? minutes)
    {
        Assert.Equal(minutes, new LockoutPolicy().LockFor(failures)?.TotalMinutes is { } t ? (int)t : null);
    }

    [Fact]
    public void Password_and_pin_policies_reject_weak_values()
    {
        Assert.Throws<KiteException>(() => PasswordPolicy.Check("short", "owner"));
        Assert.Throws<KiteException>(() => PasswordPolicy.Check("owner-is-me-123", "owner"));
        Assert.Throws<KiteException>(() => PasswordPolicy.Check("aaaaaaaaaaaa", "x"));
        PasswordPolicy.Check("Muscat-Branch-2026", "owner");
        Assert.Throws<KiteException>(() => PasswordPolicy.CheckPin("1111"));
        Assert.Throws<KiteException>(() => PasswordPolicy.CheckPin("12a4"));
        PasswordPolicy.CheckPin("4826");
    }

    [Fact]
    public void Secrets_encrypted_with_aes_gcm_decrypt_and_tampering_is_detected()
    {
        var protector = new AesGcmSecretProtector(RandomNumberGenerator.GetBytes(32));
        var secret = Totp.NewSecret();
        var sealedBytes = protector.Protect(secret);
        Assert.Equal(secret, protector.Unprotect(sealedBytes));
        sealedBytes[^1] ^= 0x01;
        Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect(sealedBytes));
    }

    [Fact]
    public void Tokens_are_random_and_only_their_hash_is_stored()
    {
        var t1 = Tokens.New();
        Assert.NotEqual(t1, Tokens.New());
        Assert.Equal(32, Tokens.Hash(t1).Length);
        Assert.DoesNotContain('+', t1);
    }
}
