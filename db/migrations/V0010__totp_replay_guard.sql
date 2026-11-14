-- KITE MOBILE ERP - V0010: a two-step code is accepted only once (RFC 6238 section 5.2).
-- The server stores the time step of the last accepted code and refuses that step or any earlier one.
ALTER TABLE iam."user" ADD COLUMN totp_last_step bigint;

COMMENT ON COLUMN iam."user".totp_last_step IS
  'Time step (Unix seconds / 30) of the last accepted TOTP code; codes for this step or earlier are refused.';
