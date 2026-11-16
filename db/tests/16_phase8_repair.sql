-- Phase 8 (V0017): every allowed repair transition passes and every other one is refused; each status change writes
-- its history; work goes ahead only on an approved estimate; estimates are versioned, approved within what the customer
-- authorised, and fixed once sent; parts move at cost into work-in-progress and a job is delivered only with nothing
-- left in it; deposits never go below zero for their job; photos keep their SHA-256; a delivered job is fixed;
-- WhatsApp consent needs a phone; and what the application role may do.
\set ON_ERROR_STOP 1
SET client_min_messages = warning;
SET kite.all_branches = 'on';

-- ---------------------------------------------------------------- fixture
INSERT INTO crm.customer (id, business_id, number, name_en, phone_e164) VALUES
  ('ac800000-0000-0000-0000-000000000001', 'b0000000-0000-0000-0000-000000000001', 'C-008001', 'Salim Al Hinai', '+96892345678');
INSERT INTO catalog.product (id, business_id, name_en, name_ar, product_type, tracking, tax_code_id) VALUES
  ('a2800000-0000-0000-0000-000000000001', 'b0000000-0000-0000-0000-000000000001', 'iPhone 13 screen', 'شاشة آيفون 13', 'spare_part', 'none', '70000000-0000-0000-0000-000000000001'),
  ('a2800000-0000-0000-0000-000000000002', 'b0000000-0000-0000-0000-000000000001', 'Repair labour', 'أجور الصيانة', 'service', 'none', '70000000-0000-0000-0000-000000000001');
INSERT INTO catalog.variant (id, business_id, product_id, sku) VALUES
  ('a3800000-0000-0000-0000-000000000001', 'b0000000-0000-0000-0000-000000000001', 'a2800000-0000-0000-0000-000000000001', 'SP-IP13-SCR'),
  ('a3800000-0000-0000-0000-000000000002', 'b0000000-0000-0000-0000-000000000001', 'a2800000-0000-0000-0000-000000000002', 'REPAIR-LABOUR');
SELECT kt.move(10, 12.000, 'opening', 'a3800000-0000-0000-0000-000000000001');
INSERT INTO repair.settings (business_id, terms_en, terms_ar) VALUES ('b0000000-0000-0000-0000-000000000001', 'Terms', 'الشروط');
INSERT INTO repair.category (id, business_id, code, name_en, name_ar) VALUES
  ('ab800000-0000-0000-0000-000000000001', 'b0000000-0000-0000-0000-000000000001', 'SCREEN', 'Screen', 'الشاشة');

-- A customer device has an IMEI or a serial.
SELECT kt.expect_error($$INSERT INTO device.customer_device (id, business_id, customer_id, model)
  VALUES (gen_random_uuid(), 'b0000000-0000-0000-0000-000000000001', 'ac800000-0000-0000-0000-000000000001', 'iPhone 13')$$, '23514');
INSERT INTO device.customer_device (id, business_id, customer_id, imei1, brand, model) VALUES
  ('ad800000-0000-0000-0000-000000000001', 'b0000000-0000-0000-0000-000000000001', 'ac800000-0000-0000-0000-000000000001', '353918050360011', 'Apple', 'iPhone 13');
SELECT kt.expect_error($$INSERT INTO device.customer_device (id, business_id, customer_id, imei1, model)
  VALUES (gen_random_uuid(), 'b0000000-0000-0000-0000-000000000001', 'ac800000-0000-0000-0000-000000000001', '353918050360011', 'again')$$, '23505');

-- A new job; ids come from a counter so every job has its own number.
CREATE SEQUENCE kt.rjob_no;
CREATE FUNCTION kt.rjob(p_branch uuid DEFAULT 'a0000000-0000-0000-0000-00000000000a', p_warranty_of uuid DEFAULT NULL) RETURNS uuid
LANGUAGE plpgsql AS $$
DECLARE id uuid := gen_random_uuid();
BEGIN
  INSERT INTO repair.repair_job (id, business_id, branch_id, number, customer_id, customer_device_id, category_id, problem, passcode_mode,
                                 pre_authorised, warranty_days, promised_at, terms_name, terms_accepted_at, created_by, warranty_of_id)
  VALUES (id, 'b0000000-0000-0000-0000-000000000001', p_branch, 'R-T-' || nextval('kt.rjob_no'), 'ac800000-0000-0000-0000-000000000001',
          'ad800000-0000-0000-0000-000000000001', 'ab800000-0000-0000-0000-000000000001', 'Cracked screen', 'none', 5.000, 90, now() + interval '2 days',
          'Salim Al Hinai', now(), 'a5000000-0000-0000-0000-000000000001', p_warranty_of);
  RETURN id;
END $$;

-- An estimate with one line (gross p_total), left as a draft.
CREATE FUNCTION kt.restimate(p_job uuid, p_total numeric) RETURNS uuid
LANGUAGE plpgsql AS $$
DECLARE e uuid := gen_random_uuid(); n integer; tax numeric := round(p_total * 5 / 105, 3);
BEGIN
  SELECT coalesce(max(estimate_no), 0) + 1 INTO n FROM repair.estimate WHERE job_id = p_job;
  INSERT INTO repair.estimate (id, business_id, branch_id, job_id, estimate_no, created_by)
  SELECT e, business_id, branch_id, id, n, 'a5000000-0000-0000-0000-000000000001' FROM repair.repair_job WHERE id = p_job;
  INSERT INTO repair.estimate_line (id, estimate_id, line_no, kind, variant_id, description_en, description_ar, qty, unit_price, tax_code_id, tax_rate,
                                    net_amount, tax_amount, gross_amount)
  VALUES (gen_random_uuid(), e, 1, 'labour', 'a3800000-0000-0000-0000-000000000002', 'Screen fitting', 'تركيب شاشة', 1, p_total,
          '70000000-0000-0000-0000-000000000001', 5, p_total - tax, tax, p_total);
  UPDATE repair.estimate SET net_total = p_total - tax, tax_total = tax, total = p_total WHERE id = e;
  RETURN e;
END $$;

CREATE FUNCTION kt.rapprove(p_estimate uuid, p_method text DEFAULT 'pre_authorised') RETURNS void
LANGUAGE sql AS $$
  UPDATE repair.estimate SET status = 'approved', approval_method = p_method, approved_at = now(), approved_by = 'a5000000-0000-0000-0000-000000000001',
         approver_name = CASE WHEN p_method IN ('signature','phone') THEN 'Salim Al Hinai' END,
         approval_ref = CASE WHEN p_method = 'phone' THEN '+96892345678' WHEN p_method = 'whatsapp' THEN 'wamid.TEST' END
   WHERE id = p_estimate
$$;

-- Moves a job to a status, with what that status needs (its outcome; the collection when delivered).
CREATE FUNCTION kt.rmove(p_job uuid, p_to text) RETURNS void
LANGUAGE sql AS $$
  UPDATE repair.repair_job
     SET status = p_to,
         outcome = CASE WHEN p_to IN ('completed','ready','delivered','cancelled','unrepairable')
                        THEN coalesce(outcome, CASE p_to WHEN 'cancelled' THEN 'cancelled' WHEN 'unrepairable' THEN 'unrepairable' ELSE 'repaired' END) END,
         delivered_at = CASE WHEN p_to = 'delivered' THEN now() END,
         collected_by = CASE WHEN p_to = 'delivered' THEN 'Salim Al Hinai' END,
         collection_check = CASE WHEN p_to = 'delivered' THEN 'qr' END,
         updated_by = 'a5000000-0000-0000-0000-000000000001'
   WHERE id = p_job
$$;

-- A job driven along allowed transitions to a status (with an approved estimate within the pre-authorised amount).
CREATE FUNCTION kt.rjob_at(p_status text) RETURNS uuid
LANGUAGE plpgsql AS $$
DECLARE j uuid := kt.rjob(); path text[];
BEGIN
  PERFORM kt.rapprove(kt.restimate(j, 3.000));
  path := CASE p_status
    WHEN 'received' THEN '{}'
    WHEN 'inspection_pending' THEN '{inspection_pending}'
    WHEN 'under_diagnosis' THEN '{inspection_pending,under_diagnosis}'
    WHEN 'waiting_approval' THEN '{inspection_pending,under_diagnosis,waiting_approval}'
    WHEN 'waiting_parts' THEN '{inspection_pending,under_diagnosis,waiting_approval,waiting_parts}'
    WHEN 'in_progress' THEN '{inspection_pending,under_diagnosis,in_progress}'
    WHEN 'testing' THEN '{inspection_pending,under_diagnosis,in_progress,testing}'
    WHEN 'completed' THEN '{inspection_pending,under_diagnosis,in_progress,testing,completed}'
    WHEN 'ready' THEN '{inspection_pending,under_diagnosis,in_progress,testing,completed,ready}'
    WHEN 'delivered' THEN '{inspection_pending,under_diagnosis,in_progress,testing,completed,ready,delivered}'
    WHEN 'cancelled' THEN '{cancelled}'
    WHEN 'unrepairable' THEN '{inspection_pending,under_diagnosis,unrepairable}'
  END::text[];
  FOR i IN 1 .. coalesce(array_length(path, 1), 0) LOOP
    PERFORM kt.rmove(j, path[i]);
  END LOOP;
  RETURN j;
END $$;

-- ---------------------------------------------------------------- every transition (13.2): 22 allowed, 110 refused
DO $$
DECLARE
  statuses text[] := ARRAY['received','inspection_pending','under_diagnosis','waiting_approval','waiting_parts','in_progress','testing',
                           'completed','ready','delivered','cancelled','unrepairable'];
  f text; t text; j uuid; allowed boolean; ok int := 0; refused int := 0; st text;
BEGIN
  FOREACH f IN ARRAY statuses LOOP
    FOREACH t IN ARRAY statuses LOOP
      CONTINUE WHEN f = t;
      j := kt.rjob_at(f);
      PERFORM kt.assert((SELECT status = f FROM repair.repair_job WHERE id = j), 'the job reached ' || f);
      allowed := EXISTS (SELECT 1 FROM repair.transition WHERE from_status = f AND to_status = t);
      BEGIN
        PERFORM kt.rmove(j, t);
        IF NOT allowed THEN RAISE EXCEPTION 'ASSERTION FAILED: % -> % was allowed', f, t USING ERRCODE = 'KTF01'; END IF;
        PERFORM kt.assert((SELECT status = t FROM repair.repair_job WHERE id = j), f || ' -> ' || t || ' moved the job');
        ok := ok + 1;
      EXCEPTION WHEN SQLSTATE 'KT040' THEN
        IF allowed THEN RAISE EXCEPTION 'ASSERTION FAILED: % -> % was refused: %', f, t, SQLERRM USING ERRCODE = 'KTF01'; END IF;
        refused := refused + 1;
      END;
    END LOOP;
  END LOOP;
  PERFORM kt.assert(ok = 22, format('22 allowed transitions passed (%s)', ok));
  PERFORM kt.assert(refused = 110, format('110 other transitions were refused (%s)', refused));
END $$;

-- A job starts as received, and its history has a row for every status it took, with the note of the transaction.
SELECT kt.expect_error($$INSERT INTO repair.repair_job (id, business_id, branch_id, number, status, customer_id, customer_device_id, category_id, problem,
                          passcode_mode, warranty_days, promised_at, terms_name, terms_accepted_at, created_by)
  VALUES (gen_random_uuid(), 'b0000000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-00000000000a', 'R-X-1', 'in_progress',
          'ac800000-0000-0000-0000-000000000001', 'ad800000-0000-0000-0000-000000000001', 'ab800000-0000-0000-0000-000000000001', 'x', 'none', 90, now(), 'x', now(),
          'a5000000-0000-0000-0000-000000000001')$$, 'KT040');
BEGIN;
CREATE TEMP TABLE delivered_job ON COMMIT DROP AS SELECT kt.rjob_at('delivered') AS id;
SELECT kt.assert((SELECT count(*) = 8 FROM repair.status_history WHERE job_id = (SELECT id FROM delivered_job)), 'received plus 7 changes: 8 history rows');
SELECT kt.assert((SELECT array_agg(to_status ORDER BY id) = '{received,inspection_pending,under_diagnosis,in_progress,testing,completed,ready,delivered}'
                    FROM repair.status_history WHERE job_id = (SELECT id FROM delivered_job)), 'in order');
COMMIT;
DO $$
DECLARE j uuid := kt.rjob_at('received');
BEGIN
  PERFORM set_config('kite.repair_note', 'Customer asked to check the camera too', true);
  PERFORM kt.rmove(j, 'inspection_pending');
  PERFORM kt.assert((SELECT note = 'Customer asked to check the camera too' AND user_id = 'a5000000-0000-0000-0000-000000000001'
                       FROM repair.status_history WHERE job_id = j AND to_status = 'inspection_pending'), 'the note and the user are kept');
END $$;
SELECT kt.expect_error($$UPDATE repair.status_history SET note = 'x'$$, 'KT001');
SELECT kt.expect_error($$DELETE FROM repair.repair_job$$, 'KT040');

-- ---------------------------------------------------------------- estimates (13.3)
DO $$
DECLARE j uuid := kt.rjob_at('under_diagnosis'); e1 uuid; e2 uuid; e3 uuid; e4 uuid;
BEGIN
  e1 := (SELECT id FROM repair.estimate WHERE job_id = j);                                       -- version 1, 3.000, approved
  -- Versions follow each other; a version cannot be skipped.
  BEGIN
    INSERT INTO repair.estimate (id, business_id, branch_id, job_id, estimate_no, created_by)
    VALUES (gen_random_uuid(), 'b0000000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-00000000000a', j, 5, 'a5000000-0000-0000-0000-000000000001');
    RAISE EXCEPTION 'ASSERTION FAILED: version 5 after 1' USING ERRCODE = 'KTF01';
  EXCEPTION WHEN SQLSTATE 'KT041' THEN NULL; END;
  -- An approved version is fixed.
  BEGIN UPDATE repair.estimate SET note = 'x' WHERE id = e1; RAISE EXCEPTION 'ASSERTION FAILED: approved changed' USING ERRCODE = 'KTF01';
  EXCEPTION WHEN SQLSTATE 'KT041' THEN NULL; END;
  -- Version 2 at 20.000: above the 5.000 pre-authorised, so staff cannot approve it on their own...
  e2 := kt.restimate(j, 20.000);
  BEGIN PERFORM kt.rapprove(e2, 'pre_authorised'); RAISE EXCEPTION 'ASSERTION FAILED: 20 > 5 approved by staff' USING ERRCODE = 'KTF01';
  EXCEPTION WHEN SQLSTATE 'KT041' THEN NULL; END;
  -- ...and the work cannot go ahead while the current version is not approved.
  BEGIN PERFORM kt.rmove(j, 'in_progress'); RAISE EXCEPTION 'ASSERTION FAILED: work without approval' USING ERRCODE = 'KTF01';
  EXCEPTION WHEN SQLSTATE 'KT041' THEN NULL; END;
  PERFORM kt.rmove(j, 'waiting_approval');
  -- Sent: its lines and total are fixed; a changed estimate is a new version.
  UPDATE repair.estimate SET status = 'sent', sent_at = now() WHERE id = e2;
  BEGIN UPDATE repair.estimate_line SET unit_price = 1 WHERE estimate_id = e2; RAISE EXCEPTION 'ASSERTION FAILED: sent line changed' USING ERRCODE = 'KTF01';
  EXCEPTION WHEN SQLSTATE 'KT041' THEN NULL; END;
  BEGIN UPDATE repair.estimate SET total = 1, net_total = 1, tax_total = 0 WHERE id = e2; RAISE EXCEPTION 'ASSERTION FAILED: sent total changed' USING ERRCODE = 'KTF01';
  EXCEPTION WHEN SQLSTATE 'KT041' THEN NULL; END;
  -- The customer approves it by phone (who and the number are required).
  BEGIN UPDATE repair.estimate SET status = 'approved', approval_method = 'phone', approved_at = now(), approver_name = 'Salim' WHERE id = e2;
        RAISE EXCEPTION 'ASSERTION FAILED: phone approval without the number' USING ERRCODE = 'KTF01';
  EXCEPTION WHEN SQLSTATE '23514' THEN NULL; END;
  PERFORM kt.rapprove(e2, 'phone');
  PERFORM kt.rmove(j, 'in_progress');
  -- Extra work found: version 3 at 25.000 supersedes nothing approved, needs the customer again; a draft cannot be tested.
  e3 := kt.restimate(j, 25.000);
  BEGIN PERFORM kt.rmove(j, 'testing'); RAISE EXCEPTION 'ASSERTION FAILED: tested on a draft estimate' USING ERRCODE = 'KTF01';
  EXCEPTION WHEN SQLSTATE 'KT041' THEN NULL; END;
  PERFORM kt.rmove(j, 'waiting_approval');
  -- The customer said no to the extra work but yes to the original: version 4 at 20.000 is within what they approved.
  e4 := kt.restimate(j, 20.000);
  PERFORM kt.assert((SELECT status = 'superseded' FROM repair.estimate WHERE id = e3), 'version 3 superseded by version 4');
  PERFORM kt.assert((SELECT status = 'approved' FROM repair.estimate WHERE id = e2), 'an approved version stays approved');
  BEGIN PERFORM kt.rapprove(e3, 'whatsapp'); RAISE EXCEPTION 'ASSERTION FAILED: an old version approved' USING ERRCODE = 'KTF01';
  EXCEPTION WHEN SQLSTATE 'KT041' THEN NULL; END;
  PERFORM kt.rapprove(e4, 'pre_authorised');
  PERFORM kt.assert(repair.authorised_ceiling(j) = 20.000, 'the customer authorised 20.000');
  PERFORM kt.rmove(j, 'in_progress');
  PERFORM kt.rmove(j, 'testing');
  -- An estimate is never deleted; a draft needs a line before it is sent; only a warranty claim has a zero warranty estimate.
  BEGIN DELETE FROM repair.estimate WHERE id = e1; RAISE EXCEPTION 'ASSERTION FAILED: estimate deleted' USING ERRCODE = 'KTF01';
  EXCEPTION WHEN SQLSTATE 'KT041' THEN NULL; END;
END $$;

DO $$
DECLARE j uuid := kt.rjob(); w uuid; e uuid := gen_random_uuid(); e2 uuid := gen_random_uuid();
BEGIN
  INSERT INTO repair.estimate (id, business_id, branch_id, job_id, estimate_no, created_by)
  VALUES (e, 'b0000000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-00000000000a', j, 1, 'a5000000-0000-0000-0000-000000000001');
  BEGIN UPDATE repair.estimate SET status = 'sent', sent_at = now() WHERE id = e; RAISE EXCEPTION 'ASSERTION FAILED: an empty estimate sent' USING ERRCODE = 'KTF01';
  EXCEPTION WHEN SQLSTATE 'KT041' THEN NULL; END;
  BEGIN PERFORM kt.rapprove(e, 'warranty'); RAISE EXCEPTION 'ASSERTION FAILED: warranty approval on a normal job' USING ERRCODE = 'KTF01';
  EXCEPTION WHEN SQLSTATE 'KT041' THEN NULL; END;
  -- A warranty claim (13.6): a new job linked to the delivered one, with a zero-charge estimate.
  w := kt.rjob(p_warranty_of => (SELECT id FROM repair.repair_job WHERE status = 'delivered' LIMIT 1));
  INSERT INTO repair.estimate (id, business_id, branch_id, job_id, estimate_no, created_by)
  VALUES (e2, 'b0000000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-00000000000a', w, 1, 'a5000000-0000-0000-0000-000000000001');
  PERFORM kt.rapprove(e2, 'warranty');
  PERFORM kt.rmove(w, 'inspection_pending'); PERFORM kt.rmove(w, 'under_diagnosis'); PERFORM kt.rmove(w, 'in_progress');
  PERFORM kt.assert((SELECT status = 'in_progress' FROM repair.repair_job WHERE id = w), 'the warranty repair goes ahead at no charge');
END $$;

-- ---------------------------------------------------------------- parts into work-in-progress (13.4)
CREATE FUNCTION kt.rfit(p_job uuid, p_qty numeric, p_return_of uuid DEFAULT NULL) RETURNS uuid
LANGUAGE plpgsql AS $$
DECLARE m inv.stock_movement; l uuid := gen_random_uuid(); fitted_cost numeric;
BEGIN
  IF p_qty > 0 THEN
    INSERT INTO inv.stock_movement (id, business_id, branch_id, warehouse_id, variant_id, movement_type, qty, source_doc_type, source_doc_id, source_line_id)
    VALUES (gen_random_uuid(), 'b0000000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-00000000000a', 'a1000000-0000-0000-0000-00000000000a',
            'a3800000-0000-0000-0000-000000000001', 'repair_issue', -p_qty, 'repair_job', p_job, l) RETURNING * INTO m;
  ELSE
    SELECT cost / qty * -p_qty INTO fitted_cost FROM repair.repair_line WHERE id = p_return_of;
    INSERT INTO inv.stock_movement (id, business_id, branch_id, warehouse_id, variant_id, movement_type, qty, value, source_doc_type, source_doc_id, source_line_id)
    VALUES (gen_random_uuid(), 'b0000000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-00000000000a', 'a1000000-0000-0000-0000-00000000000a',
            'a3800000-0000-0000-0000-000000000001', 'repair_return', -p_qty, fitted_cost, 'repair_job', p_job, l) RETURNING * INTO m;
  END IF;
  INSERT INTO repair.repair_line (id, business_id, branch_id, job_id, kind, variant_id, product_type, qty, cost, stock_movement_id, return_of_id, created_by)
  VALUES (l, 'b0000000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-00000000000a', p_job, 'part', 'a3800000-0000-0000-0000-000000000001', 'spare_part',
          p_qty, -m.value, m.id, p_return_of, 'a5000000-0000-0000-0000-000000000001');
  RETURN l;
END $$;

DO $$
DECLARE j uuid := kt.rjob_at('in_progress'); early uuid := kt.rjob_at('under_diagnosis'); f1 uuid; f2 uuid;
BEGIN
  -- Not while it is being diagnosed (the work is not approved to go ahead yet).
  BEGIN PERFORM kt.rfit(early, 1); RAISE EXCEPTION 'ASSERTION FAILED: part fitted while diagnosing' USING ERRCODE = 'KTF01';
  EXCEPTION WHEN SQLSTATE 'KT041' THEN NULL; END;
  f1 := kt.rfit(j, 2);
  PERFORM kt.assert(repair.wip(j) = 24.000, 'two screens at 12.000 in work-in-progress');
  PERFORM kt.assert((SELECT qty = 8 FROM inv.stock_balance WHERE variant_id = 'a3800000-0000-0000-0000-000000000001'), '8 left in stock');
  -- One comes back unused, at what it went out at; not more than were fitted.
  f2 := kt.rfit(j, -1, f1);
  PERFORM kt.assert(repair.wip(j) = 12.000, 'one screen left in work-in-progress');
  BEGIN PERFORM kt.rfit(j, -2, f1); RAISE EXCEPTION 'ASSERTION FAILED: returned more than fitted' USING ERRCODE = 'KTF01';
  EXCEPTION WHEN SQLSTATE 'KT041' THEN NULL; END;
  BEGIN UPDATE repair.repair_line SET cost = 0 WHERE id = f1; RAISE EXCEPTION 'ASSERTION FAILED: a work line changed' USING ERRCODE = 'KTF01';
  EXCEPTION WHEN SQLSTATE 'KT001' THEN NULL; END;
  PERFORM kt.rmove(j, 'testing'); PERFORM kt.rmove(j, 'completed'); PERFORM kt.rmove(j, 'ready');
  -- Not delivered while work-in-progress holds the part.
  BEGIN PERFORM kt.rmove(j, 'delivered'); RAISE EXCEPTION 'ASSERTION FAILED: delivered with parts in WIP' USING ERRCODE = 'KTF01';
  EXCEPTION WHEN SQLSTATE 'KT041' THEN NULL; END;
  -- More than it holds cannot leave; a write-off needs its reason and part.
  BEGIN
    INSERT INTO repair.wip_clearing (id, business_id, branch_id, job_id, kind, amount, created_by)
    VALUES (gen_random_uuid(), 'b0000000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-00000000000a', j, 'cost_of_repair', 12.001, 'a5000000-0000-0000-0000-000000000001');
    RAISE EXCEPTION 'ASSERTION FAILED: cleared more than WIP' USING ERRCODE = 'KTF01';
  EXCEPTION WHEN SQLSTATE 'KT041' THEN NULL; END;
  BEGIN
    INSERT INTO repair.wip_clearing (id, business_id, branch_id, job_id, kind, amount, line_id, created_by)
    VALUES (gen_random_uuid(), 'b0000000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-00000000000a', j, 'written_off', 1, f1, 'a5000000-0000-0000-0000-000000000001');
    RAISE EXCEPTION 'ASSERTION FAILED: write-off without a reason' USING ERRCODE = 'KTF01';
  EXCEPTION WHEN SQLSTATE '23514' THEN NULL; END;
  INSERT INTO repair.wip_clearing (id, business_id, branch_id, job_id, kind, amount, created_by)
  VALUES (gen_random_uuid(), 'b0000000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-00000000000a', j, 'cost_of_repair', 12.000, 'a5000000-0000-0000-0000-000000000001');
  PERFORM kt.rmove(j, 'delivered');
  -- Delivered: the work is closed and the job fixed, except that a passcode can still be purged.
  BEGIN PERFORM kt.rfit(j, -1, f1); RAISE EXCEPTION 'ASSERTION FAILED: work after delivery' USING ERRCODE = 'KTF01';
  EXCEPTION WHEN SQLSTATE 'KT041' THEN NULL; END;
  BEGIN UPDATE repair.repair_job SET problem = 'changed' WHERE id = j; RAISE EXCEPTION 'ASSERTION FAILED: delivered job changed' USING ERRCODE = 'KTF01';
  EXCEPTION WHEN SQLSTATE 'KT040' THEN NULL; END;
  UPDATE repair.repair_job SET passcode_purged_at = now() WHERE id = j;
END $$;

-- A passcode is kept only when one was provided; a job keeps its customer and device.
SELECT kt.expect_error($$UPDATE repair.repair_job SET passcode_enc = '\x01' WHERE status = 'received' AND passcode_mode = 'none'$$, '23514');
SELECT kt.expect_error($$UPDATE repair.repair_job SET customer_id = customer_id, customer_device_id = gen_random_uuid() WHERE status = 'received'$$, 'KT041');

-- ---------------------------------------------------------------- technician reassignments are logged (13.6)
DO $$
DECLARE j uuid := kt.rjob_at('received');
BEGIN
  UPDATE repair.repair_job SET technician_id = 'a5000000-0000-0000-0000-000000000001', updated_by = 'a5000000-0000-0000-0000-000000000001' WHERE id = j;
  UPDATE repair.repair_job SET technician_id = NULL WHERE id = j;
  PERFORM kt.assert((SELECT count(*) = 2 FROM repair.assignment WHERE job_id = j), 'two assignment rows');
END $$;

-- ---------------------------------------------------------------- deposits (13.1, 13.5)
DO $$
DECLARE j uuid := kt.rjob_at('received'); l1 uuid := gen_random_uuid(); d1 uuid := gen_random_uuid();
BEGIN
  -- A deposit names its job; store credit does not.
  BEGIN
    INSERT INTO crm.credit_ledger (id, business_id, branch_id, customer_id, balance_type, amount, source_doc_type, source_doc_id, created_by)
    VALUES (gen_random_uuid(), 'b0000000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-00000000000a', 'ac800000-0000-0000-0000-000000000001',
            'deposit', 10, 'repair_deposit', gen_random_uuid(), 'a5000000-0000-0000-0000-000000000001');
    RAISE EXCEPTION 'ASSERTION FAILED: deposit without its job' USING ERRCODE = 'KTF01';
  EXCEPTION WHEN SQLSTATE '23514' THEN NULL; END;
  INSERT INTO crm.credit_ledger (id, business_id, branch_id, customer_id, balance_type, amount, source_doc_type, source_doc_id, created_by, repair_job_id)
  VALUES (l1, 'b0000000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-00000000000a', 'ac800000-0000-0000-0000-000000000001',
          'deposit', 10.000, 'repair_deposit', d1, 'a5000000-0000-0000-0000-000000000001', j);
  INSERT INTO repair.deposit (id, business_id, branch_id, job_id, number, kind, amount, method, shift_id, terminal_id, credit_entry_id, created_by)
  VALUES (d1, 'b0000000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-00000000000a', j, 'DEP-T-1', 'deposit', 10.000, 'cash',
          'a6000000-0000-0000-0000-000000000001', 'd0000000-0000-0000-0000-00000000000a', l1, 'a5000000-0000-0000-0000-000000000001');
  -- The cash is a tender on the drawer: a payment with the deposit receipt and no sale.
  INSERT INTO sales.payment (id, business_id, branch_id, shift_id, method, amount, repair_deposit_id)
  VALUES (gen_random_uuid(), 'b0000000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-00000000000a', 'a6000000-0000-0000-0000-000000000001', 'cash', 10.000, d1);
  BEGIN
    INSERT INTO sales.payment (id, business_id, branch_id, shift_id, method, amount)
    VALUES (gen_random_uuid(), 'b0000000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-00000000000a', 'a6000000-0000-0000-0000-000000000001', 'cash', 1);
    RAISE EXCEPTION 'ASSERTION FAILED: a payment with neither a sale nor a deposit' USING ERRCODE = 'KTF01';
  EXCEPTION WHEN SQLSTATE '23514' THEN NULL; END;
  PERFORM kt.assert(repair.deposit_balance(j) = 10.000, 'the job holds 10.000');
  -- Never below zero for its job.
  BEGIN
    INSERT INTO crm.credit_ledger (id, business_id, branch_id, customer_id, balance_type, amount, source_doc_type, source_doc_id, created_by, repair_job_id)
    VALUES (gen_random_uuid(), 'b0000000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-00000000000a', 'ac800000-0000-0000-0000-000000000001',
            'deposit', -10.001, 'repair_deposit', gen_random_uuid(), 'a5000000-0000-0000-0000-000000000001', j);
    RAISE EXCEPTION 'ASSERTION FAILED: deposit below zero' USING ERRCODE = 'KTF01';
  EXCEPTION WHEN SQLSTATE 'KT042' THEN NULL; END;
  -- A refund needs its reason; a deposit is not taken as store credit.
  BEGIN
    INSERT INTO repair.deposit (id, business_id, branch_id, job_id, number, kind, amount, method, shift_id, credit_entry_id, created_by)
    VALUES (gen_random_uuid(), 'b0000000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-00000000000a', j, 'DEP-T-2', 'refund', 1, 'cash',
            'a6000000-0000-0000-0000-000000000001', l1, 'a5000000-0000-0000-0000-000000000001');
    RAISE EXCEPTION 'ASSERTION FAILED: refund without a reason' USING ERRCODE = 'KTF01';
  EXCEPTION WHEN SQLSTATE '23514' THEN NULL; END;
  PERFORM kt.assert(NOT EXISTS (SELECT 1 FROM repair.deposit_mismatches()), 'receipts and the ledger agree');
END $$;
SELECT kt.expect_error($$UPDATE repair.deposit SET amount = 1$$, 'KT001');

-- ---------------------------------------------------------------- photos keep their SHA-256
DO $$
DECLARE j uuid := kt.rjob_at('received'); bytes bytea := '\x89504e470d0a1a0a';
BEGIN
  INSERT INTO repair.photo (id, business_id, branch_id, job_id, stage, content_type, size_bytes, sha256, content, created_by)
  VALUES (gen_random_uuid(), 'b0000000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-00000000000a', j, 'intake', 'image/png', 8,
          encode(sha256(bytes), 'hex'), bytes, 'a5000000-0000-0000-0000-000000000001');
  BEGIN
    INSERT INTO repair.photo (id, business_id, branch_id, job_id, stage, content_type, size_bytes, sha256, content, created_by)
    VALUES (gen_random_uuid(), 'b0000000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-00000000000a', j, 'intake', 'image/png', 8,
            repeat('0', 64), bytes, 'a5000000-0000-0000-0000-000000000001');
    RAISE EXCEPTION 'ASSERTION FAILED: wrong hash accepted' USING ERRCODE = 'KTF01';
  EXCEPTION WHEN SQLSTATE '23514' THEN NULL; END;
END $$;

-- ---------------------------------------------------------------- WhatsApp consent needs a phone and its time
SELECT kt.expect_error($$UPDATE crm.customer SET whatsapp_opt_in = true WHERE id = 'ac800000-0000-0000-0000-000000000001'$$, '23514');
UPDATE crm.customer SET whatsapp_opt_in = true, whatsapp_opt_in_at = now() WHERE id = 'ac800000-0000-0000-0000-000000000001';
SELECT kt.expect_error($$INSERT INTO notif.outbound_message (id, business_id, branch_id, phone_e164, lang, template, body_text, expires_at)
  VALUES (gen_random_uuid(), 'b0000000-0000-0000-0000-000000000001', 'a0000000-0000-0000-0000-00000000000a', '+96892345678', 'en', 'repair_ready', 'and text', now())$$, '23514');

-- ---------------------------------------------------------------- the application role
RESET kite.all_branches;
SET ROLE kite_app;
SET kite.branch_ids = '{a0000000-0000-0000-0000-00000000000a}';
SELECT kt.expect_error($$DELETE FROM repair.repair_job$$, '42501');
SELECT kt.expect_error($$UPDATE repair.repair_line SET note = 'x'$$, '42501');
SELECT kt.expect_error($$UPDATE repair.deposit SET reason = 'x'$$, '42501');
SELECT kt.expect_error($$DELETE FROM repair.photo$$, '42501');
SELECT kt.assert((SELECT count(*) > 0 FROM repair.repair_job), 'sees its branch''s jobs');
SELECT kt.assert((SELECT count(*) > 0 FROM repair.estimate_line), 'and their estimate lines');
SET kite.branch_ids = '{a0000000-0000-0000-0000-00000000000b}';
SELECT kt.assert((SELECT count(*) = 0 FROM repair.repair_job), 'another branch sees none of them');
SELECT kt.assert((SELECT count(*) = 0 FROM repair.estimate_line), 'nor their estimate lines');
RESET ROLE;
RESET kite.branch_ids;

SELECT 'phase 8 repair tests passed' AS result;
