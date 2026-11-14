-- Outbox: gap-free origin_seq, payload hash, immutable events, no deletion of unsent events (7.2).
SET ROLE kite_app;

INSERT INTO sync.outbox (event_id, event_type, schema_version, aggregate_type, aggregate_id, aggregate_version,
                         business_id, origin_server_id, occurred_at, payload)
VALUES ('50000000-0000-0000-0000-000000000001', 'core.business_created', 1, 'business', 'b0000000-0000-0000-0000-000000000001', 1,
        'b0000000-0000-0000-0000-000000000001', 'c0000000-0000-0000-0000-000000000001', now(), '{"name":"Test"}');

BEGIN;
INSERT INTO sync.outbox (event_id, event_type, schema_version, aggregate_type, aggregate_id, aggregate_version,
                         business_id, origin_server_id, occurred_at, payload)
VALUES ('50000000-0000-0000-0000-0000000000ff', 'core.rolled_back', 1, 'x', 'b0000000-0000-0000-0000-000000000001', 1,
        'b0000000-0000-0000-0000-000000000001', 'c0000000-0000-0000-0000-000000000001', now(), '{}');
ROLLBACK;

INSERT INTO sync.outbox (event_id, event_type, schema_version, aggregate_type, aggregate_id, aggregate_version,
                         business_id, origin_server_id, occurred_at, payload, origin_seq, status)
VALUES ('50000000-0000-0000-0000-000000000002', 'iam.user_created', 1, 'user', 'b0000000-0000-0000-0000-000000000009', 1,
        'b0000000-0000-0000-0000-000000000001', 'c0000000-0000-0000-0000-000000000001', now(), '{"username":"owner"}',
        999, 'acked');  -- caller-supplied seq and status are ignored by the trigger

SELECT kt.assert((SELECT array_agg(origin_seq ORDER BY origin_seq) = '{1,2}' FROM sync.outbox), 'origin_seq is gap-free 1,2 despite a rollback');
SELECT kt.assert((SELECT status = 'pending' FROM sync.outbox WHERE origin_seq = 2), 'new events always start pending');
SELECT kt.assert((SELECT payload_sha256 = sha256(convert_to(payload::text, 'UTF8')) FROM sync.outbox WHERE origin_seq = 1), 'payload hash computed');

-- Delivery bookkeeping is allowed; acknowledging works.
UPDATE sync.outbox SET attempts = attempts + 1, last_attempt_at = now() WHERE origin_seq = 1;
UPDATE sync.outbox SET status = 'acked', acked_at = now() WHERE origin_seq = 1;
SELECT kt.expect_error($$UPDATE sync.outbox SET status = 'pending', acked_at = NULL WHERE origin_seq = 1$$, 'KT004');
RESET ROLE;

-- Even the owner cannot change an event or delete one that is not archived.
SET ROLE kite_migrator;
SELECT kt.expect_error($$UPDATE sync.outbox SET payload = '{"tampered":true}' WHERE origin_seq = 2$$, 'KT004');
SELECT kt.expect_error($$DELETE FROM sync.outbox WHERE origin_seq = 2$$, 'KT001');
SELECT kt.expect_error($$DELETE FROM sync.outbox WHERE origin_seq = 1$$, 'KT001');
RESET ROLE;
