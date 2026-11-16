using System.Text.RegularExpressions;
using Kite.Host.Infrastructure;
using Kite.Persistence;
using Kite.Platform;
using Kite.Sales;

namespace Kite.Host.Features;

public sealed record NameRequest(string NameEn, string NameAr, Guid? ParentId = null, string? Status = null, int? Version = null);
public sealed record VariantInput(string Sku, string? ColourEn = null, string? ColourAr = null, int? StorageGb = null, int? RamGb = null,
    List<string>? Barcodes = null, decimal? Price = null, decimal? MinPrice = null);
public sealed record ProductRequest(string NameEn, string NameAr, string ProductType, Guid TaxCodeId, string? Tracking = null, Guid? BrandId = null,
    Guid? CategoryId = null, int? DefaultWarrantyDays = null, List<VariantInput>? Variants = null, string? Status = null, int? Version = null);
public sealed record VariantUpdateRequest(string Sku, string? ColourEn, string? ColourAr, int? StorageGb, int? RamGb, string Status, int Version);
public sealed record BarcodeRequest(string Code);
public sealed record PriceRequest(decimal Price, decimal? MinPrice = null, DateOnly? EffectiveFrom = null, Guid? PriceListId = null);
public sealed record BranchVariantRequest(decimal? OverridePrice, decimal? OverrideMinPrice, decimal? ReorderLevel, string? BinLocation);
public sealed record CustomerRequest(string NameEn, string? NameAr = null, string? Kind = null, string? Phone = null, string? Email = null,
    string? VatNumber = null, string? CrNumber = null, string? AddressEn = null, string? AddressAr = null, string? Status = null, int? Version = null);

public sealed record BrandDto(Guid Id, string NameEn, string NameAr, string Status, int Version);
public sealed record CategoryDto(Guid Id, Guid? ParentId, string NameEn, string NameAr, string Status, int Version);
public sealed record ProductRow(Guid Id, Guid? BrandId, string? BrandName, Guid? CategoryId, string? CategoryName, string NameEn, string NameAr,
    string ProductType, string Tracking, Guid TaxCodeId, string TaxCode, int DefaultWarrantyDays, string Status, int Version);
public sealed record VariantRow(Guid Id, Guid ProductId, string Sku, string? ColourEn, string? ColourAr, int? StorageGb, int? RamGb, string Status, int Version,
    decimal? Price, decimal? MinPrice, DateTime? PriceFrom);
public sealed record CustomerDto(Guid Id, string Number, string Kind, string NameEn, string? NameAr, string? PhoneE164, string? Email, string? VatNumber,
    string? CrNumber, string? AddressEn, string? AddressAr, string Status, int Version);
public sealed record PosItemDto(Guid VariantId, string Sku, string ProductType, string Tracking, string DescriptionEn, string DescriptionAr, Guid TaxCodeId,
    string TaxKind, decimal TaxRate, decimal? ListPrice, decimal? MinPrice, int WarrantyDays, bool Active, decimal Stock, string? Barcode);

/// <summary>Catalog (9.3): brands, categories, products and variants, barcodes, effective-dated prices, branch overrides; customers; POS item lookup.</summary>
public static partial class CatalogEndpoints
{
    [GeneratedRegex("^[A-Za-z0-9._/-]{1,40}$")] private static partial Regex SkuPattern();
    [GeneratedRegex("^[A-Za-z0-9._/-]{3,64}$")] private static partial Regex BarcodePattern();

    private static readonly string[] ProductTypes = ["device", "accessory", "spare_part", "service", "consumable"];

    private const string ProductSelect = """
        SELECT p.id, p.brand_id AS BrandId, b.name_en AS BrandName, p.category_id AS CategoryId, c.name_en AS CategoryName, p.name_en AS NameEn,
               p.name_ar AS NameAr, p.product_type AS ProductType, p.tracking, p.tax_code_id AS TaxCodeId, tc.code AS TaxCode,
               p.default_warranty_days AS DefaultWarrantyDays, p.status, p.version
          FROM catalog.product p JOIN tax.tax_code tc ON tc.id = p.tax_code_id
          LEFT JOIN catalog.brand b ON b.id = p.brand_id LEFT JOIN catalog.category c ON c.id = p.category_id
        """;

    private const string VariantSelect = """
        SELECT v.id, v.product_id AS ProductId, v.sku, v.colour_en AS ColourEn, v.colour_ar AS ColourAr, v.storage_gb AS StorageGb, v.ram_gb AS RamGb,
               v.status, v.version, pr.price, pr.min_price AS MinPrice, pr.effective_from AS PriceFrom
          FROM catalog.variant v
          LEFT JOIN catalog.price pr ON pr.variant_id = v.id
               AND pr.price_list_id = (SELECT pl.id FROM catalog.price_list pl WHERE pl.business_id = v.business_id AND pl.is_default)
               AND pr.effective_from <= @today AND (pr.effective_to IS NULL OR pr.effective_to > @today)
        """;

    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api/v1/catalog");

        // ---------------------------------------------------------- brands and categories
        g.MapGet("/brands", async (HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), t =>
                t.QueryAsync<BrandDto>("SELECT id, name_en AS NameEn, name_ar AS NameAr, status, version FROM catalog.brand ORDER BY lower(name_en)"), ct)))
            .RequirePermission(Permissions.CatalogView);

        g.MapPost("/brands", async (NameRequest req, HttpContext http, KiteDb db, ServerContext server, CancellationToken ct) =>
        {
            CheckNames(req.NameEn, req.NameAr);
            var dto = await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                var id = t.NewId();
                await t.ExecuteAsync("INSERT INTO catalog.brand (id, business_id, name_en, name_ar, created_by) VALUES (@id, @b, @en, @ar, @uid)",
                    new { id, b = server.BusinessId, en = req.NameEn.Trim(), ar = req.NameAr.Trim(), uid = http.User.UserIdOf() });
                await t.AuditAsync(new AuditEntry("catalog.brand_created", "brand", id, After: Json.Obj(req)));
                await t.PublishAsync("catalog.brand_created", "brand", id, 1, Json.Obj(new { req.NameEn, req.NameAr }));
                return await t.QuerySingleOrDefaultAsync<BrandDto>("SELECT id, name_en AS NameEn, name_ar AS NameAr, status, version FROM catalog.brand WHERE id = @id", new { id });
            }, ct);
            return Results.Created($"/api/v1/catalog/brands/{dto!.Id}", dto);
        }).RequirePermission(Permissions.CatalogManage);

        g.MapGet("/categories", async (HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), t =>
                t.QueryAsync<CategoryDto>("SELECT id, parent_id AS ParentId, name_en AS NameEn, name_ar AS NameAr, status, version FROM catalog.category ORDER BY lower(name_en)"), ct)))
            .RequirePermission(Permissions.CatalogView);

        g.MapPost("/categories", async (NameRequest req, HttpContext http, KiteDb db, ServerContext server, CancellationToken ct) =>
        {
            CheckNames(req.NameEn, req.NameAr);
            var dto = await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                var id = t.NewId();
                await t.ExecuteAsync("INSERT INTO catalog.category (id, business_id, parent_id, name_en, name_ar, created_by) VALUES (@id, @b, @ParentId, @en, @ar, @uid)",
                    new { id, b = server.BusinessId, req.ParentId, en = req.NameEn.Trim(), ar = req.NameAr.Trim(), uid = http.User.UserIdOf() });
                await t.AuditAsync(new AuditEntry("catalog.category_created", "category", id, After: Json.Obj(req)));
                await t.PublishAsync("catalog.category_created", "category", id, 1, Json.Obj(new { req.NameEn, req.NameAr, req.ParentId }));
                return await t.QuerySingleOrDefaultAsync<CategoryDto>("SELECT id, parent_id AS ParentId, name_en AS NameEn, name_ar AS NameAr, status, version FROM catalog.category WHERE id = @id", new { id });
            }, ct);
            return Results.Created($"/api/v1/catalog/categories/{dto!.Id}", dto);
        }).RequirePermission(Permissions.CatalogManage);

        g.MapGet("/price-lists", async (HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), t =>
                t.RowsAsync("SELECT id, code, name_en AS \"nameEn\", name_ar AS \"nameAr\", is_default AS \"isDefault\", status FROM catalog.price_list ORDER BY is_default DESC, code"), ct)))
            .RequirePermission(Permissions.CatalogView);

        // ---------------------------------------------------------- products and variants
        g.MapGet("/products", async (string? q, string? type, string? status, int? limit, HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                var today = BusinessDay.Today((await BusinessInfo.LoadAsync(t)).TimeZone, t.Clock).ToDateTime(TimeOnly.MinValue);
                var products = (await t.QueryAsync<ProductRow>(ProductSelect + """
                     WHERE (@q::text IS NULL OR p.name_en ILIKE '%' || @q || '%' OR p.name_ar ILIKE '%' || @q || '%'
                            OR EXISTS (SELECT 1 FROM catalog.variant v WHERE v.product_id = p.id AND upper(v.sku) LIKE '%' || upper(@q) || '%')
                            OR EXISTS (SELECT 1 FROM catalog.barcode bc JOIN catalog.variant v ON v.id = bc.variant_id WHERE v.product_id = p.id AND bc.code = @q))
                       AND (@type::text IS NULL OR p.product_type = @type) AND (@status::text IS NULL OR p.status = @status)
                     ORDER BY lower(p.name_en) LIMIT @limit
                    """, new { q = Clean(q), type = Clean(type), status = Clean(status), limit = Math.Clamp(limit ?? 100, 1, 500) })).ToArray();
                return await WithVariantsAsync(t, products, today);
            }, ct))).RequirePermission(Permissions.CatalogView);

        g.MapGet("/products/{id:guid}", async (Guid id, HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                var p = await t.QuerySingleOrDefaultAsync<ProductRow>(ProductSelect + " WHERE p.id = @id", new { id })
                    ?? throw new KiteException(ErrorCodes.NotFound, "Product not found.", 404);
                var today = BusinessDay.Today((await BusinessInfo.LoadAsync(t)).TimeZone, t.Clock).ToDateTime(TimeOnly.MinValue);
                return (await WithVariantsAsync(t, [p], today))[0];
            }, ct))).RequirePermission(Permissions.CatalogView);

        g.MapPost("/products", async (ProductRequest req, HttpContext http, KiteDb db, ServerContext server, CancellationToken ct) =>
        {
            CheckProduct(req);
            if (req.Variants is null || req.Variants.Count == 0) throw new KiteException(ErrorCodes.Validation, "Add at least one variant (SKU).", 400, "variants");
            var hasPrice = req.Variants.Any(v => v.Price is not null);
            if (hasPrice) http.User.Demand(Permissions.CatalogPriceManage, null);
            var result = await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                var uid = http.User.UserIdOf();
                var id = t.NewId();
                var tracking = req.Tracking ?? (req.ProductType == "device" ? "imei" : "none");
                await t.ExecuteAsync("""
                    INSERT INTO catalog.product (id, business_id, brand_id, category_id, name_en, name_ar, product_type, tracking, tax_code_id, default_warranty_days, created_by)
                    VALUES (@id, @b, @BrandId, @CategoryId, @en, @ar, @ProductType, @tracking, @TaxCodeId, @warranty, @uid)
                    """, new { id, b = server.BusinessId, req.BrandId, req.CategoryId, en = req.NameEn.Trim(), ar = req.NameAr.Trim(), req.ProductType, tracking,
                               req.TaxCodeId, warranty = req.DefaultWarrantyDays ?? 0, uid });
                var biz = await BusinessInfo.LoadAsync(t);
                var today = BusinessDay.Today(biz.TimeZone, t.Clock);
                foreach (var v in req.Variants!) await AddVariantAsync(t, server.BusinessId!.Value, id, v, uid, today);
                await t.AuditAsync(new AuditEntry("catalog.product_created", "product", id, After: Json.Obj(req)));
                await t.PublishAsync("catalog.product_created", "product", id, 1, Json.Obj(req));
                var p = await t.QuerySingleOrDefaultAsync<ProductRow>(ProductSelect + " WHERE p.id = @id", new { id });
                return (await WithVariantsAsync(t, [p!], today.ToDateTime(TimeOnly.MinValue)))[0];
            }, ct);
            return Results.Created($"/api/v1/catalog/products/{result.Product.Id}", result);
        }).RequirePermission(Permissions.CatalogManage);

        g.MapPut("/products/{id:guid}", async (Guid id, ProductRequest req, HttpContext http, KiteDb db, CancellationToken ct) =>
        {
            CheckProduct(req);
            if (req.Version is null) throw new KiteException(ErrorCodes.Validation, "Version is required.", 400, "version");
            var result = await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                var before = await t.QuerySingleOrDefaultAsync<ProductRow>(ProductSelect + " WHERE p.id = @id FOR UPDATE OF p", new { id })
                    ?? throw new KiteException(ErrorCodes.NotFound, "Product not found.", 404);
                if (before.Version != req.Version) throw new KiteException(ErrorCodes.VersionMismatch, "Someone else changed this product. Reload and try again.", 412);
                var tracking = req.Tracking ?? before.Tracking;
                if (tracking != before.Tracking && await t.ExecuteScalarAsync<bool>(
                        "SELECT EXISTS (SELECT 1 FROM inv.stock_movement m JOIN catalog.variant v ON v.id = m.variant_id WHERE v.product_id = @id)", new { id }))
                    throw new KiteException(ErrorCodes.Validation, "Tracking cannot change once the product has stock movements.", 400, "tracking");
                if (req.ProductType != before.ProductType && await t.ExecuteScalarAsync<bool>(
                        "SELECT EXISTS (SELECT 1 FROM inv.stock_movement m JOIN catalog.variant v ON v.id = m.variant_id WHERE v.product_id = @id)", new { id }))
                    throw new KiteException(ErrorCodes.Validation, "The product type decides its accounts; it cannot change once the product has stock movements.", 400, "productType");
                var status = req.Status ?? before.Status;
                if (status is not ("active" or "inactive")) throw new KiteException(ErrorCodes.Validation, "Status must be active or inactive.", 400, "status");
                await t.ExecuteAsync("""
                    UPDATE catalog.product SET brand_id = @BrandId, category_id = @CategoryId, name_en = @en, name_ar = @ar, product_type = @ProductType,
                           tracking = @tracking, tax_code_id = @TaxCodeId, default_warranty_days = @warranty, status = @status, updated_by = @uid WHERE id = @id
                    """, new { id, req.BrandId, req.CategoryId, en = req.NameEn.Trim(), ar = req.NameAr.Trim(), req.ProductType, tracking, req.TaxCodeId,
                               warranty = req.DefaultWarrantyDays ?? before.DefaultWarrantyDays, status, uid = http.User.UserIdOf() });
                await t.AuditAsync(new AuditEntry("catalog.product_changed", "product", id, Before: Json.Obj(before), After: Json.Obj(req)));
                await t.PublishAsync("catalog.product_changed", "product", id, before.Version + 1, Json.Obj(req));
                var p = await t.QuerySingleOrDefaultAsync<ProductRow>(ProductSelect + " WHERE p.id = @id", new { id });
                var today = BusinessDay.Today((await BusinessInfo.LoadAsync(t)).TimeZone, t.Clock).ToDateTime(TimeOnly.MinValue);
                return (await WithVariantsAsync(t, [p!], today))[0];
            }, ct);
            return Results.Ok(result);
        }).RequirePermission(Permissions.CatalogManage);

        g.MapPost("/products/{id:guid}/variants", async (Guid id, VariantInput req, HttpContext http, KiteDb db, ServerContext server, CancellationToken ct) =>
        {
            if (req.Price is not null) http.User.Demand(Permissions.CatalogPriceManage, null);
            var vid = await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                if (!await t.ExecuteScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM catalog.product WHERE id = @id)", new { id }))
                    throw new KiteException(ErrorCodes.NotFound, "Product not found.", 404);
                var biz = await BusinessInfo.LoadAsync(t);
                var v = await AddVariantAsync(t, server.BusinessId!.Value, id, req, http.User.UserIdOf(), BusinessDay.Today(biz.TimeZone, t.Clock));
                await t.AuditAsync(new AuditEntry("catalog.variant_created", "variant", v, After: Json.Obj(req)));
                return v;
            }, ct);
            return Results.Created($"/api/v1/catalog/products/{id}", new { id = vid });
        }).RequirePermission(Permissions.CatalogManage);

        g.MapPut("/variants/{id:guid}", async (Guid id, VariantUpdateRequest req, HttpContext http, KiteDb db, CancellationToken ct) =>
        {
            var sku = (req.Sku ?? "").Trim();
            if (!SkuPattern().IsMatch(sku)) throw new KiteException(ErrorCodes.Validation, "SKU must be 1 to 40 letters, digits or . _ / -", 400, "sku");
            if (req.Status is not ("active" or "inactive")) throw new KiteException(ErrorCodes.Validation, "Status must be active or inactive.", 400, "status");
            await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                var n = await t.ExecuteAsync("""
                    UPDATE catalog.variant SET sku = @sku, colour_en = @ColourEn, colour_ar = @ColourAr, storage_gb = @StorageGb, ram_gb = @RamGb, status = @Status,
                           updated_by = @uid WHERE id = @id AND version = @Version
                    """, new { id, sku, req.ColourEn, req.ColourAr, req.StorageGb, req.RamGb, req.Status, req.Version, uid = http.User.UserIdOf() });
                if (n == 0) throw new KiteException(ErrorCodes.VersionMismatch, "Someone else changed this variant, or it does not exist. Reload and try again.", 412);
                await t.AuditAsync(new AuditEntry("catalog.variant_changed", "variant", id, After: Json.Obj(req)));
                await t.PublishAsync("catalog.variant_changed", "variant", id, req.Version + 1, Json.Obj(req));
            }, ct);
            return Results.NoContent();
        }).RequirePermission(Permissions.CatalogManage);

        g.MapPost("/variants/{id:guid}/barcodes", async (Guid id, BarcodeRequest req, HttpContext http, KiteDb db, ServerContext server, CancellationToken ct) =>
        {
            var code = (req.Code ?? "").Trim();
            if (!BarcodePattern().IsMatch(code)) throw new KiteException(ErrorCodes.Validation, "A barcode is 3 to 64 letters, digits or . _ / -", 400, "code");
            var bid = await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                var bid = t.NewId();
                await t.ExecuteAsync("INSERT INTO catalog.barcode (id, business_id, variant_id, code, created_by) VALUES (@bid, @b, @id, @code, @uid)",
                    new { bid, b = server.BusinessId, id, code, uid = http.User.UserIdOf() });
                await t.AuditAsync(new AuditEntry("catalog.barcode_added", "variant", id, After: Json.Obj(new { code })));
                await t.PublishAsync("catalog.barcode_added", "barcode", bid, 1, Json.Obj(new { variantId = id, code }));
                return bid;
            }, ct);
            return Results.Created($"/api/v1/catalog/barcodes/{bid}", new { id = bid, code });
        }).RequirePermission(Permissions.CatalogManage);

        g.MapDelete("/barcodes/{id:guid}", async (Guid id, HttpContext http, KiteDb db, CancellationToken ct) =>
        {
            await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                var code = await t.ExecuteScalarAsync<string?>("DELETE FROM catalog.barcode WHERE id = @id RETURNING code", new { id })
                    ?? throw new KiteException(ErrorCodes.NotFound, "Barcode not found.", 404);
                await t.AuditAsync(new AuditEntry("catalog.barcode_removed", "barcode", id, Before: Json.Obj(new { code })));
                await t.PublishAsync("catalog.barcode_removed", "barcode", id, 2, Json.Obj(new { code }));
            }, ct);
            return Results.NoContent();
        }).RequirePermission(Permissions.CatalogManage);

        // A new price closes the current one the day before it starts (9.4: prices never overlap).
        g.MapPost("/variants/{id:guid}/prices", async (Guid id, PriceRequest req, HttpContext http, KiteDb db, CancellationToken ct) =>
        {
            await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                var biz = await BusinessInfo.LoadAsync(t);
                var from = req.EffectiveFrom ?? BusinessDay.Today(biz.TimeZone, t.Clock);
                await SetPriceAsync(t, id, req.PriceListId, req.Price, req.MinPrice, from, http.User.UserIdOf());
                await t.AuditAsync(new AuditEntry("catalog.price_set", "variant", id, After: Json.Obj(new { req.Price, req.MinPrice, from, req.PriceListId })));
                await t.PublishAsync("catalog.price_set", "variant", id, 1, Json.Obj(new { req.Price, req.MinPrice, from, req.PriceListId }));
            }, ct);
            return Results.NoContent();
        }).RequirePermission(Permissions.CatalogPriceManage);

        g.MapGet("/variants/{id:guid}/prices", async (Guid id, HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), t => t.RowsAsync("""
                SELECT pr.id, pl.code AS "priceList", pr.price, pr.min_price AS "minPrice", pr.effective_from AS "effectiveFrom", pr.effective_to AS "effectiveTo"
                  FROM catalog.price pr JOIN catalog.price_list pl ON pl.id = pr.price_list_id WHERE pr.variant_id = @id ORDER BY pr.effective_from DESC
                """, new { id }), ct)))
            .RequirePermission(Permissions.CatalogView);

        g.MapPut("/variants/{id:guid}/branches/{branchId:guid}", async (Guid id, Guid branchId, BranchVariantRequest req, HttpContext http, KiteDb db, ServerContext server, CancellationToken ct) =>
        {
            http.User.Demand(Permissions.CatalogPriceManage, branchId);
            await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                await t.ExecuteAsync("""
                    INSERT INTO catalog.branch_variant (id, business_id, branch_id, variant_id, override_price, override_min_price, reorder_level, bin_location, created_by)
                    VALUES (@nid, @b, @branchId, @id, @OverridePrice, @OverrideMinPrice, @ReorderLevel, @BinLocation, @uid)
                    ON CONFLICT (branch_id, variant_id) DO UPDATE SET override_price = EXCLUDED.override_price, override_min_price = EXCLUDED.override_min_price,
                           reorder_level = EXCLUDED.reorder_level, bin_location = EXCLUDED.bin_location, updated_by = EXCLUDED.created_by
                    """, new { nid = t.NewId(), b = server.BusinessId, branchId, id, req.OverridePrice, req.OverrideMinPrice, req.ReorderLevel, req.BinLocation, uid = http.User.UserIdOf() });
                await t.AuditAsync(new AuditEntry("catalog.branch_price_set", "variant", id, After: Json.Obj(req), BranchId: branchId));
                await t.PublishAsync("catalog.branch_variant_set", "variant", id, 1, Json.Obj(req), branchId);
            }, ct);
            return Results.NoContent();
        }).RequireSignedIn();

        // ---------------------------------------------------------- POS lookup: barcode, SKU or name, with price and shop stock
        app.MapGet("/api/v1/pos/items", async (string? q, Guid? branchId, int? limit, HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                var (branch, warehouse) = await BranchForLookupAsync(t, http, branchId);
                var biz = await BusinessInfo.LoadAsync(t);
                var term = Clean(q);
                return await t.QueryAsync<PosItemDto>(PosItemSql("""
                    (@q::text IS NULL OR EXISTS (SELECT 1 FROM catalog.barcode bc WHERE bc.variant_id = v.id AND bc.code = @q)
                      OR upper(v.sku) = upper(@q) OR p.name_en ILIKE '%' || @q || '%' OR p.name_ar ILIKE '%' || @q || '%' OR upper(v.sku) LIKE upper(@q) || '%')
                    """) + """
                     ORDER BY (EXISTS (SELECT 1 FROM catalog.barcode bc WHERE bc.variant_id = v.id AND bc.code = @q)) DESC,
                              (upper(v.sku) = upper(coalesce(@q, ''))) DESC, (v.status = 'active' AND p.status = 'active') DESC, lower(p.name_en), v.sku
                     LIMIT @limit
                    """, new { q = term, branchId = branch, warehouse, d = BusinessDay.Today(biz.TimeZone, t.Clock).ToDateTime(TimeOnly.MinValue),
                               limit = Math.Clamp(limit ?? 30, 1, 5000) });
            }, ct))).RequireSignedIn();

        app.MapGet("/api/v1/pos/items/barcode/{code}", async (string code, Guid? branchId, HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                var (branch, warehouse) = await BranchForLookupAsync(t, http, branchId);
                var biz = await BusinessInfo.LoadAsync(t);
                return await t.QuerySingleOrDefaultAsync<PosItemDto>(PosItemSql("""
                    (EXISTS (SELECT 1 FROM catalog.barcode bc WHERE bc.variant_id = v.id AND bc.code = @q) OR upper(v.sku) = upper(@q))
                    """) + " LIMIT 1", new { q = code.Trim(), branchId = branch, warehouse, d = BusinessDay.Today(biz.TimeZone, t.Clock).ToDateTime(TimeOnly.MinValue) })
                    ?? throw new KiteException(ErrorCodes.BarcodeUnknown, $"No item has barcode or SKU {code}.", 404, "code");
            }, ct))).RequireSignedIn();

        MapCustomers(app);
    }

    public static string PosItemSql(string where) =>
        ItemPricing.Select(where).Replace(" FROM catalog.variant v", """
            , coalesce((SELECT sb.qty FROM inv.stock_balance sb WHERE sb.variant_id = v.id AND sb.warehouse_id = @warehouse), 0) AS Stock,
              (SELECT bc.code FROM catalog.barcode bc WHERE bc.variant_id = v.id ORDER BY bc.created_at LIMIT 1) AS Barcode
              FROM catalog.variant v
            """, StringComparison.Ordinal);

    private static async Task<(Guid Branch, Guid? Warehouse)> BranchForLookupAsync(DbTx t, HttpContext http, Guid? branchId)
    {
        Guid branch;
        if (branchId is { } b) branch = b;
        else if (http.User.TerminalIdOf() is { } tid) branch = (await PosTerminals.ByIdAsync(t, tid)).BranchId;
        else throw new KiteException(ErrorCodes.Validation, "Choose a branch.", 400, "branchId");
        if (!http.User.HasIn(Permissions.PosSell, branch)) http.User.Demand(Permissions.CatalogView, branch);
        var warehouse = await t.ExecuteScalarAsync<Guid?>("""
            SELECT id FROM core.warehouse WHERE branch_id = @branch AND status = 'active' ORDER BY (kind = 'shop') DESC, (code = 'SHOP') DESC, created_at LIMIT 1
            """, new { branch });
        return (branch, warehouse);
    }

    public sealed record ProductView(ProductRow Product, IReadOnlyList<VariantView> Variants);
    public sealed record VariantView(VariantRow Variant, IReadOnlyList<BarcodeView> Barcodes);
    public sealed record BarcodeView(Guid Id, string Code);

    private static async Task<ProductView[]> WithVariantsAsync(DbTx t, ProductRow[] products, DateTime today)
    {
        if (products.Length == 0) return [];
        var ids = products.Select(p => p.Id).ToArray();
        var variants = (await t.QueryAsync<VariantRow>(VariantSelect + " WHERE v.product_id = ANY(@ids) ORDER BY v.sku", new { ids, today })).ToArray();
        var vids = variants.Select(v => v.Id).ToArray();
        var barcodes = (await t.QueryAsync<(Guid Id, Guid VariantId, string Code)>("SELECT id, variant_id, code FROM catalog.barcode WHERE variant_id = ANY(@vids) ORDER BY created_at", new { vids }))
            .ToLookup(b => b.VariantId);
        var byProduct = variants.ToLookup(v => v.ProductId);
        return products.Select(p => new ProductView(p, byProduct[p.Id].Select(v =>
            new VariantView(v, barcodes[v.Id].Select(b => new BarcodeView(b.Id, b.Code)).ToArray())).ToArray())).ToArray();
    }

    private static async Task<Guid> AddVariantAsync(DbTx t, Guid businessId, Guid productId, VariantInput v, Guid uid, DateOnly today)
    {
        var sku = (v.Sku ?? "").Trim();
        if (!SkuPattern().IsMatch(sku)) throw new KiteException(ErrorCodes.Validation, $"SKU '{sku}' must be 1 to 40 letters, digits or . _ / -", 400, "sku");
        var id = t.NewId();
        await t.ExecuteAsync("""
            INSERT INTO catalog.variant (id, business_id, product_id, sku, colour_en, colour_ar, storage_gb, ram_gb, created_by)
            VALUES (@id, @businessId, @productId, @sku, @ColourEn, @ColourAr, @StorageGb, @RamGb, @uid)
            """, new { id, businessId, productId, sku, v.ColourEn, v.ColourAr, v.StorageGb, v.RamGb, uid });
        foreach (var raw in v.Barcodes ?? [])
        {
            var code = raw.Trim();
            if (!BarcodePattern().IsMatch(code)) throw new KiteException(ErrorCodes.Validation, $"Barcode '{code}' is 3 to 64 letters, digits or . _ / -", 400, "barcodes");
            await t.ExecuteAsync("INSERT INTO catalog.barcode (id, business_id, variant_id, code, created_by) VALUES (@bid, @businessId, @id, @code, @uid)",
                new { bid = t.NewId(), businessId, id, code, uid });
        }
        if (v.Price is { } price) await SetPriceAsync(t, id, null, price, v.MinPrice, today, uid);
        return id;
    }

    private static async Task SetPriceAsync(DbTx t, Guid variantId, Guid? priceListId, decimal price, decimal? minPrice, DateOnly from, Guid uid)
    {
        if (price < 0 || price != Currency.OMR.Round(price)) throw new KiteException(ErrorCodes.Validation, "Price must be zero or more with at most 3 decimals.", 400, "price");
        if (minPrice is { } m && (m < 0 || m > price || m != Currency.OMR.Round(m)))
            throw new KiteException(ErrorCodes.Validation, "The minimum price must be between zero and the price.", 400, "minPrice");
        var list = priceListId ?? await t.ExecuteScalarAsync<Guid?>("SELECT id FROM catalog.price_list WHERE is_default")
            ?? throw new KiteException(ErrorCodes.Validation, "There is no default price list.", 400, "priceListId");
        var d = from.ToDateTime(TimeOnly.MinValue);
        if (await t.ExecuteScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM catalog.price WHERE price_list_id = @list AND variant_id = @variantId AND effective_from > @d)",
                new { list, variantId, d }))
            throw new KiteException(ErrorCodes.Validation, "A later price is already set; change that one instead.", 400, "effectiveFrom");
        var same = await t.ExecuteAsync("""
            UPDATE catalog.price SET price = @price, min_price = @minPrice, updated_by = @uid
             WHERE price_list_id = @list AND variant_id = @variantId AND effective_from = @d
            """, new { list, variantId, d, price, minPrice, uid });
        if (same > 0) return;
        await t.ExecuteAsync("""
            UPDATE catalog.price SET effective_to = @d, updated_by = @uid
             WHERE price_list_id = @list AND variant_id = @variantId AND effective_from < @d AND (effective_to IS NULL OR effective_to > @d)
            """, new { list, variantId, d, uid });
        await t.ExecuteAsync("""
            INSERT INTO catalog.price (id, price_list_id, variant_id, price, min_price, effective_from, created_by)
            VALUES (@id, @list, @variantId, @price, @minPrice, @d, @uid)
            """, new { id = t.NewId(), list, variantId, price, minPrice, d, uid });
    }

    private static void CheckNames(string? en, string? ar)
    {
        if (string.IsNullOrWhiteSpace(en)) throw new KiteException(ErrorCodes.Validation, "English name is required.", 400, "nameEn");
        if (string.IsNullOrWhiteSpace(ar)) throw new KiteException(ErrorCodes.Validation, "Arabic name is required.", 400, "nameAr");
    }

    private static void CheckProduct(ProductRequest req)
    {
        CheckNames(req.NameEn, req.NameAr);
        if (!ProductTypes.Contains(req.ProductType)) throw new KiteException(ErrorCodes.Validation, "Type must be device, accessory, spare_part, service or consumable.", 400, "productType");
        var tracking = req.Tracking ?? (req.ProductType == "device" ? "imei" : "none");
        if (tracking is not ("none" or "imei" or "serial")) throw new KiteException(ErrorCodes.Validation, "Tracking must be none, imei or serial.", 400, "tracking");
        if (req.ProductType == "service" && tracking != "none") throw new KiteException(ErrorCodes.Validation, "Services are not tracked.", 400, "tracking");
        if (tracking == "imei" && req.ProductType != "device") throw new KiteException(ErrorCodes.Validation, "Only devices are tracked by IMEI.", 400, "tracking");
        if (req.DefaultWarrantyDays is < 0 or > 3650) throw new KiteException(ErrorCodes.Validation, "Warranty is 0 to 3650 days.", 400, "defaultWarrantyDays");
    }

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    // ---------------------------------------------------------- customers (credit arrives in Phase 10)
    private const string CustomerSelect = """
        SELECT id, number, kind, name_en AS NameEn, name_ar AS NameAr, phone_e164 AS PhoneE164, email, vat_number AS VatNumber, cr_number AS CrNumber,
               address_en AS AddressEn, address_ar AS AddressAr, status, version FROM crm.customer
        """;

    private static void MapCustomers(WebApplication app)
    {
        var g = app.MapGroup("/api/v1/customers");

        g.MapGet("", async (string? q, int? limit, HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), t => t.QueryAsync<CustomerDto>(CustomerSelect + """
                 WHERE status <> 'merged' AND (@q::text IS NULL OR name_en ILIKE '%' || @q || '%' OR name_ar ILIKE '%' || @q || '%' OR number ILIKE '%' || @q || '%'
                        OR phone_e164 LIKE '%' || regexp_replace(@q, '[^0-9]', '', 'g') || '%' AND length(regexp_replace(@q, '[^0-9]', '', 'g')) >= 4)
                 ORDER BY lower(name_en) LIMIT @limit
                """, new { q = Clean(q), limit = Math.Clamp(limit ?? 50, 1, 500) }), ct)))
            .RequirePermission(Permissions.CustomersView);

        g.MapGet("/{id:guid}", async (Guid id, HttpContext http, KiteDb db, CancellationToken ct) =>
            Results.Ok(await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                var c = await t.QuerySingleOrDefaultAsync<CustomerDto>(CustomerSelect + " WHERE id = @id", new { id })
                    ?? throw new KiteException(ErrorCodes.NotFound, "Customer not found.", 404);
                var sales = await t.RowsAsync("""
                    SELECT id, number, doc_type AS "docType", completed_at AS "completedAt", gross_total AS "grossTotal" FROM sales.sale
                     WHERE customer_id = @id ORDER BY completed_at DESC LIMIT 50
                    """, new { id });
                return new { customer = c, recentSales = sales };
            }, ct))).RequirePermission(Permissions.CustomersView);

        g.MapPost("", async (CustomerRequest req, HttpContext http, KiteDb db, ServerContext server, CancellationToken ct) =>
        {
            var (kind, phone) = CheckCustomer(req);
            var dto = await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                var id = t.NewId();
                var number = await DocNumbers.NextAsync(t, server.BusinessId!.Value, null, "customer", 2000, "C");
                Guid? originBranch = http.User.TerminalIdOf() is { } tid ? (await PosTerminals.ByIdAsync(t, tid)).BranchId : server.BranchId;
                await t.ExecuteAsync("""
                    INSERT INTO crm.customer (id, business_id, number, kind, name_en, name_ar, phone_e164, email, vat_number, cr_number, address_en, address_ar,
                                              origin_branch_id, created_by, origin_server_id)
                    VALUES (@id, @b, @number, @kind, @en, @NameAr, @phone, @Email, @VatNumber, @CrNumber, @AddressEn, @AddressAr, @originBranch, @uid,
                            (SELECT id FROM core.server WHERE is_self))
                    """, new { id, b = server.BusinessId, number, kind, en = req.NameEn.Trim(), req.NameAr, phone, req.Email, req.VatNumber, req.CrNumber,
                               req.AddressEn, req.AddressAr, originBranch, uid = http.User.UserIdOf() });
                await t.AuditAsync(new AuditEntry("crm.customer_created", "customer", id, After: Json.Obj(new { number, req.NameEn, phone })));
                await t.PublishAsync("crm.customer_created", "customer", id, 1, Json.Obj(new { number, kind, req.NameEn, req.NameAr, phone, req.Email, req.VatNumber }));
                return await t.QuerySingleOrDefaultAsync<CustomerDto>(CustomerSelect + " WHERE id = @id", new { id });
            }, ct);
            return Results.Created($"/api/v1/customers/{dto!.Id}", dto);
        }).RequirePermission(Permissions.CustomersManage);

        g.MapPut("/{id:guid}", async (Guid id, CustomerRequest req, HttpContext http, KiteDb db, CancellationToken ct) =>
        {
            var (kind, phone) = CheckCustomer(req);
            if (req.Version is null) throw new KiteException(ErrorCodes.Validation, "Version is required.", 400, "version");
            var status = req.Status ?? "active";
            if (status is not ("active" or "inactive")) throw new KiteException(ErrorCodes.Validation, "Status must be active or inactive.", 400, "status");
            var dto = await db.InTransactionAsync(http.User.ScopeOf(), http.ActorOf(), async t =>
            {
                var before = await t.QuerySingleOrDefaultAsync<CustomerDto>(CustomerSelect + " WHERE id = @id FOR UPDATE", new { id })
                    ?? throw new KiteException(ErrorCodes.NotFound, "Customer not found.", 404);
                if (before.Version != req.Version) throw new KiteException(ErrorCodes.VersionMismatch, "Someone else changed this customer. Reload and try again.", 412);
                await t.ExecuteAsync("""
                    UPDATE crm.customer SET kind = @kind, name_en = @en, name_ar = @NameAr, phone_e164 = @phone, email = @Email, vat_number = @VatNumber,
                           cr_number = @CrNumber, address_en = @AddressEn, address_ar = @AddressAr, status = @status, updated_by = @uid WHERE id = @id
                    """, new { id, kind, en = req.NameEn.Trim(), req.NameAr, phone, req.Email, req.VatNumber, req.CrNumber, req.AddressEn, req.AddressAr, status, uid = http.User.UserIdOf() });
                await t.AuditAsync(new AuditEntry("crm.customer_changed", "customer", id, Before: Json.Obj(before), After: Json.Obj(req)));
                await t.PublishAsync("crm.customer_changed", "customer", id, before.Version + 1, Json.Obj(new { kind, req.NameEn, req.NameAr, phone, req.Email, req.VatNumber, status }));
                return await t.QuerySingleOrDefaultAsync<CustomerDto>(CustomerSelect + " WHERE id = @id", new { id });
            }, ct);
            return Results.Ok(dto);
        }).RequirePermission(Permissions.CustomersManage);
    }

    /// <summary>Oman numbers may be typed locally (8 digits) and are stored in E.164 (+968...).</summary>
    public static string? NormalisePhone(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var digits = new string(raw.Where(char.IsAsciiDigit).ToArray());
        var plus = raw.TrimStart().StartsWith('+') || raw.TrimStart().StartsWith("00", StringComparison.Ordinal);
        if (raw.TrimStart().StartsWith("00", StringComparison.Ordinal)) digits = digits[2..];
        var e164 = plus ? "+" + digits : digits.Length == 8 ? "+968" + digits : "+" + digits;
        if (!Regex.IsMatch(e164, @"^\+[1-9][0-9]{6,14}$"))
            throw new KiteException(ErrorCodes.Validation, "Enter the phone number with its country code, or 8 digits for Oman.", 400, "phone");
        return e164;
    }

    private static (string Kind, string? Phone) CheckCustomer(CustomerRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.NameEn)) throw new KiteException(ErrorCodes.Validation, "Name is required.", 400, "nameEn");
        var kind = req.Kind ?? "individual";
        if (kind is not ("individual" or "company")) throw new KiteException(ErrorCodes.Validation, "Kind must be individual or company.", 400, "kind");
        if (req.Email is { Length: > 0 } e && !Regex.IsMatch(e, @"^[^@\s]+@[^@\s]+\.[^@\s]+$")) throw new KiteException(ErrorCodes.Validation, "That email address is not valid.", 400, "email");
        return (kind, NormalisePhone(req.Phone));
    }
}
