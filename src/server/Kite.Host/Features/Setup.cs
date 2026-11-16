using Kite.Accounting;
using Kite.Host.Infrastructure;
using Kite.Identity;
using Kite.Persistence;
using Kite.Platform;
using Kite.Tax;

namespace Kite.Host.Features;

public sealed record SetupBusiness(
    string LegalNameEn, string LegalNameAr, string? TradeNameEn, string? TradeNameAr, string? CrNumber, string? VatNumber,
    string? AddressEn, string? AddressAr, string? Phone, string? Email, int FiscalYearStartMonth = 1, bool PricesIncludeTax = true);

public sealed record SetupBranch(string Code, string NameEn, string NameAr);

public sealed record SetupOwner(string Username, string DisplayNameEn, string? DisplayNameAr, string Password, string PreferredLanguage = "en");

public sealed record SetupRequest(
    string SetupCode, SetupBusiness Business, SetupBranch HeadOffice, SetupOwner Owner,
    string ServerRole = "combined", Dictionary<string, bool>? Modules = null);

/// <summary>
/// Business setup wizard (blueprint 26.2). One transaction creates the business, head-office
/// branch, this server's identity, fiscal periods, number series, tax codes (flagged for
/// accountant review), chart of accounts, role templates, owner account and module switches,
/// with audit rows and sync events.
/// </summary>
public static class SetupEndpoints
{
    public static readonly IReadOnlyList<(string DocType, string Prefix)> NumberSeries =
    [
        ("journal", "JV"), ("sale_invoice", "INV"), ("credit_note", "CN"), ("repair_job", "R"),
        ("trade_in", "TI"), ("purchase_order", "PO"), ("goods_receipt", "GR"), ("customer_receipt", "RC"),
        ("shift", "SH"), ("stock_adjustment", "ADJ"), ("stock_count", "CNT"), ("device_inspection", "INS"), ("refurb_order", "RF"),
    ];

    public static void Map(WebApplication app)
    {
        app.MapGet("/api/v1/setup/status", (ServerContext server) =>
            Results.Ok(new { configured = server.IsConfigured, role = server.IsConfigured ? server.Role : null }))
            .AllowEveryone();

        app.MapPost("/api/v1/setup", async (SetupRequest req, HttpContext http, KiteDb db, ServerContext server,
            SetupCodeStore codes, IPasswordHasher hasher, ModuleStateCache modules, IClock clock, PkiService pki, LicenceService licence, CancellationToken ct) =>
        {
            if (server.IsConfigured)
                throw new KiteException(ErrorCodes.SetupAlreadyDone, "This server is already set up.", 409);
            if (!codes.Matches(req.SetupCode))
                throw new KiteException(ErrorCodes.SetupCodeInvalid, "The setup code is not correct. It is shown on the server screen and in its data folder.", 403, "setupCode");
            Validate(req);
            PasswordPolicy.Check(req.Owner.Password, req.Owner.Username);
            var moduleStates = ModuleCatalog.Apply(ModuleCatalog.Defaults(), req.Modules ?? []);

            var businessId = Ids.New();
            var branchId = Ids.New();
            var serverId = Ids.New();
            var ownerId = Ids.New();
            var installationId = MachineId.New();
            var tz = BusinessTimeZone();
            var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.UtcNow, tz).DateTime);
            var periods = FiscalCalendar.YearContaining(today, req.Business.FiscalYearStartMonth);
            var fiscalYear = periods[0].FiscalYear;
            var passwordHash = hasher.Hash(req.Owner.Password);
            var actor = new ActorContext(ownerId, branchId, null, http.Connection.RemoteIpAddress?.ToString());

            try
            {
                await db.InTransactionAsync(AccessScope.System, actor, async t =>
                {
                    if (await t.ExecuteScalarAsync<long>("SELECT count(*) FROM core.business") > 0)
                        throw new KiteException(ErrorCodes.SetupAlreadyDone, "This server is already set up.", 409);

                    var b = req.Business;
                    await t.ExecuteAsync("""
                        INSERT INTO core.business (id, legal_name_en, legal_name_ar, trade_name_en, trade_name_ar, cr_number, vat_number,
                            address_en, address_ar, phone, email, fiscal_year_start_month, prices_include_tax, created_by)
                        VALUES (@businessId, @LegalNameEn, @LegalNameAr, @TradeNameEn, @TradeNameAr, @CrNumber, @VatNumber,
                            @AddressEn, @AddressAr, @Phone, @Email, @FiscalYearStartMonth, @PricesIncludeTax, @ownerId)
                        """, new { businessId, ownerId, b.LegalNameEn, b.LegalNameAr, b.TradeNameEn, b.TradeNameAr, b.CrNumber, b.VatNumber,
                                   b.AddressEn, b.AddressAr, b.Phone, b.Email, b.FiscalYearStartMonth, b.PricesIncludeTax });

                    var h = req.HeadOffice;
                    await t.ExecuteAsync("""
                        INSERT INTO core.branch (id, business_id, code, name_en, name_ar, is_head_office, created_by)
                        VALUES (@branchId, @businessId, @Code, @NameEn, @NameAr, true, @ownerId)
                        """, new { branchId, businessId, ownerId, Code = h.Code.ToUpperInvariant(), h.NameEn, h.NameAr });

                    await t.ExecuteAsync("""
                        INSERT INTO core.server (id, business_id, branch_id, role, name, is_self, status, installation_id)
                        VALUES (@serverId, @businessId, @branchId, @role, @name, true, 'active', @installationId)
                        """, new { serverId, businessId, branchId, role = req.ServerRole, name = Environment.MachineName, installationId });
                    await t.ExecuteAsync("INSERT INTO lic.control_state (checkin_key) VALUES (@k) ON CONFLICT (id) DO NOTHING",
                        new { k = BranchLinkService.NewCheckinKey() });
                    server.Set(serverId, businessId, branchId, req.ServerRole, installationId);

                    await t.ExecuteAsync("""
                        INSERT INTO core.warehouse (id, business_id, branch_id, code, name_en, name_ar, kind, created_by)
                        VALUES (@id, @businessId, @branchId, 'SHOP', 'Shop floor', 'صالة العرض', 'shop', @ownerId)
                        """, new { id = Ids.New(), businessId, branchId, ownerId });

                    foreach (var p in periods)
                        await t.ExecuteAsync("""
                            INSERT INTO acct.fiscal_period (id, business_id, branch_id, fiscal_year, period_no, starts_on, ends_on)
                            VALUES (@id, @businessId, NULL, @FiscalYear, @PeriodNo, @starts::date, @ends::date)
                            """, new { id = Ids.New(), businessId, p.FiscalYear, p.PeriodNo,
                                       starts = p.StartsOn.ToDateTime(TimeOnly.MinValue), ends = p.EndsOn.ToDateTime(TimeOnly.MinValue) });

                    foreach (var (docType, prefix) in NumberSeries)
                        await t.ExecuteAsync("""
                            INSERT INTO core.number_series (id, business_id, branch_id, doc_type, prefix, fiscal_year, created_by)
                            VALUES (@id, @businessId, @branchId, @docType, @prefix, @fiscalYear, @ownerId)
                            """, new { id = Ids.New(), businessId, branchId, docType, prefix = $"{prefix}-{h.Code.ToUpperInvariant()}-{fiscalYear}-", fiscalYear, ownerId });

                    foreach (var code in OmanTaxTemplate.Codes)
                    {
                        var codeId = Ids.New();
                        await t.ExecuteAsync("""
                            INSERT INTO tax.tax_code (id, business_id, code, name_en, name_ar, kind, status, created_by)
                            VALUES (@codeId, @businessId, @Code, @NameEn, @NameAr, @kind, @status, @ownerId)
                            """, new { codeId, businessId, ownerId, code.Code, code.NameEn, code.NameAr,
                                       kind = ToDbKind(code.Kind), status = code.ActiveByDefault ? "active" : "inactive" });
                        foreach (var r in code.Rates)
                            await t.ExecuteAsync("""
                                INSERT INTO tax.tax_rate (id, tax_code_id, rate_percent, effective_from, effective_to, created_by)
                                VALUES (@id, @codeId, @RatePercent, @from::date, @to::date, @ownerId)
                                """, new { id = Ids.New(), codeId, r.RatePercent, ownerId,
                                           from = r.EffectiveFrom.ToDateTime(TimeOnly.MinValue), to = r.EffectiveTo?.ToDateTime(TimeOnly.MinValue) });
                    }

                    var accountIds = new Dictionary<string, Guid>(StringComparer.Ordinal);
                    foreach (var a in ChartTemplate.Accounts)
                    {
                        var id = Ids.New();
                        accountIds[a.Code] = id;
                        await t.ExecuteAsync("""
                            INSERT INTO acct.account (id, business_id, code, name_en, name_ar, type, parent_id, is_postable, is_control, created_by)
                            VALUES (@id, @businessId, @Code, @NameEn, @NameAr, @type, @parent, @IsPostable, @IsControl, @ownerId)
                            """, new { id, businessId, ownerId, a.Code, a.NameEn, a.NameAr, type = a.Type.ToString().ToLowerInvariant(),
                                       parent = a.ParentCode is null ? (Guid?)null : accountIds[a.ParentCode], a.IsPostable, a.IsControl });
                    }

                    Guid ownerRoleId = Guid.Empty;
                    foreach (var role in RoleTemplates.All)
                    {
                        var roleId = Ids.New();
                        if (role.Code == RoleTemplates.OwnerCode) ownerRoleId = roleId;
                        await t.ExecuteAsync("""
                            INSERT INTO iam.role (id, business_id, code, name_en, name_ar, is_system, discount_limit_percent, discount_limit_amount, created_by)
                            VALUES (@roleId, @businessId, @Code, @NameEn, @NameAr, true, @DiscountLimitPercent, @DiscountLimitAmount, @ownerId)
                            """, new { roleId, businessId, ownerId, role.Code, role.NameEn, role.NameAr, role.DiscountLimitPercent, role.DiscountLimitAmount });
                        foreach (var perm in role.Permissions)
                            await t.ExecuteAsync("INSERT INTO iam.role_permission (role_id, permission_code) VALUES (@roleId, @perm)", new { roleId, perm });
                    }

                    var o = req.Owner;
                    await t.ExecuteAsync("""
                        INSERT INTO iam."user" (id, business_id, username, display_name_en, display_name_ar, password_hash,
                                                preferred_language, default_branch_id, created_by)
                        VALUES (@ownerId, @businessId, @username, @DisplayNameEn, @DisplayNameAr, @passwordHash, @lang, @branchId, @ownerId)
                        """, new { ownerId, businessId, branchId, username = o.Username.ToLowerInvariant(), o.DisplayNameEn, o.DisplayNameAr,
                                   passwordHash, lang = o.PreferredLanguage == "ar" ? "ar" : "en" });
                    await t.ExecuteAsync("""
                        INSERT INTO iam.user_branch_role (id, user_id, branch_id, role_id, created_by)
                        VALUES (@id, @ownerId, NULL, @ownerRoleId, @ownerId)
                        """, new { id = Ids.New(), ownerId, ownerRoleId });

                    foreach (var (code, enabled) in moduleStates)
                        await t.ExecuteAsync("""
                            INSERT INTO core.module_setting (id, business_id, branch_id, module_code, enabled, created_by)
                            VALUES (@id, @businessId, NULL, @code, @enabled, @ownerId)
                            """, new { id = Ids.New(), businessId, code, enabled, ownerId });

                    await Phase4Seed.EnsureAsync(t, businessId);

                    await t.AuditAsync(new AuditEntry("setup.completed", "business", businessId,
                        After: Json.Obj(new { b.LegalNameEn, b.LegalNameAr, b.VatNumber, headOffice = h.Code, req.ServerRole, owner = o.Username })));
                    await t.PublishAsync("core.business_created", "business", businessId, 1,
                        Json.Obj(new { b.LegalNameEn, b.LegalNameAr, b.TradeNameEn, b.TradeNameAr, b.CrNumber, b.VatNumber, b.FiscalYearStartMonth, b.PricesIncludeTax }));
                    await t.PublishAsync("core.branch_created", "branch", branchId, 1,
                        Json.Obj(new { code = h.Code.ToUpperInvariant(), h.NameEn, h.NameAr, isHeadOffice = true }), branchId);
                    await t.PublishAsync("iam.user_created", "user", ownerId, 1,
                        Json.Obj(new { username = o.Username.ToLowerInvariant(), o.DisplayNameEn, o.DisplayNameAr, roles = new[] { RoleTemplates.OwnerCode } }));
                    await t.PublishAsync("core.modules_configured", "business", businessId, 1, Json.Obj(new { modules = moduleStates }));
                }, ct);
            }
            catch
            {
                server.Reset();
                throw;
            }

            codes.Consume();
            modules.Invalidate();
            licence.Invalidate();
            // The business CA and the sync certificate (blueprint 18.1). A failure here does not undo the setup:
            // startup tries again, and branch registration waits until it works.
            try { await pki.EnsureAsync(ct); }
            catch (Exception e) when (e is not OperationCanceledException)
            { http.RequestServices.GetRequiredService<ILogger<PkiService>>().LogError(e, "Could not create the business certificate authority"); }
            return Results.Created("/api/v1/setup/status", new { businessId, branchId, ownerUserId = ownerId, fiscalYear, installationId });
        }).AllowEveryone();
    }

    private static void Validate(SetupRequest req)
    {
        Require(req.Business is not null, "business", "Business details are required.");
        Require(req.HeadOffice is not null, "headOffice", "Head-office branch is required.");
        Require(req.Owner is not null, "owner", "Owner account is required.");
        Require(!string.IsNullOrWhiteSpace(req.Business!.LegalNameEn), "business.legalNameEn", "Legal name in English is required.");
        Require(!string.IsNullOrWhiteSpace(req.Business.LegalNameAr), "business.legalNameAr", "Legal name in Arabic is required.");
        Require(req.Business.FiscalYearStartMonth is >= 1 and <= 12, "business.fiscalYearStartMonth", "Fiscal year start month must be 1 to 12.");
        Require(System.Text.RegularExpressions.Regex.IsMatch(req.HeadOffice!.Code ?? "", "^[A-Za-z0-9-]{2,12}$"), "headOffice.code", "Branch code must be 2 to 12 letters, digits or hyphens.");
        Require(!string.IsNullOrWhiteSpace(req.HeadOffice.NameEn) && !string.IsNullOrWhiteSpace(req.HeadOffice.NameAr), "headOffice", "Branch names in English and Arabic are required.");
        Require(System.Text.RegularExpressions.Regex.IsMatch(req.Owner!.Username ?? "", "^[a-zA-Z0-9._-]{3,40}$"), "owner.username", "Username must be 3 to 40 letters, digits, dots, hyphens or underscores.");
        Require(!string.IsNullOrWhiteSpace(req.Owner.DisplayNameEn), "owner.displayNameEn", "Owner name is required.");
        Require(req.ServerRole is "combined" or "main", "serverRole", "Server role must be combined (single branch) or main (head office).");
    }

    private static void Require(bool ok, string field, string message)
    {
        if (!ok) throw new KiteException(ErrorCodes.Validation, message, 400, field);
    }

    private static string ToDbKind(TaxKind k) => k switch
    {
        TaxKind.Standard => "standard",
        TaxKind.Zero => "zero",
        TaxKind.Exempt => "exempt",
        TaxKind.OutOfScope => "out_of_scope",
        TaxKind.Margin => "margin",
        _ => throw new ArgumentOutOfRangeException(nameof(k)),
    };

    public static TimeZoneInfo BusinessTimeZone()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Asia/Muscat"); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.CreateCustomTimeZone("Asia/Muscat", TimeSpan.FromHours(4), "Asia/Muscat", "Asia/Muscat"); }
    }
}
