using Kite.Accounting;
using Kite.Host.Features;
using Kite.Persistence;
using Kite.Platform;
using Kite.Sales;

namespace Kite.Host.Infrastructure;

public sealed class Supplier
{
    public Guid Id { get; set; }
    public string Number { get; set; } = "";
    public string NameEn { get; set; } = "";
    public string NameAr { get; set; } = "";
    public string? VatNumber { get; set; }
    public string? CrNumber { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string? Address { get; set; }
    public string? ContactName { get; set; }
    public int TermsDays { get; set; }
    public string Status { get; set; } = "active";
    public Guid? MergedIntoId { get; set; }
    public Guid? OriginBranchId { get; set; }
    public Guid? OriginServerId { get; set; }
    public int Version { get; set; }
    public long SyncSeq { get; set; }

    public const string Select = """
        SELECT s.id, s.number, s.name_en AS NameEn, s.name_ar AS NameAr, s.vat_number AS VatNumber, s.cr_number AS CrNumber, s.phone_e164 AS Phone, s.email,
               s.address, s.contact_name AS ContactName, s.terms_days AS TermsDays, s.status, s.merged_into_id AS MergedIntoId, s.origin_branch_id AS OriginBranchId,
               s.origin_server_id AS OriginServerId, s.version, s.sync_seq AS SyncSeq
          FROM purch.supplier s
        """;
}

public sealed record SupplierInput(string NameEn, string NameAr, string? VatNumber, string? CrNumber, string? Phone, string? Email, string? Address,
    string? ContactName, int? TermsDays, int? Version = null);

// ------------------------------------------------------------------------------------------------ wire formats (sync port)
public sealed record SupplierSnapshot(Guid Id, string Number, string NameEn, string NameAr, string? VatNumber, string? CrNumber, string? Phone, string? Email,
    string? Address, string? ContactName, int TermsDays, string Status, Guid? MergedIntoId, Guid? OriginBranchId, int Version, long Seq);
public sealed record SupplierEntryPush(Guid Id, Guid BranchId, Guid SupplierId, string Kind, decimal Amount, DateOnly DocDate, DateOnly? DueDate, string Number,
    string SourceDocType, Guid SourceDocId, string? Note, DateTimeOffset CreatedAt, Guid CreatedBy);
public sealed record SupplierPushRequest(List<SupplierSnapshot> Suppliers, List<SupplierEntryPush> Entries);
public sealed record SupplierPushResponse(List<SupplierSnapshot> Suppliers, List<Guid> AckedEntries, List<string> Errors);

/// <summary>
/// Suppliers (16.1): business-wide records kept by head office. A branch server that is offline may add a provisional
/// supplier; head office approves it or merges it into an existing one, and the outcome reaches every branch with the
/// stream. Each branch writes its own supplier ledger lines; head office holds all of them for balances and statements.
/// </summary>
public static class SupplierCore
{
    public static Task<Supplier?> LoadAsync(DbTx t, Guid id, bool forUpdate = false) =>
        t.QuerySingleOrDefaultAsync<Supplier>(Supplier.Select + " WHERE s.id = @id" + (forUpdate ? " FOR UPDATE" : ""), new { id });

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    public static (string NameEn, string NameAr, string? Phone, int Terms) Validate(SupplierInput r, int defaultTerms)
    {
        var en = Clean(r.NameEn) ?? throw new KiteException(ErrorCodes.Validation, "Name in English is required.", 400, "nameEn");
        var ar = Clean(r.NameAr) ?? throw new KiteException(ErrorCodes.Validation, "Name in Arabic is required.", 400, "nameAr");
        var phone = Clean(r.Phone);
        if (phone is not null && !System.Text.RegularExpressions.Regex.IsMatch(phone, @"^\+[1-9][0-9]{6,14}$"))
            throw new KiteException(ErrorCodes.Validation, "Phone is in international form, e.g. +96891234567.", 400, "phone");
        var terms = r.TermsDays ?? defaultTerms;
        if (terms is < 0 or > 365) throw new KiteException(ErrorCodes.Validation, "Payment terms are 0 to 365 days.", 400, "termsDays");
        return (en, ar, phone, terms);
    }

    /// <summary>Adds a supplier: active and numbered business-wide on head office; provisional and numbered by the branch on a branch server.</summary>
    public static async Task<(Guid Id, string Number, string Status)> CreateAsync(DbTx t, SupplierInput r, Guid branchId, Guid by)
    {
        var (en, ar, phone, terms) = Validate(r, 30);
        var biz = await BusinessInfo.LoadAsync(t);
        var headOffice = t.Server.IsHeadOffice;
        var number = headOffice
            ? await DocNumbers.NextAsync(t, biz.Id, null, "supplier", 2000, "S")
            : await DocNumbers.NextAsync(t, biz.Id, branchId, "supplier_provisional", await FiscalPeriods.FiscalYearOfAsync(t, biz.Id, BusinessDay.Today(biz.TimeZone, t.Clock)), "SP");
        var id = t.NewId();
        var status = headOffice ? "active" : "provisional";
        await t.ExecuteAsync("""
            INSERT INTO purch.supplier (id, business_id, number, name_en, name_ar, vat_number, cr_number, phone_e164, email, address, contact_name, terms_days, status,
                                        origin_branch_id, approved_at, approved_by, created_by, origin_server_id)
            VALUES (@id, @b, @number, @en, @ar, @vat, @cr, @phone, @email, @address, @contact, @terms, @status, @branchId,
                    CASE WHEN @headOffice THEN now() END, CASE WHEN @headOffice THEN @by END, @by, (SELECT id FROM core.server WHERE is_self))
            """, new { id, b = biz.Id, number, en, ar, vat = Clean(r.VatNumber), cr = Clean(r.CrNumber), phone, email = Clean(r.Email), address = Clean(r.Address),
                       contact = Clean(r.ContactName), terms, status, branchId, headOffice, by });
        await t.AuditAsync(new AuditEntry("purch.supplier_created", "supplier", id, After: Json.Obj(new { number, en, ar, status }), BranchId: branchId));
        await t.PublishAsync("purch.supplier_created", "supplier", id, 1, Json.Obj(new { number, en, ar, status }), branchId);
        return (id, number, status);
    }

    public static async Task UpdateAsync(DbTx t, Supplier s, SupplierInput r, Guid by)
    {
        if (s.Status == "merged") throw new KiteException(ErrorCodes.SupplierState, $"{s.Number} was merged; change the supplier it was merged into.", 409);
        if (!t.Server.IsHeadOffice && (s.Status != "provisional" || s.OriginServerId != t.Server.ServerId))
            throw new KiteException(ErrorCodes.NotHeadOffice, "Suppliers are changed at head office; a branch changes only the provisional suppliers it added.", 409);
        var (en, ar, phone, terms) = Validate(r, s.TermsDays);
        var n = await t.ExecuteAsync("""
            UPDATE purch.supplier SET name_en = @en, name_ar = @ar, vat_number = @vat, cr_number = @cr, phone_e164 = @phone, email = @email, address = @address,
                   contact_name = @contact, terms_days = @terms, updated_by = @by
             WHERE id = @id AND version = @Version
            """, new { id = s.Id, en, ar, vat = Clean(r.VatNumber), cr = Clean(r.CrNumber), phone, email = Clean(r.Email), address = Clean(r.Address),
                       contact = Clean(r.ContactName), terms, by, r.Version });
        if (n == 0) throw new KiteException(ErrorCodes.VersionMismatch, "Someone else changed this supplier. Reload and try again.", 412, "version");
        await t.AuditAsync(new AuditEntry("purch.supplier_changed", "supplier", s.Id, Json.Obj(new { s.NameEn, s.NameAr, s.TermsDays }), Json.Obj(new { en, ar, terms })));
    }

    /// <summary>Head office approves a provisional supplier (16.1).</summary>
    public static async Task ApproveAsync(DbTx t, Supplier s, Guid by)
    {
        if (!t.Server.IsHeadOffice) throw new KiteException(ErrorCodes.NotHeadOffice, "Head office approves suppliers.", 409);
        if (s.Status != "provisional") throw new KiteException(ErrorCodes.SupplierState, $"{s.Number} is {s.Status}, not waiting for approval.", 409);
        await t.ExecuteAsync("UPDATE purch.supplier SET status = 'active', approved_at = now(), approved_by = @by, updated_by = @by WHERE id = @id", new { id = s.Id, by });
        await t.AuditAsync(new AuditEntry("purch.supplier_approved", "supplier", s.Id, After: Json.Obj(new { s.Number, s.NameEn })));
        await t.PublishAsync("purch.supplier_approved", "supplier", s.Id, s.Version + 1, Json.Obj(new { s.Number }));
    }

    /// <summary>Head office merges a duplicate into an active supplier: its lines stay as they were and count for the survivor.</summary>
    public static async Task MergeAsync(DbTx t, Supplier s, Supplier into, string? reason, Guid by)
    {
        if (!t.Server.IsHeadOffice) throw new KiteException(ErrorCodes.NotHeadOffice, "Head office merges suppliers.", 409);
        if (s.Id == into.Id) throw new KiteException(ErrorCodes.Validation, "Choose another supplier to merge into.", 400, "intoId");
        if (s.Status == "merged") throw new KiteException(ErrorCodes.SupplierState, $"{s.Number} is already merged.", 409);
        if (into.Status != "active") throw new KiteException(ErrorCodes.SupplierState, $"{into.Number} is {into.Status}; merge only into an active supplier.", 409, "intoId");
        if (await t.ExecuteScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM purch.supplier WHERE merged_into_id = @id)", new { id = s.Id }))
            throw new KiteException(ErrorCodes.SupplierState, $"Other suppliers were merged into {s.Number}; it stays.", 409);
        await t.ExecuteAsync("UPDATE purch.supplier SET status = 'merged', merged_into_id = @into, updated_by = @by WHERE id = @id", new { id = s.Id, into = into.Id, by });
        await t.AuditAsync(new AuditEntry("purch.supplier_merged", "supplier", s.Id, After: Json.Obj(new { s.Number, into = into.Number, reason })));
        await t.PublishAsync("purch.supplier_merged", "supplier", s.Id, s.Version + 1, Json.Obj(new { s.Number, intoId = into.Id, into = into.Number }));
    }

    /// <summary>An opening balance at a branch (16.1): the ledger line and Dr opening balance equity / Cr supplier payables (an advance the other way).</summary>
    public static async Task<(Guid Id, string Number)> OpeningAsync(DbTx t, Supplier s, Guid branchId, decimal amount, DateOnly date, string? note, Guid by)
    {
        if (s.Status == "merged") throw new KiteException(ErrorCodes.SupplierState, $"{s.Number} was merged; use the supplier it was merged into.", 409);
        if (amount == 0 || amount != decimal.Round(amount, 3)) throw new KiteException(ErrorCodes.Validation, "The amount is not zero, with at most 3 decimals.", 400, "amount");
        var biz = await BusinessInfo.LoadAsync(t);
        var id = t.NewId();
        var number = await DocNumbers.NextAsync(t, biz.Id, branchId, "opening_balance", await FiscalPeriods.FiscalYearOfAsync(t, biz.Id, date), "OB");
        await t.ExecuteAsync("""
            INSERT INTO purch.supplier_ledger (id, business_id, branch_id, supplier_id, kind, amount, doc_date, due_date, number, source_doc_type, source_doc_id, note,
                                               created_by, origin_server_id)
            VALUES (@id, @b, @branchId, @sid, 'opening', @amount, @d::date, @d::date, @number, 'opening_balance', @id, @note, @by, (SELECT id FROM core.server WHERE is_self))
            """, new { id, b = biz.Id, branchId, sid = s.Id, amount, d = date.ToDateTime(TimeOnly.MinValue), number, note = Clean(note), by });
        await Posting.PostAsync(t, biz.Id, branchId, PostingRuleTemplates.SupplierOpening, new PostingFacts().Set("amount", amount), date, "opening_balance", id,
            $"Supplier opening balance {number}: {s.NameEn}", partySupplierId: s.Id);
        await t.AuditAsync(new AuditEntry("purch.supplier_opening", "supplier", s.Id, After: Json.Obj(new { supplier = s.Number, number, amount, date }), BranchId: branchId));
        return (id, number);
    }

    // ------------------------------------------------------------------ the stream to branch servers and pushes from them
    public static async Task<List<SupplierSnapshot>> SnapshotsAsync(DbTx t, long sinceSeq, IReadOnlyCollection<Guid>? ids = null, int limit = 500) =>
        (await t.QueryAsync<Supplier>(Supplier.Select + (ids is null ? " WHERE s.sync_seq > @sinceSeq" : " WHERE s.id = ANY(@ids)") + " ORDER BY s.sync_seq LIMIT @limit",
            new { sinceSeq, ids = ids?.ToArray(), limit }))
        .Select(s => new SupplierSnapshot(s.Id, s.Number, s.NameEn, s.NameAr, s.VatNumber, s.CrNumber, s.Phone, s.Email, s.Address, s.ContactName, s.TermsDays,
            s.Status, s.MergedIntoId, s.OriginBranchId, s.Version, s.SyncSeq)).ToList();

    /// <summary>A branch server applies head office's copies; head office's numbers and versions are kept.</summary>
    public static async Task ApplySnapshotsAsync(DbTx t, IReadOnlyList<SupplierSnapshot> list, bool advanceSeq)
    {
        if (list.Count == 0) return;
        await t.ExecuteAsync("SELECT set_config('kite.credit_replica', 'on', true)");
        var businessId = await t.ExecuteScalarAsync<Guid>("SELECT id FROM core.business LIMIT 1");
        // Survivors first, so a merge never points at a supplier this server does not have yet.
        foreach (var s in list.OrderBy(x => x.MergedIntoId is null ? 0 : 1))
            await t.ExecuteAsync("""
                INSERT INTO purch.supplier (id, business_id, number, name_en, name_ar, vat_number, cr_number, phone_e164, email, address, contact_name, terms_days,
                                            status, merged_into_id, origin_branch_id, version, sync_seq)
                VALUES (@Id, @businessId, @Number, @NameEn, @NameAr, @VatNumber, @CrNumber, @Phone, @Email, @Address, @ContactName, @TermsDays, @Status, @MergedIntoId,
                        @OriginBranchId, @Version, @Seq)
                ON CONFLICT (id) DO UPDATE SET number = EXCLUDED.number, name_en = EXCLUDED.name_en, name_ar = EXCLUDED.name_ar, vat_number = EXCLUDED.vat_number,
                       cr_number = EXCLUDED.cr_number, phone_e164 = EXCLUDED.phone_e164, email = EXCLUDED.email, address = EXCLUDED.address,
                       contact_name = EXCLUDED.contact_name, terms_days = EXCLUDED.terms_days, status = EXCLUDED.status, merged_into_id = EXCLUDED.merged_into_id,
                       version = EXCLUDED.version, sync_seq = EXCLUDED.sync_seq, updated_at = now()
                """, new { s.Id, businessId, s.Number, s.NameEn, s.NameAr, s.VatNumber, s.CrNumber, s.Phone, s.Email, s.Address, s.ContactName, s.TermsDays, s.Status,
                           s.MergedIntoId, s.OriginBranchId, s.Version, s.Seq });
        if (advanceSeq)
            await t.ExecuteAsync("""
                INSERT INTO crm.credit_link (business_id, supplier_seq) VALUES (@businessId, @seq)
                ON CONFLICT (business_id) DO UPDATE SET supplier_seq = greatest(crm.credit_link.supplier_seq, EXCLUDED.supplier_seq), updated_at = now()
                """, new { businessId, seq = list.Max(s => s.Seq) });
        await t.ExecuteAsync("SELECT set_config('kite.credit_replica', '', true)");
    }

    /// <summary>A branch server's provisional suppliers head office does not have at their current version, and its unconfirmed ledger lines.</summary>
    public static async Task<SupplierPushRequest> PendingPushAsync(DbTx t)
    {
        var suppliers = (await t.QueryAsync<Supplier>(Supplier.Select + """
             WHERE s.status = 'provisional' AND s.origin_server_id = (SELECT id FROM core.server WHERE is_self)
               AND NOT EXISTS (SELECT 1 FROM purch.supplier_push p WHERE p.supplier_id = s.id AND p.version >= s.version)
             ORDER BY s.created_at LIMIT 200
            """)).Select(s => new SupplierSnapshot(s.Id, s.Number, s.NameEn, s.NameAr, s.VatNumber, s.CrNumber, s.Phone, s.Email, s.Address, s.ContactName,
                s.TermsDays, s.Status, null, s.OriginBranchId, s.Version, 0)).ToList();
        var entries = (await t.WithAllBranchesAsync(() => t.QueryAsync<(Guid Id, Guid BranchId, Guid SupplierId, string Kind, decimal Amount, DateTime DocDate, DateTime? DueDate,
            string Number, string SourceDocType, Guid SourceDocId, string? Note, DateTime CreatedAt, Guid CreatedBy)>("""
            SELECT l.id, l.branch_id, l.supplier_id, l.kind, l.amount, l.doc_date, l.due_date, l.number, l.source_doc_type, l.source_doc_id, l.note, l.created_at, l.created_by
              FROM purch.supplier_ledger l
             WHERE l.origin_server_id = (SELECT id FROM core.server WHERE is_self) AND NOT EXISTS (SELECT 1 FROM purch.supplier_entry_ack k WHERE k.entry_id = l.id)
             ORDER BY l.created_at LIMIT 500
            """))).Select(e => new SupplierEntryPush(e.Id, e.BranchId, e.SupplierId, e.Kind, e.Amount, DateOnly.FromDateTime(e.DocDate),
                e.DueDate is { } d ? DateOnly.FromDateTime(d) : null, e.Number, e.SourceDocType, e.SourceDocId, e.Note,
                new DateTimeOffset(DateTime.SpecifyKind(e.CreatedAt, DateTimeKind.Utc)), e.CreatedBy)).ToList();
        return new SupplierPushRequest(suppliers, entries);
    }

    /// <summary>
    /// Head office takes a branch server's provisional suppliers (a newer version of one still provisional replaces its
    /// details; one head office already approved or merged keeps head office's) and its supplier ledger lines, each once.
    /// </summary>
    public static async Task<SupplierPushResponse> AcceptPushAsync(DbTx t, PkiService.BranchIdentity who, SupplierPushRequest req)
    {
        var businessId = await t.ExecuteScalarAsync<Guid>("SELECT id FROM core.business LIMIT 1");
        var errors = new List<string>();
        var answered = new List<Guid>();
        foreach (var s in req.Suppliers ?? [])
        {
            if (s.OriginBranchId != who.BranchId) { errors.Add($"{s.Number}: added at another branch"); continue; }
            await t.ExecuteAsync("""
                INSERT INTO purch.supplier (id, business_id, number, name_en, name_ar, vat_number, cr_number, phone_e164, email, address, contact_name, terms_days,
                                            status, origin_branch_id, created_by, origin_server_id)
                VALUES (@Id, @businessId, @Number, @NameEn, @NameAr, @VatNumber, @CrNumber, @Phone, @Email, @Address, @ContactName, @TermsDays, 'provisional',
                        @OriginBranchId, NULL, @server)
                ON CONFLICT (id) DO UPDATE SET name_en = EXCLUDED.name_en, name_ar = EXCLUDED.name_ar, vat_number = EXCLUDED.vat_number, cr_number = EXCLUDED.cr_number,
                       phone_e164 = EXCLUDED.phone_e164, email = EXCLUDED.email, address = EXCLUDED.address, contact_name = EXCLUDED.contact_name,
                       terms_days = EXCLUDED.terms_days
                 WHERE purch.supplier.status = 'provisional'
                """, new { s.Id, businessId, s.Number, s.NameEn, s.NameAr, s.VatNumber, s.CrNumber, s.Phone, s.Email, s.Address, s.ContactName, s.TermsDays,
                           s.OriginBranchId, server = who.ServerId });
            answered.Add(s.Id);
        }
        var acked = new List<Guid>();
        foreach (var e in req.Entries ?? [])
        {
            if (e.BranchId != who.BranchId) { errors.Add($"{e.Number}: written for another branch"); continue; }
            if (!await t.ExecuteScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM purch.supplier WHERE id = @SupplierId)", new { e.SupplierId }))
            { errors.Add($"{e.Number}: head office does not have the supplier yet"); continue; }
            await t.ExecuteAsync("""
                INSERT INTO purch.supplier_ledger (id, business_id, branch_id, supplier_id, kind, amount, doc_date, due_date, number, source_doc_type, source_doc_id, note,
                                                   created_at, created_by, origin_server_id)
                VALUES (@Id, @businessId, @BranchId, @SupplierId, @Kind, @Amount, @doc::date, @due::date, @Number, @SourceDocType, @SourceDocId, @Note, @CreatedAt,
                        @CreatedBy, @server)
                ON CONFLICT (id) DO NOTHING
                """, new { e.Id, businessId, e.BranchId, e.SupplierId, e.Kind, e.Amount, doc = e.DocDate.ToDateTime(TimeOnly.MinValue),
                           due = e.DueDate?.ToDateTime(TimeOnly.MinValue), e.Number, e.SourceDocType, e.SourceDocId, e.Note, e.CreatedAt, e.CreatedBy, server = who.ServerId });
            acked.Add(e.Id);
        }
        if (answered.Count + acked.Count > 0)
            await t.AuditAsync(new AuditEntry("purch.sync_received", "server", who.ServerId,
                After: Json.Obj(new { suppliers = answered.Count, entries = acked.Count, errors = errors.Count }), BranchId: who.BranchId));
        return new SupplierPushResponse(await SnapshotsAsync(t, 0, answered), acked, errors);
    }

    /// <summary>Open amounts by due date: a supplier's negative lines settle its oldest positive lines first (until purchasing allocates them, Phase 11).</summary>
    public static AgingBuckets Aging(IReadOnlyList<(DateOnly Due, decimal Amount)> lines, DateOnly asOf)
    {
        var positives = lines.Where(l => l.Amount > 0).Select((l, i) => new OpenItem(Guid.NewGuid(), l.Due, l.Amount)).ToList();
        var credit = -lines.Where(l => l.Amount < 0).Sum(l => l.Amount);
        var (allocs, left) = CreditMath.AllocateOldestFirst(positives, credit);
        var used = allocs.ToDictionary(a => a.ItemId, a => a.Amount);
        return CreditMath.Aging(positives.Select(p => (p.Due, p.Open - used.GetValueOrDefault(p.Id))), left, asOf);
    }
}
