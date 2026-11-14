-- Phase 3: certificates, registration tokens, licences, backup runs (V0011).
-- Superuser sets up an authority and a certificate; the checks run as kite_app.

INSERT INTO pki.authority (id, business_id, subject, cert_pem, fingerprint_sha256, not_before, not_after, key_file)
VALUES ('5a000000-0000-0000-0000-000000000001', 'b0000000-0000-0000-0000-000000000001', 'CN=KITE Business CA',
        '-----BEGIN CERTIFICATE-----x', repeat('a', 64), now(), now() + interval '20 years', 'ca.key');

INSERT INTO core.server (id, business_id, branch_id, role, name)
VALUES ('c0000000-0000-0000-0000-000000000002', 'b0000000-0000-0000-0000-000000000001',
        'a0000000-0000-0000-0000-00000000000b', 'branch', 'Sohar server');

INSERT INTO pki.certificate (serial, authority_id, kind, server_id, branch_id, subject, cert_pem, fingerprint_sha256, not_before, not_after)
VALUES ('0123456789abcdef', '5a000000-0000-0000-0000-000000000001', 'branch_server',
        'c0000000-0000-0000-0000-000000000002', 'a0000000-0000-0000-0000-00000000000b',
        'CN=KITE branch B02', '-----BEGIN CERTIFICATE-----y', repeat('b', 64), now(), now() + interval '2 years');

-- A second active authority for the same business is refused.
SELECT kt.expect_error($$INSERT INTO pki.authority (id, business_id, subject, cert_pem, fingerprint_sha256, not_before, not_after)
  VALUES ('5a000000-0000-0000-0000-000000000002', 'b0000000-0000-0000-0000-000000000001', 'CN=Other', 'x', repeat('c', 64), now(), now() + interval '1 year')$$, '23505');

SET ROLE kite_app;

-- The app may revoke a certificate (only the revocation columns) ...
SELECT kt.expect_error($$UPDATE pki.certificate SET subject = 'CN=changed' WHERE serial = '0123456789abcdef'$$, '42501');
UPDATE pki.certificate SET revoked_at = now(), revoked_reason = 'server_replaced' WHERE serial = '0123456789abcdef';
-- ... but a revocation is permanent and certificates are never deleted.
SELECT kt.expect_error($$UPDATE pki.certificate SET revoked_at = NULL, revoked_reason = NULL WHERE serial = '0123456789abcdef'$$, 'KT006');
SELECT kt.expect_error($$DELETE FROM pki.certificate WHERE serial = '0123456789abcdef'$$, '42501');
-- A reason is required with a revocation.
RESET ROLE;
SELECT kt.expect_error($$INSERT INTO pki.certificate (serial, authority_id, kind, subject, cert_pem, fingerprint_sha256, not_before, not_after, revoked_at)
  VALUES ('fedcba9876543210', '5a000000-0000-0000-0000-000000000001', 'server_tls', 'CN=x', 'x', repeat('d', 64), now(), now() + interval '1 year', now())$$, '23514');
-- Even the owner role cannot un-revoke (trigger, not only grants).
SELECT kt.expect_error($$UPDATE pki.certificate SET revoked_at = NULL, revoked_reason = NULL WHERE serial = '0123456789abcdef'$$, 'KT006');
SELECT kt.expect_error($$DELETE FROM pki.certificate WHERE serial = '0123456789abcdef'$$, 'KT006');

-- Registration tokens: at most 24 hours, branch-scoped, never deleted.
SELECT kt.expect_error($$INSERT INTO core.registration_token (id, business_id, branch_id, token_hash, expires_at)
  VALUES ('5b000000-0000-0000-0000-000000000001', 'b0000000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-00000000000b', repeat('1', 64), now() + interval '25 hours')$$, '23514');
SET ROLE kite_app;
SET kite.branch_ids = '{a0000000-0000-0000-0000-00000000000a}';
SELECT kt.expect_error($$INSERT INTO core.registration_token (id, business_id, branch_id, token_hash, expires_at)
  VALUES ('5b000000-0000-0000-0000-000000000002', 'b0000000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-00000000000b', repeat('2', 64), now() + interval '24 hours')$$, '42501');
RESET kite.branch_ids;
SET kite.all_branches = 'on';
INSERT INTO core.registration_token (id, business_id, branch_id, token_hash, expires_at)
VALUES ('5b000000-0000-0000-0000-000000000003', 'b0000000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-00000000000b', repeat('3', 64), now() + interval '24 hours');
UPDATE core.registration_token SET used_at = now(), used_by_server_id = 'c0000000-0000-0000-0000-000000000002'
 WHERE id = '5b000000-0000-0000-0000-000000000003';
SELECT kt.expect_error($$UPDATE core.registration_token SET expires_at = now() + interval '1 hour' WHERE id = '5b000000-0000-0000-0000-000000000003'$$, '42501');
SELECT kt.expect_error($$DELETE FROM core.registration_token$$, '42501');
-- A used token cannot also be cancelled.
SELECT kt.expect_error($$UPDATE core.registration_token SET cancelled_at = now() WHERE id = '5b000000-0000-0000-0000-000000000003'$$, '23514');
RESET kite.all_branches;
-- Outside any scope the token is invisible.
SELECT kt.assert((SELECT count(*) FROM core.registration_token) = 0, 'no scope: no registration tokens visible');

-- Licences: one current licence; the signed text never changes; never deleted.
INSERT INTO lic.licence (id, installation_id, kind, customer, valid_until, grace_days, branches_allowed, terminals_allowed, modules, issued_at, file_text, source, is_current)
VALUES ('kme-test-1', '1A2B-3C4D-5E6F-7A8B', 'subscription', 'Test Mobiles LLC', '2027-12-31', 30, 2, 5, '{core,sales}', now(), '{}', 'import', true);
SELECT kt.expect_error($$INSERT INTO lic.licence (id, installation_id, kind, customer, valid_until, grace_days, branches_allowed, terminals_allowed, modules, issued_at, file_text, source, is_current)
  VALUES ('kme-test-2', '1A2B-3C4D-5E6F-7A8B', 'trial', 'Test', '2027-01-31', 30, 1, 1, '{core}', now(), '{}', 'import', true)$$, '23505');
SELECT kt.expect_error($$UPDATE lic.licence SET valid_until = '2099-12-31' WHERE id = 'kme-test-1'$$, '42501');
SELECT kt.expect_error($$DELETE FROM lic.licence$$, '42501');
UPDATE lic.licence SET is_current = false WHERE id = 'kme-test-1';

-- Backup runs: history is kept; a finished run needs its finish time.
INSERT INTO ops.backup_run (id, kind, status, trigger, started_at)
VALUES ('5c000000-0000-0000-0000-000000000001', 'logical', 'running', 'manual', now());
SELECT kt.expect_error($$UPDATE ops.backup_run SET status = 'succeeded' WHERE id = '5c000000-0000-0000-0000-000000000001'$$, '23514');
UPDATE ops.backup_run SET status = 'succeeded', finished_at = now(), size_bytes = 1024, sha256 = repeat('e', 64)
 WHERE id = '5c000000-0000-0000-0000-000000000001';
SELECT kt.expect_error($$UPDATE ops.backup_run SET started_at = now() - interval '1 day'$$, '42501');
SELECT kt.expect_error($$DELETE FROM ops.backup_run$$, '42501');

-- The terminal count for the licence sees every branch, whatever the session scope.
SET kite.branch_ids = '{a0000000-0000-0000-0000-00000000000a}';
SELECT kt.assert((SELECT count(*) FROM core.terminal) <= 1, 'branch scope sees at most its own terminal');
SELECT kt.assert(core.active_terminal_count() = (SELECT count(*) FROM core.terminal WHERE status = 'active') OR true, 'count callable');
RESET kite.branch_ids;
RESET ROLE;
UPDATE core.terminal SET status = 'active', approved_at = now();
SET ROLE kite_app;
SET kite.branch_ids = '{a0000000-0000-0000-0000-00000000000a}';
SELECT kt.assert(core.active_terminal_count() = 2, 'active terminal count covers all branches (2), not only the session scope');
RESET kite.branch_ids;
RESET ROLE;
UPDATE core.terminal SET status = 'pending', approved_at = NULL;
SET ROLE kite_app;

-- Installation IDs have the Nextdab Machine ID shape.
SELECT kt.expect_error($$UPDATE core.server SET installation_id = 'not-an-id' WHERE is_self$$, '23514');
UPDATE core.server SET installation_id = '1A2B-3C4D-5E6F-7A8B' WHERE is_self;
RESET ROLE;

-- The new permissions exist (owner roles get them in the migration; no owner role in this fixture).
SELECT kt.assert((SELECT count(*) FROM iam.permission WHERE code IN ('SERVERS.MANAGE','LICENCE.MANAGE','BACKUP.VIEW','BACKUP.MANAGE')) = 4,
                 'Phase 3 permissions are seeded');
-- The backup role can read every row despite row-level security.
SELECT kt.assert((SELECT rolbypassrls AND rolreplication AND rolcreatedb FROM pg_roles WHERE rolname = 'kite_backup'),
                 'kite_backup has BYPASSRLS, REPLICATION and CREATEDB');
