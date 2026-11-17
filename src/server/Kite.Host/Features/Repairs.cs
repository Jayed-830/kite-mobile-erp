using System.Globalization;
using System.Text;
using System.Text.Json;
using Kite.Accounting;
using Kite.Host.Infrastructure;
using Kite.Identity;
using Kite.Persistence;
using Kite.Platform;
using Kite.Devices;
using Kite.Sales;

namespace Kite.Host.Features;

public sealed record RepairDeviceInput(string? Imei1 = null, string? Imei2 = null, string? Serial = null, string? Brand = null, string? Model = null, string? Colour = null);
public sealed record PhotoInput(string Stage, string DataUrl, string? Caption = null);
public sealed record RepairIntakeRequest(Guid CustomerId, Guid CategoryId, string Problem, string PasscodeMode, string TermsName, string TermsSignature,
    Guid? CustomerDeviceId = null, RepairDeviceInput? Device = null, List<string>? FaultCodes = null, Dictionary<string, string>? Condition = null,
    string? ConditionNotes = null, List<string>? Accessories = null, string? AccessoriesOther = null, string? Passcode = null, decimal? PreAuthorised = null,
    DateTimeOffset? PromisedAt = null, bool NotifyWhatsapp = false, string? Lang = null, List<PhotoInput>? Photos = null, Guid? TechnicianId = null,
    Guid? BranchId = null, Guid? Id = null);
public sealed record WarrantyClaimRequest(string Problem, string PasscodeMode, string TermsName, string TermsSignature, List<string>? FaultCodes = null,
    Dictionary<string, string>? Condition = null, string? ConditionNotes = null, List<string>? Accessories = null, string? AccessoriesOther = null,
    string? Passcode = null, bool NotifyWhatsapp = false, List<PhotoInput>? Photos = null);
public sealed record RepairStatusRequest(string To, string? Note = null, int? Version = null);
public sealed record RepairAssignRequest(Guid? TechnicianId);
public sealed record EstimateRequest(List<EstimateLineInput> Lines, string? Note = null);
public sealed record EstimateApproveRequest(string Method, string? ApproverName = null, string? Phone = null, string? Signature = null, string? Then = null);
public sealed record EstimateDeclineRequest(string Reason);
public sealed record PartFitRequest(Guid VariantId, decimal Qty, string? Note = null, Guid? WarehouseId = null, string? Serial = null);
public sealed record PartReturnRequest(decimal Qty, string? Note = null);
public sealed record PartWriteOffRequest(string Reason);
public sealed record LabourRequest(int Minutes, Guid? TechnicianId = null, string? Note = null);
public sealed record DepositRequest(decimal Amount, string Method, decimal? Tendered = null, string? CardTerminal = null, string? ApprovalCode = null,
    string? Reference = null);
public sealed record DepositRefundRequest(decimal Amount, string Method, string Reason, string? CardTerminal = null, string? Reference = null);
public sealed record DeliverRequest(string Check, string CollectedBy, string Signature, string? QrCode = null, decimal? DiagnosisFee = null,
    List<TenderInput>? Payments = null, string? ExcessTo = null, decimal? ExpectedDue = null, string? Note = null, Guid? CreditApprovalId = null,
    CreditPlanInput? CreditPlan = null);
public sealed record RepairMessageRequest(string Text);
public sealed record RepairSettingsRequest(decimal PreAuthorised, int WarrantyDays, int PasscodeKeepDays, int[] UncollectedDays, decimal DiagnosisFee,
    decimal HourlyRate, bool DepositVatPoint, string[] NotifyStatuses, string TermsEn, string TermsAr, int Version);
public sealed record RepairCategoryRequest(string Code, string NameEn, string NameAr, int TurnaroundHours, int? WarrantyDays = null, decimal? PreAuthorised = null,
    short? Sort = null, string? Status = null, int? Version = null);
public sealed record FaultCodeRequest(string Code, string NameEn, string NameAr, Guid? CategoryId = null, short? Sort = null, string? Status = null, int? Version = null);

/// <summary>
/// Repairs (blueprint 13): intake with the customer's device, condition, photos, passcode handling and signed terms; the 12-status
/// workflow and the workshop board; versioned estimates and their approval; parts into work-in-progress and labour; deposits;
/// delivery with the repair invoice; cancellations with a diagnosis fee; warranty claims; uncollected devices; documents.
/// </summary>
public static partial class RepairEndpoints
{
    public static readonly string[] AccessoryCodes = ["sim_tray", "sim_card", "memory_card", "case", "charger", "box"];
    public static readonly string[] ConditionCodes = ["screen", "body", "buttons", "cameras", "liquid"];

    public static void Map(WebApplication app)
    {
        MapJobs(app);
        MapWork(app);
        MapMoney(app);
        MapSetup(app);
        MapDocuments(app);
        MapParts(app);
    }

    private static Guid Uid(HttpContext http) => http.User.UserIdOf();

    // ------------------------------------------------------------------ the job, its board and its workflow
    private static void MapJobs(WebApplication app)
    {
        var g = app.MapGroup("/api/v1/repairs");

        // The workshop board (13.6) and the job list: by status and technician, overdue promises first.
        g.MapGet("", async (string? status, Guid? technicianId, Guid? branchId, string? q, bool? open, HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), t => t.RowsAsync("""
                SELECT j.id, j.number, j.status, j.outcome, b.code AS "branchCode", j.branch_id AS "branchId", c.name_en AS "customerName", c.phone_e164 AS "customerPhone",
                       coalesce(nullif(trim(coalesce(d.brand, '') || ' ' || d.model), ''), d.model) AS device, coalesce(d.imei1, d.serial) AS identifier,
                       cat.name_en AS "categoryEn", cat.name_ar AS "categoryAr", j.technician_id AS "technicianId", tu.display_name_en AS "technicianName",
                       j.promised_at AS "promisedAt", (j.promised_at < now() AND j.status NOT IN ('ready','delivered','cancelled','unrepairable','completed')) AS overdue,
                       j.warranty_of_id IS NOT NULL AS "warrantyClaim", j.created_at AS "createdAt", j.delivered_at AS "deliveredAt",
                       (SELECT e.total FROM repair.estimate e WHERE e.job_id = j.id ORDER BY e.estimate_no DESC LIMIT 1) AS estimate,
                       (SELECT e.status FROM repair.estimate e WHERE e.job_id = j.id ORDER BY e.estimate_no DESC LIMIT 1) AS "estimateStatus",
                       repair.deposit_balance(j.id) AS deposit,
                       (SELECT max(h.at) FROM repair.status_history h WHERE h.job_id = j.id) AS "statusAt",
                       (SELECT count(*) FROM notif.inbound_message m WHERE m.customer_id = j.customer_id AND m.handled IS DISTINCT FROM 'seen'
                          AND m.received_at > coalesce((SELECT max(o.created_at) FROM notif.outbound_message o WHERE o.customer_id = j.customer_id AND o.template IS NULL), 'epoch')) AS "unreadMessages",
                       j.reminded_days AS "remindedDays", j.version
                  FROM repair.repair_job j JOIN core.branch b ON b.id = j.branch_id JOIN crm.customer c ON c.id = j.customer_id
                  JOIN device.customer_device d ON d.id = j.customer_device_id JOIN repair.category cat ON cat.id = j.category_id
                  LEFT JOIN iam."user" tu ON tu.id = j.technician_id
                 WHERE (@status::text IS NULL OR j.status = @status) AND (@technicianId::uuid IS NULL OR j.technician_id = @technicianId)
                   AND (@branchId::uuid IS NULL OR j.branch_id = @branchId) AND (NOT @open OR j.status <> 'delivered')
                   AND (@q::text IS NULL OR j.number ILIKE '%' || @q || '%' OR c.name_en ILIKE '%' || @q || '%' OR c.phone_e164 LIKE '%' || @q || '%'
                        OR d.imei1 = @q OR upper(d.serial) = upper(@q))
                 ORDER BY (j.status = 'delivered'), overdue DESC, j.promised_at LIMIT 500
                """, new { status = Clean(status), technicianId, branchId, q = Clean(q), open = open ?? false }), ct)))
            .RequirePermission(Permissions.RepairView).RequireModule("repair");

        g.MapGet("/{id:guid}", async (Guid id, HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), t => DetailAsync(t, http, id), ct)))
            .RequirePermission(Permissions.RepairView).RequireModule("repair");

        // Intake look-up (13.1, 11.5): the customer's devices with this IMEI or serial, the unit the business sold (with its warranty),
        // and the device's open and earlier jobs.
        g.MapGet("/lookup", async (string code, HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), t => LookupAsync(t, code), ct)))
            .RequirePermission(Permissions.RepairView).RequireModule("repair");

        g.MapPost("", async (RepairIntakeRequest req, HttpContext http, KiteDb db, LicenceService licence, ISecretProtector secrets, ModuleStateCache modules,
            CancellationToken ct) =>
        {
            await licence.RequireTransactAsync(ct);
            if (req.NotifyWhatsapp && !await modules.IsEnabledAsync("notif", ct))
                throw new KiteException(ErrorCodes.WhatsAppState, "WhatsApp notifications are switched off for this business.", 400, "notifyWhatsapp");
            var result = await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), t => IntakeAsync(t, http, secrets, req, null), ct);
            return Results.Created($"/api/v1/repairs/{result}", await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), t => DetailAsync(t, http, result), ct));
        }).RequirePermission(Permissions.RepairIntake).RequireModule("repair");

        // A warranty claim (13.6): a new job linked to the delivered one, at no charge, counted as rework against its technician.
        g.MapPost("/{id:guid}/warranty-claim", async (Guid id, WarrantyClaimRequest req, HttpContext http, KiteDb db, LicenceService licence,
            ISecretProtector secrets, CancellationToken ct) =>
        {
            await licence.RequireTransactAsync(ct);
            var newId = await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                var o = await RepairCore.LoadAsync(t, id, forUpdate: true);
                http.User.Demand(Permissions.RepairIntake, o.BranchId);
                if (o.Status != "delivered" || o.Outcome != "repaired")
                    throw new KiteException(ErrorCodes.RepairState, "A warranty claim is made on a repaired job that was delivered.", 409);
                var today = BusinessDay.Today((await BusinessInfo.LoadAsync(t)).TimeZone, t.Clock);
                if (o.WarrantyUntil is not { } until || DateOnly.FromDateTime(until) < today)
                    throw new KiteException(ErrorCodes.RepairState, o.WarrantyUntil is null ? "This repair had no warranty." : $"The repair's warranty ended on {o.WarrantyUntil:yyyy-MM-dd}.", 409);
                var claimId = await IntakeAsync(t, http, secrets, new RepairIntakeRequest(o.CustomerId, o.CategoryId, req.Problem, req.PasscodeMode, req.TermsName,
                    req.TermsSignature, o.CustomerDeviceId, null, req.FaultCodes, req.Condition, req.ConditionNotes, req.Accessories, req.AccessoriesOther,
                    req.Passcode, 0m, null, req.NotifyWhatsapp, o.Lang, req.Photos, o.TechnicianId, o.BranchId), o);
                // Rework (13.6, 15.3): a claim soon after delivery can take back the original technician's commission.
                await Commissions.ClawbackAsync(t, await RepairCore.LoadAsync(t, claimId), Uid(http));
                return claimId;
            }, ct);
            return Results.Created($"/api/v1/repairs/{newId}", await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), t => DetailAsync(t, http, newId), ct));
        }).RequirePermission(Permissions.RepairIntake).RequireModule("repair");

        // A status change (13.2). Delivery has its own endpoint; cancelling needs REPAIR.CANCEL.
        g.MapPost("/{id:guid}/status", async (Guid id, RepairStatusRequest req, HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await PosEndpoints.WithRetryAsync(() => db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                var j = await RepairCore.LoadAsync(t, id, forUpdate: true);
                await ChangeStatusAsync(t, http, j, req.To, req.Note, req.Version);
                return await DetailAsync(t, http, id);
            }, ct))))
            .RequirePermission(Permissions.RepairView).RequireModule("repair");

        g.MapPost("/{id:guid}/assign", async (Guid id, RepairAssignRequest req, HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                var j = await RepairCore.LoadAsync(t, id, forUpdate: true);
                // A technician may take an unassigned job; anyone else assigning or reassigning needs REPAIR.ASSIGN.
                var self = req.TechnicianId == Uid(http) && j.TechnicianId is null && http.User.HasIn(Permissions.RepairWork, j.BranchId);
                if (!self) http.User.Demand(Permissions.RepairAssign, j.BranchId);
                if (j.Status == "delivered") throw new KiteException(ErrorCodes.RepairState, "A delivered job keeps its technician.", 409);
                if (req.TechnicianId is { } tech) await CheckTechnicianAsync(t, tech, j.BranchId);
                await t.ExecuteAsync("UPDATE repair.repair_job SET technician_id = @tech, updated_by = @uid WHERE id = @id",
                    new { tech = req.TechnicianId, uid = Uid(http), id });
                await t.AuditAsync(new AuditEntry("repair.assigned", "repair_job", id, Before: Json.Obj(new { technicianId = j.TechnicianId }),
                    After: Json.Obj(new { technicianId = req.TechnicianId }), BranchId: j.BranchId));
                return await DetailAsync(t, http, id);
            }, ct))).RequirePermission(Permissions.RepairView).RequireModule("repair");

        // Technicians a job can be assigned to: users with REPAIR.WORK in the branch.
        g.MapGet("/technicians", async (Guid? branchId, HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(AccessScope.System, http.ActorOf(), t => t.RowsAsync("""
                SELECT DISTINCT u.id, u.display_name_en AS "nameEn", u.display_name_ar AS "nameAr"
                  FROM iam."user" u JOIN iam.user_branch_role ur ON ur.user_id = u.id JOIN iam.role_permission rp ON rp.role_id = ur.role_id
                 WHERE u.status = 'active' AND rp.permission_code = 'REPAIR.WORK' AND (@branchId::uuid IS NULL OR ur.branch_id IS NULL OR ur.branch_id = @branchId)
                 ORDER BY 2
                """, new { branchId }), ct))).RequirePermission(Permissions.RepairView).RequireModule("repair");

        // Passcode (13.1): shown only with REPAIR.CREDENTIAL.VIEW, every view logged; never printed or sent.
        g.MapPost("/{id:guid}/passcode", async (Guid id, HttpContext http, KiteDb db, ISecretProtector secrets, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                var j = await RepairCore.LoadAsync(t, id);
                http.User.Demand(Permissions.RepairCredentialView, j.BranchId);
                var enc = await t.ExecuteScalarAsync<byte[]?>("SELECT passcode_enc FROM repair.repair_job WHERE id = @id", new { id })
                    ?? throw new KiteException(ErrorCodes.RepairPasscode, j.PasscodePurgedAt is not null ? "The passcode was deleted." : "No passcode was given for this job.", 404);
                await t.ExecuteAsync("INSERT INTO repair.credential_view (branch_id, job_id, user_id) VALUES (@BranchId, @id, @uid)", new { j.BranchId, id, uid = Uid(http) });
                await t.AuditAsync(new AuditEntry("repair.passcode_viewed", "repair_job", id, After: Json.Obj(new { j.Number }), BranchId: j.BranchId));
                return new { passcode = Encoding.UTF8.GetString(secrets.Unprotect(enc)) };
            }, ct))).RequirePermission(Permissions.RepairCredentialView).RequireModule("repair");

        g.MapPost("/{id:guid}/photos", async (Guid id, PhotoInput req, HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                var j = await RepairCore.LoadAsync(t, id);
                if (!http.User.HasIn(Permissions.RepairWork, j.BranchId)) http.User.Demand(Permissions.RepairIntake, j.BranchId);
                if (req.Stage is not ("intake" or "diagnosis" or "delivery")) throw new KiteException(ErrorCodes.Validation, "A photo is from intake, diagnosis or delivery.", 400, "stage");
                if (j.Status == "delivered" && req.Stage != "delivery") throw new KiteException(ErrorCodes.RepairState, "The job was delivered.", 409);
                return new { id = await SavePhotoAsync(t, j, req.Stage, req.DataUrl, req.Caption, null, Uid(http)) };
            }, ct))).RequirePermission(Permissions.RepairView).RequireModule("repair");

        g.MapGet("/{id:guid}/photos/{photoId:guid}", async (Guid id, Guid photoId, HttpContext http, KiteDb db, CancellationToken ct) =>
        {
            var p = await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), t => t.QuerySingleOrDefaultAsync<(string ContentType, byte[] Content, string Sha256)>("""
                SELECT content_type, content, sha256 FROM repair.photo WHERE id = @photoId AND job_id = @id
                """, new { photoId, id }), ct);
            if (p.Content is null) throw new KiteException(ErrorCodes.NotFound, "Photo not found.", 404);
            http.Response.Headers["X-Sha256"] = p.Sha256;
            http.Response.Headers.CacheControl = "private, max-age=3600";
            return Results.File(p.Content, p.ContentType);
        }).RequirePermission(Permissions.RepairView).RequireModule("repair");

        // Messages with the customer about the job (5.3): what was sent and its status, and their replies.
        g.MapGet("/{id:guid}/messages", async (Guid id, HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                var j = await RepairCore.LoadAsync(t, id);
                return await MessagesAsync(t, j);
            }, ct))).RequirePermission(Permissions.RepairView).RequireModule("repair");

        // A reply typed by staff: only within 24 hours of the customer's last message (WhatsApp's customer service window).
        g.MapPost("/{id:guid}/messages", async (Guid id, RepairMessageRequest req, HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                var j = await RepairCore.LoadAsync(t, id);
                http.User.Demand(Permissions.NotifReply, j.BranchId);
                var text = (req.Text ?? "").Trim();
                if (text.Length is < 1 or > 1000) throw new KiteException(ErrorCodes.Validation, "Write 1 to 1000 characters.", 400, "text");
                if (j.CustomerPhone is null) throw new KiteException(ErrorCodes.WhatsAppConsent, "The customer has no phone number.", 400);
                var last = await t.ExecuteScalarAsync<DateTime?>("SELECT max(received_at) FROM notif.inbound_message WHERE phone_e164 = @p AND kind <> 'stop'",
                    new { p = j.CustomerPhone });
                if (last is null || last.Value < t.Clock.UtcNow.UtcDateTime.AddHours(-24))
                    throw new KiteException(ErrorCodes.WhatsAppWindow, "WhatsApp allows a typed reply only within 24 hours of the customer's last message. Call the customer instead.", 409);
                await t.ExecuteAsync("""
                    INSERT INTO notif.outbound_message (id, business_id, branch_id, customer_id, phone_e164, lang, body_text, source_doc_type, source_doc_id,
                                                        expires_at, created_by, origin_server_id)
                    VALUES (@mid, @b, @BranchId, @CustomerId, @CustomerPhone, @Lang, @text, 'repair_job', @id, now() + interval '1 hour', @uid,
                            (SELECT id FROM core.server WHERE is_self))
                    """, new { mid = t.NewId(), b = j.BusinessId, j.BranchId, j.CustomerId, j.CustomerPhone, j.Lang, text, id, uid = Uid(http) });
                await t.ExecuteAsync("UPDATE notif.inbound_message SET handled = coalesce(handled, 'seen') WHERE customer_id = @c AND handled IS NULL", new { c = j.CustomerId });
                return await MessagesAsync(t, j);
            }, ct))).RequirePermission(Permissions.NotifReply).RequireModule("repair");

        // Devices ready but not collected (13.6): reminders go out at the configured ages; disposal is never automatic.
        g.MapGet("/uncollected", async (HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), t => t.RowsAsync("""
                SELECT j.id, j.number, b.code AS "branchCode", c.name_en AS "customerName", c.phone_e164 AS "customerPhone", d.model AS device, j.outcome,
                       h.at AS "readySince", floor(extract(epoch FROM now() - h.at) / 86400)::int AS days, j.reminded_days AS "remindedDays",
                       repair.deposit_balance(j.id) AS deposit
                  FROM repair.repair_job j JOIN core.branch b ON b.id = j.branch_id JOIN crm.customer c ON c.id = j.customer_id
                  JOIN device.customer_device d ON d.id = j.customer_device_id
                  JOIN LATERAL (SELECT max(at) AS at FROM repair.status_history WHERE job_id = j.id AND to_status = 'ready') h ON true
                 WHERE j.status = 'ready' ORDER BY h.at
                """), ct))).RequirePermission(Permissions.RepairView).RequireModule("repair");
    }

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static async Task CheckTechnicianAsync(DbTx t, Guid tech, Guid branchId)
    {
        var ok = await t.WithAllBranchesAsync(() => t.ExecuteScalarAsync<bool>("""
            SELECT EXISTS (SELECT 1 FROM iam."user" u JOIN iam.user_branch_role ur ON ur.user_id = u.id JOIN iam.role_permission rp ON rp.role_id = ur.role_id
                            WHERE u.id = @tech AND u.status = 'active' AND rp.permission_code = 'REPAIR.WORK'
                              AND (ur.branch_id IS NULL OR ur.branch_id = @branchId))
            """, new { tech, branchId }));
        if (!ok) throw new KiteException(ErrorCodes.Validation, "That person cannot work on repairs in this branch.", 400, "technicianId");
    }

    /// <summary>The status change rules on top of the database's transition table (13.2, 13.3).</summary>
    public static async Task ChangeStatusAsync(DbTx t, HttpContext http, RepairJob j, string to, string? note, int? version)
    {
        var uid = Uid(http);
        if (to == "delivered") throw new KiteException(ErrorCodes.RepairCollection, "A device is delivered with the collection step: the customer's check, signature and payment.", 409, "to");
        if (to == "cancelled") http.User.Demand(Permissions.RepairCancel, j.BranchId);
        else if (to == "ready" && http.User.HasIn(Permissions.RepairIntake, j.BranchId)) { }
        else http.User.Demand(Permissions.RepairWork, j.BranchId);
        if (!RepairCore.Transitions.TryGetValue(j.Status, out var allowed) || !allowed.Contains(to))
            throw new KiteException(ErrorCodes.RepairTransition, $"A job that is {RepairCore.Words(j.Status)} cannot move to {RepairCore.Words(to)}.", 409, "to");
        if (version is { } v && v != j.Version) throw new KiteException(ErrorCodes.VersionMismatch, "Someone else changed this job. Reload and try again.", 412, "version");
        var biz = await BusinessInfo.LoadAsync(t);
        var settings = await RepairCore.SettingsAsync(t);
        if (to is "in_progress" or "waiting_parts" or "testing" or "completed")
        {
            // Work goes ahead on an approved estimate; one within what the customer authorised is approved by staff here.
            var e = await RepairCore.CurrentEstimateAsync(t, j.Id)
                ?? throw new KiteException(ErrorCodes.RepairEstimate, "Write the estimate first: the work goes ahead on an approved estimate.", 409);
            if (e.Status != "approved")
            {
                var ceiling = await RepairCore.CeilingAsync(t, j.Id);
                if (e.Status is "declined" or "superseded" || e.Total > ceiling)
                    throw new KiteException(ErrorCodes.RepairEstimate,
                        $"The estimate ({e.Total:0.000}) is more than the customer authorised ({ceiling:0.000}). Send it and wait for the customer's approval.", 409);
                if (e.Status == "draft" && e.Total == 0 && !j.IsWarrantyClaim)
                    throw new KiteException(ErrorCodes.RepairEstimate, "The estimate has no lines yet.", 409);
                await RepairCore.ApproveAsync(t, j, e, "pre_authorised", null, null, uid, t.Clock.UtcNow);
            }
        }
        if (to == "waiting_approval")
        {
            var e = await RepairCore.CurrentEstimateAsync(t, j.Id)
                ?? throw new KiteException(ErrorCodes.RepairEstimate, "Write the estimate the customer is to approve first.", 409);
            if (e.Status == "approved") throw new KiteException(ErrorCodes.RepairEstimate, "The current estimate is already approved: write a new version for the extra work.", 409);
            if (e.Status == "draft") await SendEstimateRowAsync(t, e, uid);
        }
        await RepairCore.MoveAsync(t, j, to, note, uid, version);
        await RepairCore.NotifyStatusAsync(t, j, settings, biz, uid);
    }

    private static Task SendEstimateRowAsync(DbTx t, RepairEstimate e, Guid uid) =>
        t.ExecuteAsync("UPDATE repair.estimate SET status = 'sent', sent_at = now(), updated_by = @uid WHERE id = @id", new { uid, id = e.Id });

    // ------------------------------------------------------------------ intake (13.1)
    private static async Task<Guid> IntakeAsync(DbTx t, HttpContext http, ISecretProtector secrets, RepairIntakeRequest req, RepairJob? warrantyOf)
    {
        var uid = Uid(http);
        Guid branchId;
        if (http.User.TerminalIdOf() is { } tid) branchId = (await PosTerminals.ByIdAsync(t, tid)).BranchId;
        else if (req.BranchId is { } b) branchId = b;
        else throw new KiteException(ErrorCodes.Validation, "Choose the branch receiving the device.", 400, "branchId");
        if (warrantyOf is not null) branchId = warrantyOf.BranchId;
        http.User.Demand(Permissions.RepairIntake, branchId);
        var biz = await BusinessInfo.LoadAsync(t);
        var settings = await RepairCore.SettingsAsync(t);

        var customer = await t.QuerySingleOrDefaultAsync<(Guid Id, string Status, string? Phone, string Lang, bool OptIn)>("""
            SELECT id, status, phone_e164, lang, whatsapp_opt_in FROM crm.customer WHERE id = @CustomerId
            """, new { req.CustomerId });
        if (customer.Id == Guid.Empty || customer.Status != "active") throw new KiteException(ErrorCodes.Validation, "Choose an active customer.", 400, "customerId");
        var cat = await t.QuerySingleOrDefaultAsync<(Guid Id, int Hours, int? Warranty, decimal? PreAuthorised, string Status)>("""
            SELECT id, turnaround_hours, warranty_days, pre_authorised, status FROM repair.category WHERE id = @CategoryId
            """, new { req.CategoryId });
        if (cat.Id == Guid.Empty || cat.Status != "active") throw new KiteException(ErrorCodes.Validation, "Choose the kind of repair.", 400, "categoryId");
        var problem = (req.Problem ?? "").Trim();
        if (problem.Length is < 3 or > 2000) throw new KiteException(ErrorCodes.Validation, "Describe the problem (3 to 2000 characters).", 400, "problem");
        var faults = (req.FaultCodes ?? []).Select(f => f.Trim().ToUpperInvariant()).Distinct().ToArray();
        if (faults.Length > 0)
        {
            var known = (await t.QueryAsync<string>("SELECT code FROM repair.fault_code WHERE status = 'active' AND code = ANY(@faults)", new { faults })).ToHashSet();
            if (faults.FirstOrDefault(f => !known.Contains(f)) is { } bad) throw new KiteException(ErrorCodes.Validation, $"Unknown fault code {bad}.", 400, "faultCodes");
        }
        var condition = new Dictionary<string, string>();
        foreach (var (k, v) in req.Condition ?? [])
        {
            if (!ConditionCodes.Contains(k)) throw new KiteException(ErrorCodes.Validation, $"Unknown condition check {k}.", 400, "condition");
            if (v is not ("ok" or "fault" or "not_checked")) throw new KiteException(ErrorCodes.Validation, "Each condition check is ok, fault or not checked.", 400, $"condition.{k}");
            condition[k] = v;
        }
        var accessories = (req.Accessories ?? []).Distinct().ToList();
        if (accessories.FirstOrDefault(a => !AccessoryCodes.Contains(a)) is { } badAcc) throw new KiteException(ErrorCodes.Validation, $"Unknown accessory {badAcc}.", 400, "accessories");
        var accessoriesJson = JsonSerializer.Serialize(new { items = accessories, other = Clean(req.AccessoriesOther) });

        if (req.PasscodeMode is not ("none" or "customer_present" or "provided"))
            throw new KiteException(ErrorCodes.Validation, "Device access is not given, tested with the customer present, or given.", 400, "passcodeMode");
        byte[]? passcodeEnc = null;
        if (req.PasscodeMode == "provided")
        {
            var pc = (req.Passcode ?? "").Trim();
            if (pc.Length is < 1 or > 64) throw new KiteException(ErrorCodes.Validation, "Type the passcode the customer gave (1 to 64 characters).", 400, "passcode");
            passcodeEnc = secrets.Protect(Encoding.UTF8.GetBytes(pc));
        }
        else if (!string.IsNullOrEmpty(req.Passcode)) throw new KiteException(ErrorCodes.Validation, "A passcode is kept only when the customer gives it.", 400, "passcode");

        var termsName = (req.TermsName ?? "").Trim();
        if (termsName.Length < 2) throw new KiteException(ErrorCodes.Validation, "Type the name of the person who signed the terms.", 400, "termsName");
        if (string.IsNullOrWhiteSpace(req.TermsSignature)) throw new KiteException(ErrorCodes.Validation, "The customer signs the terms.", 400, "termsSignature");
        var lang = req.Lang ?? customer.Lang;
        if (lang is not ("en" or "ar")) throw new KiteException(ErrorCodes.Validation, "Language is en or ar.", 400, "lang");
        if (req.NotifyWhatsapp && customer.Phone is null)
            throw new KiteException(ErrorCodes.WhatsAppConsent, "WhatsApp updates need the customer's mobile number: add it to the customer first.", 400, "notifyWhatsapp");

        var preAuthorised = req.PreAuthorised ?? cat.PreAuthorised ?? settings.PreAuthorised;
        if (preAuthorised < 0 || preAuthorised != decimal.Round(preAuthorised, 3)) throw new KiteException(ErrorCodes.Validation, "The pre-authorised amount is zero or more, in baisa.", 400, "preAuthorised");
        var now = t.Clock.UtcNow;
        var promised = req.PromisedAt ?? now.AddHours(cat.Hours);
        if (promised < now.AddMinutes(-5)) throw new KiteException(ErrorCodes.Validation, "The promised time is in the past.", 400, "promisedAt");
        if (req.TechnicianId is { } tech) await CheckTechnicianAsync(t, tech, branchId);

        // The device: one of the customer's, or a new one by IMEI or serial, connected to the unit the business sold when it did.
        Guid deviceId;
        Guid? soldSale = null;
        if (req.CustomerDeviceId is { } cdId)
        {
            var owner = await t.ExecuteScalarAsync<Guid?>("SELECT customer_id FROM device.customer_device WHERE id = @cdId", new { cdId });
            if (owner != req.CustomerId) throw new KiteException(ErrorCodes.Validation, "That device is not this customer's.", 400, "customerDeviceId");
            deviceId = cdId;
            soldSale = await t.ExecuteScalarAsync<Guid?>("""
                SELECT u.sold_sale_id FROM device.customer_device d JOIN device.device_unit u ON u.id = d.device_unit_id WHERE d.id = @cdId
                """, new { cdId });
        }
        else
        {
            var d = req.Device ?? throw new KiteException(ErrorCodes.Validation, "Scan the device's IMEI or serial number.", 400, "device");
            (deviceId, soldSale) = await DeviceForAsync(t, biz, req.CustomerId, d, uid);
        }

        var id = req.Id ?? t.NewId();
        if (await t.ExecuteScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM repair.repair_job WHERE id = @id)", new { id }))
            return id;   // the same intake sent again (a retry after a lost answer)
        var openJob = await t.ExecuteScalarAsync<string?>("""
            SELECT number FROM repair.repair_job WHERE customer_device_id = @deviceId AND status <> 'delivered' LIMIT 1
            """, new { deviceId });
        if (openJob is not null) throw new KiteException(ErrorCodes.RepairState, $"This device already has an open job, {openJob}.", 409, "device");

        var number = await DocNumbers.NextAsync(t, biz.Id, branchId, "repair_job",
            await FiscalPeriods.FiscalYearOfAsync(t, biz.Id, BusinessDay.DateOf(now, biz.TimeZone)), "R");
        var warrantyDays = cat.Warranty ?? settings.WarrantyDays;
        await t.ExecuteAsync("SELECT set_config('kite.repair_note', @n, true)", new { n = warrantyOf is null ? "Received" : $"Warranty claim on {warrantyOf.Number}" });
        await t.ExecuteAsync("""
            INSERT INTO repair.repair_job (id, business_id, branch_id, number, customer_id, customer_device_id, category_id, technician_id, warranty_of_id,
                                           sold_sale_id, problem, fault_codes, condition, condition_notes, accessories, passcode_mode, passcode_enc, pre_authorised,
                                           warranty_days, promised_at, terms_name, terms_accepted_at, notify_whatsapp, lang, created_by, origin_server_id)
            VALUES (@id, @b, @branchId, @number, @CustomerId, @deviceId, @CategoryId, @tech, @warrantyOfId, @soldSale, @problem, @faults, @condition::jsonb,
                    @conditionNotes, @accessoriesJson::jsonb, @PasscodeMode, @passcodeEnc, @preAuthorised, @warrantyDays, @promised, @termsName, @now,
                    @notify, @lang, @uid, (SELECT id FROM core.server WHERE is_self))
            """, new { id, b = biz.Id, branchId, number, req.CustomerId, deviceId, req.CategoryId, tech = req.TechnicianId, warrantyOfId = warrantyOf?.Id, soldSale,
                       problem, faults, condition = JsonSerializer.Serialize(condition), conditionNotes = Clean(req.ConditionNotes), accessoriesJson,
                       req.PasscodeMode, passcodeEnc, preAuthorised, warrantyDays, promised, termsName, now, notify = req.NotifyWhatsapp, lang, uid });
        await t.ExecuteAsync("SELECT set_config('kite.repair_note', '', true)");

        // WhatsApp consent is captured here (5.3): opting in records it on the customer and in the consent log.
        if (req.NotifyWhatsapp && !customer.OptIn)
        {
            await t.ExecuteAsync("UPDATE crm.customer SET whatsapp_opt_in = true, whatsapp_opt_in_at = @now, updated_by = @uid WHERE id = @CustomerId",
                new { now, uid, req.CustomerId });
            await t.ExecuteAsync("""
                INSERT INTO notif.consent (id, business_id, branch_id, customer_id, phone_e164, action, source, note, at, by_user_id)
                VALUES (@cid, @b, @branchId, @CustomerId, @phone, 'opt_in', 'intake', @number, @now, @uid)
                """, new { cid = t.NewId(), b = biz.Id, branchId, req.CustomerId, phone = customer.Phone, number, now, uid });
        }

        var j = await RepairCore.LoadAsync(t, id);
        await SavePhotoAsync(t, j, "terms_signature", req.TermsSignature, "Terms signed by " + termsName, null, uid);
        foreach (var p in req.Photos ?? [])
        {
            if (p.Stage != "intake") throw new KiteException(ErrorCodes.Validation, "Intake photos are from intake.", 400, "photos");
            await SavePhotoAsync(t, j, "intake", p.DataUrl, p.Caption, null, uid);
        }
        if ((req.Photos?.Count ?? 0) > 12) throw new KiteException(ErrorCodes.Validation, "At most 12 intake photos.", 400, "photos");

        // A warranty claim's work is at no charge (13.6): its estimate is approved at zero.
        if (warrantyOf is not null)
        {
            await RepairCore.NewEstimateAsync(t, j, [], "Warranty claim", uid);
            var e = (await RepairCore.CurrentEstimateAsync(t, j.Id))!;
            await RepairCore.ApproveAsync(t, j, e, "warranty", null, warrantyOf.Number, uid, now);
        }

        await t.AuditAsync(new AuditEntry("repair.received", "repair_job", id,
            After: Json.Obj(new { number, customer = req.CustomerId, device = j.Identifier, j.Model, warrantyOf = warrantyOf?.Number, preAuthorised }), BranchId: branchId));
        await t.PublishAsync("repair.received", "repair_job", id, 1, Json.Obj(new { number, j.Identifier, j.Model }), branchId);
        await RepairCore.NotifyStatusAsync(t, j, settings, biz, uid);
        return id;
    }

    /// <summary>The customer's device for these identifiers: found, or added (linked to the unit the business sold, 13.1).</summary>
    private static async Task<(Guid Id, Guid? SoldSale)> DeviceForAsync(DbTx t, BusinessInfo biz, Guid customerId, RepairDeviceInput d, Guid uid)
    {
        string? imei1 = null, imei2 = null, serial = null;
        if (!string.IsNullOrWhiteSpace(d.Imei1))
        {
            var c = DeviceUnits.Classify(d.Imei1, "device.imei1");
            if (c.Kind != "imei") throw new KiteException(ErrorCodes.ImeiInvalid, "IMEI 1 is not an IMEI.", 400, "device.imei1");
            imei1 = c.Value;
        }
        if (!string.IsNullOrWhiteSpace(d.Imei2))
        {
            var c = DeviceUnits.Classify(d.Imei2, "device.imei2");
            if (c.Kind != "imei") throw new KiteException(ErrorCodes.ImeiInvalid, "IMEI 2 is not an IMEI.", 400, "device.imei2");
            imei2 = c.Value;
        }
        if (!string.IsNullOrWhiteSpace(d.Serial)) serial = d.Serial.Trim().ToUpperInvariant();
        if (imei1 is null && serial is null) throw new KiteException(ErrorCodes.Validation, "Scan the device's IMEI or serial number.", 400, "device");
        var existing = await t.ExecuteScalarAsync<Guid?>("""
            SELECT id FROM device.customer_device WHERE customer_id = @customerId AND ((@imei1::text IS NOT NULL AND imei1 = @imei1)
                   OR (@imei1::text IS NULL AND imei1 IS NULL AND upper(serial) = @serial)) LIMIT 1
            """, new { customerId, imei1, serial });
        DeviceUnitRow? unit = null;
        var code = imei1 is not null ? new ScannedCode("imei", imei1) : new ScannedCode("serial", serial!);
        unit = await t.WithAllBranchesAsync(() => DeviceUnits.FindAsync(t, code));
        var soldSale = unit is { Status: "sold" } ? unit.SoldSaleId : null;
        if (existing is { } id)
        {
            if (unit is not null)
                await t.ExecuteAsync("UPDATE device.customer_device SET device_unit_id = @u, updated_by = @uid WHERE id = @id AND device_unit_id IS NULL",
                    new { u = unit.Id, uid, id });
            return (id, soldSale);
        }
        var model = Clean(d.Model) ?? unit?.DescriptionEn ?? throw new KiteException(ErrorCodes.Validation, "Type the device's model.", 400, "device.model");
        var newId = t.NewId();
        await t.ExecuteAsync("""
            INSERT INTO device.customer_device (id, business_id, customer_id, imei1, imei2, serial, brand, model, colour, device_unit_id, created_by, origin_server_id)
            VALUES (@newId, @b, @customerId, @imei1, @imei2, @serial, @brand, @model, @colour, @unit, @uid, (SELECT id FROM core.server WHERE is_self))
            """, new { newId, b = biz.Id, customerId, imei1, imei2, serial, brand = Clean(d.Brand), model, colour = Clean(d.Colour), unit = unit?.Id, uid });
        return (newId, soldSale);
    }

    private static async Task<object> LookupAsync(DbTx t, string code)
    {
        var c = DeviceUnits.Classify(code);
        var devices = await t.RowsAsync("""
            SELECT d.id, d.customer_id AS "customerId", cu.name_en AS "customerName", cu.phone_e164 AS "customerPhone", d.imei1, d.imei2, d.serial,
                   d.brand, d.model, d.colour
              FROM device.customer_device d JOIN crm.customer cu ON cu.id = d.customer_id
             WHERE (@kind = 'imei' AND (d.imei1 = @v OR d.imei2 = @v)) OR (@kind = 'serial' AND upper(d.serial) = upper(@v))
            """, new { kind = c.Kind, v = c.Value });
        var unit = await t.WithAllBranchesAsync(() => DeviceUnits.FindAsync(t, c));
        object? sold = null;
        if (unit is { Status: "sold", SoldSaleId: { } saleId })
            sold = await t.WithAllBranchesAsync(async () => (await t.RowsAsync("""
                SELECT s.id AS "saleId", s.number, s.completed_at AS "soldAt", b.code AS "branchCode", sl.warranty_until AS "warrantyUntil",
                       (sl.warranty_until IS NOT NULL AND sl.warranty_until >= current_date) AS "inWarranty", s.customer_id AS "customerId",
                       cu.name_en AS "customerName"
                  FROM sales.sale s JOIN core.branch b ON b.id = s.branch_id JOIN sales.sale_line sl ON sl.sale_id = s.id AND sl.device_unit_id = @unit
                  LEFT JOIN crm.customer cu ON cu.id = s.customer_id
                 WHERE s.id = @saleId
                """, new { unit = unit.Id, saleId })).FirstOrDefault());
        var jobs = await t.RowsAsync("""
            SELECT j.id, j.number, j.status, j.outcome, j.created_at AS "createdAt", j.delivered_at AS "deliveredAt", j.warranty_until AS "warrantyUntil",
                   (j.warranty_until IS NOT NULL AND j.warranty_until >= current_date) AS "inWarranty"
              FROM repair.repair_job j JOIN device.customer_device d ON d.id = j.customer_device_id
             WHERE (@kind = 'imei' AND (d.imei1 = @v OR d.imei2 = @v)) OR (@kind = 'serial' AND upper(d.serial) = upper(@v))
             ORDER BY j.created_at DESC LIMIT 20
            """, new { kind = c.Kind, v = c.Value });
        return new { kind = c.Kind, value = c.Value, devices, unit = unit is null ? null : new { unit.Id, unit.DescriptionEn, unit.DescriptionAr, unit.Status }, sold, jobs };
    }

    // ------------------------------------------------------------------ the job's detail
    public static async Task<object> DetailAsync(DbTx t, HttpContext http, Guid id)
    {
        var j = await RepairCore.LoadAsync(t, id);
        http.User.Demand(Permissions.RepairView, j.BranchId);
        var settings = await RepairCore.SettingsAsync(t);
        var estimates = new List<object>();
        foreach (var e in await t.RowsAsync("""
            SELECT e.id, e.estimate_no AS "estimateNo", e.status, e.net_total AS "netTotal", e.tax_total AS "taxTotal", e.total, e.note, e.sent_at AS "sentAt",
                   e.approval_method AS "approvalMethod", e.approved_at AS "approvedAt", au.display_name_en AS "approvedBy", e.approver_name AS "approverName",
                   e.approval_ref AS "approvalRef", e.decline_reason AS "declineReason", e.declined_at AS "declinedAt", e.created_at AS "createdAt",
                   cu.display_name_en AS "createdBy", e.version
              FROM repair.estimate e JOIN iam."user" cu ON cu.id = e.created_by LEFT JOIN iam."user" au ON au.id = e.approved_by
             WHERE e.job_id = @id ORDER BY e.estimate_no DESC
            """, new { id }))
        {
            var lines = await RepairCore.EstimateLinesAsync(t, (Guid)e["id"]!);
            e["lines"] = lines;
            estimates.Add(e);
        }
        var work = await t.RowsAsync("""
            SELECT l.id, l.kind, l.variant_id AS "variantId", v.sku, p.name_en AS "nameEn", p.name_ar AS "nameAr", l.qty, l.cost, l.return_of_id AS "returnOfId",
                   l.serial, l.vendor, l.reference, EXISTS (SELECT 1 FROM repair.repair_line r WHERE r.return_of_id = l.id AND l.kind = 'external') AS reversed,
                   l.minutes, tu.display_name_en AS "technicianName", l.note, l.created_at AS "createdAt", cu.display_name_en AS "createdBy",
                   CASE WHEN l.kind = 'part' AND l.qty > 0 THEN l.qty + coalesce((SELECT sum(r.qty) FROM repair.repair_line r WHERE r.return_of_id = l.id), 0) END AS "qtyLeft",
                   CASE WHEN l.kind = 'part' AND l.qty > 0 THEN l.cost + coalesce((SELECT sum(r.cost) FROM repair.repair_line r WHERE r.return_of_id = l.id), 0)
                        - coalesce((SELECT sum(w.amount) FROM repair.wip_clearing w WHERE w.line_id = l.id), 0) END AS "costLeft",
                   EXISTS (SELECT 1 FROM repair.wip_clearing w WHERE w.line_id = l.id) AS "writtenOff"
              FROM repair.repair_line l JOIN iam."user" cu ON cu.id = l.created_by LEFT JOIN iam."user" tu ON tu.id = l.technician_id
              LEFT JOIN catalog.variant v ON v.id = l.variant_id LEFT JOIN catalog.product p ON p.id = v.product_id
             WHERE l.job_id = @id ORDER BY l.created_at, l.id
            """, new { id });
        var clearing = await t.RowsAsync("""
            SELECT kind, amount, reason, created_at AS "createdAt" FROM repair.wip_clearing WHERE job_id = @id ORDER BY created_at
            """, new { id });
        var deposits = await t.RowsAsync("""
            SELECT d.id, d.number, d.kind, d.amount, d.tax, d.method, d.reason, s.number AS "saleNumber", d.created_at AS "createdAt", u.display_name_en AS "createdBy"
              FROM repair.deposit d JOIN iam."user" u ON u.id = d.created_by LEFT JOIN sales.sale s ON s.id = d.sale_id
             WHERE d.job_id = @id ORDER BY d.created_at, d.id
            """, new { id });
        var history = await t.RowsAsync("""
            SELECT h.from_status AS "from", h.to_status AS "to", h.note, h.at, u.display_name_en AS "userName"
              FROM repair.status_history h LEFT JOIN iam."user" u ON u.id = h.user_id WHERE h.job_id = @id ORDER BY h.id
            """, new { id });
        var assignments = await t.RowsAsync("""
            SELECT a.at, f.display_name_en AS "fromName", tt.display_name_en AS "toName", b.display_name_en AS "byName"
              FROM repair.assignment a LEFT JOIN iam."user" f ON f.id = a.from_user_id LEFT JOIN iam."user" tt ON tt.id = a.to_user_id
              LEFT JOIN iam."user" b ON b.id = a.by_user_id WHERE a.job_id = @id ORDER BY a.id
            """, new { id });
        var photos = await t.RowsAsync("""
            SELECT id, stage, content_type AS "contentType", size_bytes AS "size", sha256, caption, created_at AS "createdAt"
              FROM repair.photo WHERE job_id = @id ORDER BY created_at
            """, new { id });
        var claims = await t.RowsAsync("SELECT id, number, status FROM repair.repair_job WHERE warranty_of_id = @id ORDER BY created_at", new { id });
        var deposit = await RepairCore.DepositBalanceAsync(t, id);
        var wip = await RepairCore.WipAsync(t, id);
        IReadOnlyList<RepairEstimateLine> charges = j.Outcome is not null ? await RepairCore.ChargesAsync(t, j, settings.DiagnosisFee) : [];
        var chargesTotal = charges.Sum(c => c.GrossAmount);
        var ceiling = await RepairCore.CeilingAsync(t, id);
        // Phase 9: the invoice that stands now (after any corrections at the till), its lines, and the corrections.
        object? invoice = null;
        if (await t.ExecuteScalarAsync<Guid?>("SELECT repair.current_invoice(@id)", new { id }) is { } invoiceId)
            invoice = new
            {
                header = (await t.RowsAsync("""
                    SELECT s.id, s.number, s.sale_date AS "saleDate", s.gross_total AS "grossTotal", s.tax_total AS "taxTotal", s.net_total AS "netTotal",
                           s.cash_rounding AS "cashRounding",
                           (SELECT coalesce(sum(p.amount), 0) FROM sales.payment p WHERE p.sale_id = s.id AND p.method = 'credit') AS "onAccount"
                      FROM sales.sale s WHERE s.id = @invoiceId
                    """, new { invoiceId }))[0],
                lines = await t.RowsAsync("""
                    SELECT l.id, l.repair_kind AS kind, l.variant_id AS "variantId", l.description_en AS "descriptionEn", l.description_ar AS "descriptionAr", l.qty,
                           l.unit_price AS "unitPrice", l.net_amount AS "netAmount", l.tax_amount AS "taxAmount", l.gross_amount AS "grossAmount"
                      FROM sales.sale_line l WHERE l.sale_id = @invoiceId ORDER BY l.line_no
                    """, new { invoiceId }),
            };
        var corrections = await t.RowsAsync("""
            SELECT c.id, cs.number AS "creditedNumber", cn.number AS "creditNoteNumber", cn.gross_total AS credited, co.number AS "correctedNumber",
                   co.gross_total AS "correctedGross", c.reason, c.created_at AS "createdAt", u.display_name_en AS "createdBy"
              FROM repair.invoice_correction c JOIN sales.sale cs ON cs.id = c.credited_sale_id JOIN sales.sale cn ON cn.id = c.credit_note_id
              LEFT JOIN sales.sale co ON co.id = c.corrected_sale_id JOIN iam."user" u ON u.id = c.created_by
             WHERE c.job_id = @id ORDER BY c.created_at
            """, new { id });
        return new
        {
            job = new
            {
                j.Id, j.Number, j.Status, j.Outcome, j.BranchId, j.BranchCode, j.CustomerId, j.CustomerName, j.CustomerNameAr, j.CustomerPhone, j.CustomerOptIn,
                j.CustomerDeviceId, j.Imei1, j.Imei2, j.Serial, j.Brand, j.Model, j.Colour, j.DeviceUnitId, j.CategoryId, j.CategoryCode, j.CategoryNameEn,
                j.CategoryNameAr, j.TechnicianId, j.TechnicianName, j.WarrantyOfId, j.WarrantyOfNumber, j.SoldSaleId, j.SoldSaleNumber, j.Problem, j.FaultCodes,
                condition = JsonDocument.Parse(j.Condition).RootElement, j.ConditionNotes, accessories = JsonDocument.Parse(j.Accessories).RootElement,
                j.PasscodeMode, j.PasscodeHeld, j.PasscodePurgedAt, j.PreAuthorised, ceiling, j.WarrantyDays, j.PromisedAt,
                overdue = j.PromisedAt < t.Clock.UtcNow.UtcDateTime && j.Status is not ("ready" or "delivered" or "cancelled" or "unrepairable" or "completed"),
                j.TermsName, j.TermsAcceptedAt, j.NotifyWhatsapp, j.Lang, j.DeliverySaleId, j.DeliverySaleNumber, j.DeliveredAt, j.DeliveredByName, j.CollectedBy,
                j.CollectionCheck, j.WarrantyUntil, j.RemindedDays, j.CreatedAt, j.CreatedByName, j.Version, qr = QrText(j),
                next = RepairCore.Transitions[j.Status],
            },
            estimates, work, clearing, deposits, history, assignments, photos, claims, parts = await RepairParts.SummaryAsync(t, id), invoice, corrections,
            money = new { deposit, depositTax = await RepairCore.DepositTaxAsync(t, id), wip, charges, chargesTotal, due = Math.Max(0, chargesTotal - deposit),
                          excess = Math.Max(0, deposit - chargesTotal), settings.DiagnosisFee, settings.HourlyRate, settings.DepositVatPoint },
        };
    }

    /// <summary>What the job card's QR code holds: the job number and a check of its id, so a typed number alone does not collect a device.</summary>
    public static string QrText(RepairJob j) => $"KITE-R|{j.Number}|{j.Id.ToString("N")[..12]}";

    private static async Task<object> MessagesAsync(DbTx t, RepairJob j)
    {
        var outbound = await t.RowsAsync("""
            SELECT o.id, 'out' AS direction, o.template, o.params, o.body_text AS "text", o.status, o.status_reason AS "statusReason", o.status_at AS "statusAt",
                   o.created_at AS at, u.display_name_en AS "byName"
              FROM notif.outbound_message o LEFT JOIN iam."user" u ON u.id = o.created_by
             WHERE o.customer_id = @c ORDER BY o.created_at DESC LIMIT 100
            """, new { c = j.CustomerId });
        var inbound = await t.RowsAsync("""
            SELECT id, 'in' AS direction, kind, body AS "text", handled, received_at AS at FROM notif.inbound_message
             WHERE customer_id = @c OR phone_e164 = @p ORDER BY received_at DESC LIMIT 100
            """, new { c = j.CustomerId, p = j.CustomerPhone });
        var last = inbound.Where(r => (string)r["kind"]! != "stop").Select(r => (DateTime)r["at"]!).DefaultIfEmpty().Max();
        return new
        {
            messages = outbound.Concat(inbound).OrderBy(r => (DateTime)r["at"]!).ToList(),
            canReply = last != default && last > t.Clock.UtcNow.UtcDateTime.AddHours(-24),
            replyUntil = last == default ? (DateTime?)null : last.AddHours(24),
            optIn = j.CustomerOptIn, notify = j.NotifyWhatsapp,
        };
    }

    /// <summary>Stores a photo or signature with its SHA-256 (the database checks the hash against the bytes).</summary>
    public static async Task<Guid> SavePhotoAsync(DbTx t, RepairJob j, string stage, string? dataUrl, string? caption, Guid? estimateId, Guid uid)
    {
        var (type, bytes) = ParseImage(dataUrl, stage.EndsWith("signature", StringComparison.Ordinal) ? "signature" : "photo");
        var id = t.NewId();
        var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
        await t.ExecuteAsync("""
            INSERT INTO repair.photo (id, business_id, branch_id, job_id, stage, content_type, size_bytes, sha256, content, caption, estimate_id, created_by)
            VALUES (@id, @b, @BranchId, @jobId, @stage, @type, @size, @sha, @bytes, @caption, @estimateId, @uid)
            """, new { id, b = j.BusinessId, j.BranchId, jobId = j.Id, stage, type, size = bytes.Length, sha, bytes, caption = Clean(caption), estimateId, uid });
        return id;
    }

    private static (string ContentType, byte[] Bytes) ParseImage(string? dataUrl, string field)
    {
        if (string.IsNullOrWhiteSpace(dataUrl)) throw new KiteException(ErrorCodes.Validation, field == "signature" ? "The signature is missing." : "The photo is missing.", 400, field);
        var comma = dataUrl.IndexOf(',', StringComparison.Ordinal);
        var b64 = comma >= 0 && dataUrl.StartsWith("data:", StringComparison.Ordinal) ? dataUrl[(comma + 1)..] : dataUrl;
        byte[] bytes;
        try { bytes = Convert.FromBase64String(b64); }
        catch (FormatException) { throw new KiteException(ErrorCodes.Validation, "The image could not be read.", 400, field); }
        if (bytes.Length is 0 or > 3 * 1024 * 1024) throw new KiteException(ErrorCodes.Validation, "An image is at most 3 MB.", 400, field);
        string type = bytes switch
        {
            [0x89, 0x50, 0x4E, 0x47, ..] => "image/png",
            [0xFF, 0xD8, 0xFF, ..] => "image/jpeg",
            [0x52, 0x49, 0x46, 0x46, _, _, _, _, 0x57, 0x45, 0x42, 0x50, ..] => "image/webp",
            _ => throw new KiteException(ErrorCodes.Validation, "Images are PNG, JPEG or WebP.", 400, field),
        };
        return (type, bytes);
    }
}
