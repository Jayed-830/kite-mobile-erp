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
            "KT010" => (400, ErrorCodes.SerializedNotYet, "Serialized products (IMEI or serial) cannot move in stock until device units arrive (Phase 5); services are not stocked.", null),
            "KT011" => (409, ErrorCodes.ReturnTooMany, "More would be returned than was sold.", null),
            _ => (500, ErrorCodes.Internal, "A database error occurred.", null),
        },
        _ => (500, ErrorCodes.Internal, "Unexpected error. Quote the trace ID to support.", null),
    };
}
