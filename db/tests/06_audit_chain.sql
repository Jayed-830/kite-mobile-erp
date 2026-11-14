-- The audit log is hash-chained in the database and tampering is detected (18.6).
SET ROLE kite_app;
SET kite.branch_ids = '{a0000000-0000-0000-0000-00000000000a}';

INSERT INTO audit.audit_log (id, branch_id, action, entity_type, entity_id, after)
VALUES ('40000000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-00000000000a', 'user.login', 'user', NULL, '{"method":"password"}');
INSERT INTO audit.audit_log (id, branch_id, action, entity_type, before, after, reason)
VALUES ('40000000-0000-0000-0000-000000000002', 'a0000000-0000-0000-0000-00000000000b', 'price.change', 'variant',
        '{"price":"10.000"}', '{"price":"9.500"}', 'promotion');   -- branch B row, invisible to this session
INSERT INTO audit.audit_log (id, branch_id, action)
VALUES ('40000000-0000-0000-0000-000000000003', NULL, 'settings.modules.change');

-- Only branch A and business-wide rows are visible...
SELECT kt.assert((SELECT count(*) FROM audit.audit_log) = 2, 'branch A session sees 2 of 3 audit rows');
-- ...but chaining and verification cover every row.
SELECT kt.assert((SELECT ok AND rows_checked = 3 FROM audit.verify_chain()), 'chain verifies across all 3 rows');
RESET ROLE;

SELECT kt.assert((SELECT array_agg(seq ORDER BY seq) = '{1,2,3}' FROM audit.audit_log), 'audit seq is gap-free 1,2,3');
SELECT kt.assert((SELECT bool_and(a.prev_hash = b.hash) FROM audit.audit_log a JOIN audit.audit_log b ON b.seq = a.seq - 1), 'each row links to the previous hash');

-- A rolled-back audit insert does not leave a gap.
BEGIN;
INSERT INTO audit.audit_log (id, action) VALUES ('40000000-0000-0000-0000-0000000000ff', 'test.rolled_back');
ROLLBACK;
INSERT INTO audit.audit_log (id, action) VALUES ('40000000-0000-0000-0000-000000000004', 'test.after_rollback');
SELECT kt.assert((SELECT seq FROM audit.audit_log WHERE id = '40000000-0000-0000-0000-000000000004') = 4, 'seq after rollback is 4');

-- Simulated tampering by a superuser who disables the protection: detected at seq 2.
BEGIN;
ALTER TABLE audit.audit_log DISABLE TRIGGER audit_immutable;
UPDATE audit.audit_log SET reason = 'nothing to see' WHERE seq = 2;
ALTER TABLE audit.audit_log ENABLE TRIGGER audit_immutable;
SELECT kt.assert((SELECT NOT ok AND first_broken_seq = 2 FROM audit.verify_chain()), 'tampering at seq 2 is detected');
ROLLBACK;
SELECT kt.assert((SELECT ok FROM audit.verify_chain()), 'chain is intact again after rollback');
