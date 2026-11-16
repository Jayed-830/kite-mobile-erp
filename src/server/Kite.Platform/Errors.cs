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

    // Phase 3: servers and certificates
    public const string NotHeadOffice = "SRV-001";
    public const string RegistrationTokenInvalid = "SRV-002";
    public const string BranchHasServer = "SRV-003";
    public const string ServerVersionMismatch = "SRV-004";
    public const string CertificateRejected = "SRV-005";
    public const string HeadOfficeUnreachable = "SRV-006";
    public const string FingerprintMismatch = "SRV-007";
    public const string AuthorityNotReady = "SRV-008";
    public const string ServerRevoked = "SRV-009";

    // Phase 3: licence (blueprint 24.5)
    public const string NoLicence = "LIC-001";
    public const string LicenceExpired = "LIC-002";
    public const string Suspended = "LIC-003";
    public const string LicenceLimit = "LIC-004";
    public const string ModuleNotLicensed = "LIC-005";
    public const string LicenceFileInvalid = "LIC-010";
    public const string LicenceSignatureInvalid = "LIC-011";
    public const string LicenceOtherProduct = "LIC-012";
    public const string LicenceOtherInstallation = "LIC-013";
    public const string LicenceKeyMissing = "LIC-014";
    public const string LicenceOlder = "LIC-015";

    // Phase 3: backups (blueprint 19)
    public const string BackupNotConfigured = "BAK-001";
    public const string BackupBusy = "BAK-002";
    public const string BackupFailed = "BAK-003";

    // Phase 4: catalog, inventory and POS (blueprint 10, 16.3)
    public const string NoPrice = "CAT-001";
    public const string BarcodeUnknown = "CAT-002";
    public const string SerializedNotYet = "CAT-003";
    public const string ItemInactive = "CAT-004";
    public const string InsufficientStock = "INV-001";
    public const string CountClosed = "INV-002";
    public const string NoOpenShift = "POS-001";
    public const string ShiftAlreadyOpen = "POS-002";
    public const string ShiftNotYours = "POS-003";
    public const string NotATerminal = "POS-004";
    public const string PaymentMismatch = "POS-005";
    public const string BelowMinimumPrice = "POS-006";
    public const string DiscountAboveLimit = "POS-007";
    public const string ApprovalInvalid = "POS-008";
    public const string ReturnWindow = "POS-009";
    public const string ReturnTooMany = "POS-010";
    public const string RefundMethod = "POS-011";
    public const string CashRefundCap = "POS-012";
    public const string VoidNotAllowed = "POS-013";
    public const string VarianceNeedsAcceptance = "POS-014";
    public const string SaleChanged = "POS-015";
    public const string CustomerRequired = "POS-016";
    public const string TerminalKeyInvalid = "POS-017";
    public const string EmergencyRecordInvalid = "POS-018";
    public const string EmergencyNotEnabled = "POS-019";
    public const string PrinterUnreachable = "PRN-001";

    // Phase 5: device units, inspections and refurbishment (blueprint 11, 12)
    public const string ImeiInvalid = "DEV-001";
    public const string DeviceUnitRequired = "DEV-002";
    public const string DeviceNotFound = "DEV-003";
    public const string DeviceNotAvailable = "DEV-004";
    public const string DeviceReserved = "DEV-005";
    public const string DeviceTransition = "DEV-006";
    public const string DeviceIdentifierTaken = "DEV-007";
    public const string DeviceDamaged = "DEV-008";
    public const string DeviceMismatch = "DEV-009";
    public const string RefurbOrderState = "DEV-010";
    public const string InspectionIncomplete = "DEV-011";
    public const string DeviceUnitCount = "DEV-012";
    public const string DeviceLedger = "DEV-013";
    public const string ImeiBlocked = "DEV-014";
    public const string CentralCheckPending = "DEV-015";
    public const string DeviceWrongModel = "DEV-016";
    public const string SerialFormat = "DEV-017";
    public const string DeviceOnHold = "DEV-018";
    public const string DeviceCountState = "DEV-019";
}
