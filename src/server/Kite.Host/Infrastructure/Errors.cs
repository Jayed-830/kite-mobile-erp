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
    public static IApplicationBuilder UseKiteErrors(this IApplicationBuilder app) =>
        app.UseExceptionHandler(handler => handler.Run(async ctx =>
        {
            var ex = ctx.Features.Get<IExceptionHandlerFeature>()?.Error;
            var (status, code, title, field) = Map(ex);
            if (status >= 500)
                ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Kite.Errors").LogError(ex, "Unhandled error");
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = "application/problem+json";
            await ctx.Response.WriteAsJsonAsync(new
            {
                type = "about:blank",
                title,
                status,
                code,
                field,
                traceId = ctx.TraceIdentifier,
            });
        }));

    public static (int Status, string Code, string Title, string? Field) Map(Exception? ex) => ex switch
    {
        KiteException k => (k.HttpStatus, k.Code, k.Message, k.Field),
        BadHttpRequestException b => (400, ErrorCodes.Validation, b.Message, null),
        System.Text.Json.JsonException => (400, ErrorCodes.Validation, "The request body is not valid JSON.", null),
        PostgresException pg => pg.SqlState switch
        {
            "23505" => (409, ErrorCodes.Conflict, "That value is already in use.", pg.ConstraintName),
            "23503" => (400, ErrorCodes.Validation, "A referenced record does not exist.", pg.ConstraintName),
            "23514" => (400, ErrorCodes.Validation, "The values break a data rule.", pg.ConstraintName),
            "23P01" => (409, ErrorCodes.Conflict, "The dates overlap an existing record.", pg.ConstraintName),
            "42501" => (403, ErrorCodes.Forbidden, "That record is outside your branches.", null),
            "KT001" => (409, "DB-KT001", "History cannot be changed; post a reversal instead.", null),
            "KT002" => (400, "DB-KT002", "The journal does not balance.", null),
            "KT003" => (409, "DB-KT003", "The accounting period is not open.", null),
            "KT004" => (409, "DB-KT004", "Sync events cannot be changed.", null),
            _ => (500, ErrorCodes.Internal, "A database error occurred.", null),
        },
        _ => (500, ErrorCodes.Internal, "Unexpected error. Quote the trace ID to support.", null),
    };
}
