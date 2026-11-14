namespace Kite.Platform;

/// <summary>
/// Business error with a stable code for support and the UI (blueprint 21.4, 22.2).
/// The API turns it into RFC 9457 Problem Details.
/// </summary>
public class KiteException : Exception
{
    public KiteException(string code, string message, int httpStatus = 400, string? field = null)
        : base(message)
    {
        Code = code;
        HttpStatus = httpStatus;
        Field = field;
    }

    public KiteException() : this(ErrorCodes.Internal, "Unexpected error.", 500) { }
    public KiteException(string message) : this(ErrorCodes.Validation, message) { }
    public KiteException(string message, Exception inner) : base(message, inner) { Code = ErrorCodes.Internal; HttpStatus = 500; }

    public string Code { get; } = ErrorCodes.Internal;
    public int HttpStatus { get; } = 500;
    public string? Field { get; }
}

public static class ErrorCodes
{
    public const string Validation = "VAL-001";
    public const string NotFound = "GEN-404";
    public const string Conflict = "GEN-409";
    public const string VersionMismatch = "GEN-412";
    public const string Internal = "GEN-500";

    public const string SetupAlreadyDone = "SET-001";
    public const string SetupCodeInvalid = "SET-002";
    public const string SetupRequired = "SET-003";

    public const string Unauthenticated = "AUTH-401";
    public const string Forbidden = "AUTH-403";
    public const string BadCredentials = "AUTH-001";
    public const string AccountLocked = "AUTH-002";
    public const string AccountDisabled = "AUTH-003";
    public const string MfaRequired = "AUTH-004";
    public const string MfaInvalid = "AUTH-005";
    public const string PasswordPolicy = "AUTH-006";
    public const string TerminalNotActive = "AUTH-007";
    public const string PinNotSet = "AUTH-008";
    public const string PasswordChangeRequired = "AUTH-009";

    public const string ModuleDisabled = "MOD-403";
    public const string ModuleDependency = "MOD-001";
    public const string ModuleAlwaysOn = "MOD-002";

    public const string SystemRoleLocked = "IAM-001";
    public const string LastOwner = "IAM-002";
}
