using Kite.Platform;
using Microsoft.AspNetCore.Diagnostics;
using Npgsql;

namespace Kite.Host.Infrastructure;

/// <summary>
/// Turns errors into RFC 9457 Problem Details with a stable "code" (blueprint 22.2).
/// Database rule violations (KT00x, unique, check) become clear 400/409 answers.
/// </summary>
public static class ErrorHandling
{
    public static IApplicationBuilder UseKiteErrors(this IApplicationBuilder app)
    {
        app.UseExceptionHandler(handler => handler.Run(async ctx =>
        {
            var ex = ctx.Features.Get<IExceptionHandlerFeature>()?.Error;
            var (status, code, title, field) = Map(ex);
            if (status >= 500)
                ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Kite.Errors").LogError(ex, "Unhandled error");
            await WriteAsync(ctx, status, code, title, field);
        }));
        // Expected refusals (sign in needed, no permission, licence, validation) are answers, not failures. They are written
        // here so the exception handler middleware does not log every one of them as an unhandled error.
        return app.Use(async (ctx, next) =>
        {
            try { await next(ctx); }
            catch (KiteException k) when (k.HttpStatus < 500 && !ctx.Response.HasStarted)
            {
                await WriteAsync(ctx, k.HttpStatus, k.Code, k.Message, k.Field);
            }
        });
    }

    private static Task WriteAsync(HttpContext ctx, int status, string code, string title, string? field)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/problem+json";
        return ctx.Response.WriteAsJsonAsync(new { type = "about:blank", title, status, code, field, traceId = ctx.TraceIdentifier });
    }

    public static (int Status, string Code, string Title, string? Field) Map(Exception? ex) => ex switch
    {
        KiteException k => (k.HttpStatus, k.Code, k.Message, k.Field),
        BadHttpRequestException b => (400, ErrorCodes.Validation, b.Message, null),
        System.Text.Json.JsonException => (400, ErrorCodes.Validation, "The request body is not valid JSON.", null),
        PostgresException pg => pg.SqlState switch
        {
            "23505" when pg.ConstraintName == "shift_one_open_per_terminal" => (409, ErrorCodes.ShiftAlreadyOpen, "This terminal already has an open shift.", null),
            "23505" when pg.ConstraintName == "variant_sku_unique" => (409, ErrorCodes.Conflict, "That SKU is already in use.", "sku"),
            "23505" when pg.ConstraintName == "barcode_business_id_code_key" => (409, ErrorCodes.Conflict, "That barcode is already on another item.", "barcode"),
            "23505" when pg.ConstraintName == "imei_registry_imei" => (409, ErrorCodes.DeviceIdentifierTaken, "That IMEI already belongs to another device.", "imei"),
            "23505" when pg.ConstraintName == "imei_registry_serial" => (409, ErrorCodes.DeviceIdentifierTaken, "That serial number already belongs to another device of this brand.", "serial"),
            "23505" when pg.ConstraintName is "central_registry_active_imei" or "central_registry_active_serial" =>
                (409, ErrorCodes.DeviceIdentifierTaken, "That IMEI or serial number was just registered for another device at head office.", "imei"),
            "23505" when pg.ConstraintName == "imei_registry_slot" => (409, ErrorCodes.DeviceIdentifierTaken, "That device already has an identifier in that place.", null),
            "23505" when pg.ConstraintName == "blocked_imei_active" => (409, ErrorCodes.Conflict, "That IMEI is already on the blocked list.", "value"),
            "23505" when pg.ConstraintName == "unit_count_one_open" => (409, ErrorCodes.DeviceCountState, "A count of these devices is already open in this branch. Finish or cancel it first.", null),
            "23505" when pg.ConstraintName == "unit_count_scan_count_id_value_key" => (409, ErrorCodes.DeviceCountState, "That device is already scanned in this count.", "code"),
            "23505" when pg.ConstraintName == "refurb_order_one_open" => (409, ErrorCodes.RefurbOrderState, "This device already has an open refurbishment order.", null),
            "23505" => (409, ErrorCodes.Conflict, "That value is already in use.", pg.ConstraintName),
            "40P01" or "40001" => (409, ErrorCodes.Conflict, "The server was busy with the same records. Try again.", null),
            "23503" => (400, ErrorCodes.Validation, "A referenced record does not exist.", pg.ConstraintName),
            "23514" => (400, ErrorCodes.Validation, "The values break a data rule.", pg.ConstraintName),
            "23P01" => (409, ErrorCodes.Conflict, "The dates overlap an existing record.", pg.ConstraintName),
            "42501" => (403, ErrorCodes.Forbidden, "That record is outside your branches.", null),
            "KT001" => (409, "DB-KT001", "History cannot be changed; post a reversal instead.", null),
            "KT002" => (400, "DB-KT002", "The journal does not balance.", null),
            "KT003" => (409, "DB-KT003", "The accounting period is not open.", null),
            "KT004" => (409, "DB-KT004", "Sync events cannot be changed.", null),
            "KT010" => (400, ErrorCodes.SerializedNotYet, "That stock movement breaks a stock rule: serialized items move one device unit at a time, and services are not stocked.", null),
            "KT011" => (409, ErrorCodes.ReturnTooMany, pg.MessageText.Contains("unit") ? "A returned device must be the unit that was sold on that line." : "More would be returned than was sold.", null),
            "KT020" => (409, ErrorCodes.DeviceTransition, Sentence(pg.MessageText), null),
            "KT021" => (409, ErrorCodes.DeviceLedger, "The device and the stock ledger would disagree, so nothing was saved.", null),
            "KT030" => (409, ErrorCodes.TradeInState, Sentence(pg.MessageText), null),
            "KT031" => (409, ErrorCodes.TradeInSettlement, "That is more than is left to settle on the trade-in.", null),
            "KT032" => (409, ErrorCodes.StoreCreditShort, "The customer does not have that much store credit.", null),
            _ => (500, ErrorCodes.Internal, "A database error occurred.", null),
        },
        _ => (500, ErrorCodes.Internal, "Unexpected error. Quote the trace ID to support.", null),
    };

    /// <summary>A database rule message ("KITE: a sold device cannot be sold") as a sentence for the user.</summary>
    private static string Sentence(string message)
    {
        var m = message.StartsWith("KITE: ", StringComparison.Ordinal) ? message[6..] : message;
        return m.Length == 0 ? m : char.ToUpperInvariant(m[0]) + m[1..] + (m.EndsWith('.') ? "" : ".");
    }
}
