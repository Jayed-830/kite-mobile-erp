-- V0004: tax codes and effective-dated rates (blueprint 17.4). Rates are data, never code.

CREATE TABLE tax.tax_code (
  id                       uuid PRIMARY KEY,
  business_id              uuid NOT NULL REFERENCES core.business(id),
  code                     text NOT NULL CHECK (code ~ '^[A-Z0-9-]{2,20}$'),
  name_en                  text NOT NULL,
  name_ar                  text NOT NULL,
  kind                     text NOT NULL CHECK (kind IN ('standard','zero','exempt','out_of_scope','margin')),
  status                   text NOT NULL DEFAULT 'active' CHECK (status IN ('active','inactive')),
  -- Seeded codes stay flagged until the business's accountant confirms them (0.5, 26.5).
  needs_accountant_review  boolean NOT NULL DEFAULT true,
  reviewed_by              uuid,
  reviewed_at              timestamptz,
  created_at               timestamptz NOT NULL DEFAULT now(),
  created_by               uuid,
  updated_at               timestamptz NOT NULL DEFAULT now(),
  updated_by               uuid,
  version                  integer NOT NULL DEFAULT 1,
  UNIQUE (business_id, code),
  CHECK (needs_accountant_review OR (reviewed_by IS NOT NULL AND reviewed_at IS NOT NULL))
);
CREATE TRIGGER tax_code_touch BEFORE UPDATE ON tax.tax_code FOR EACH ROW EXECUTE FUNCTION core.touch_row();

CREATE TABLE tax.tax_rate (
  id              uuid PRIMARY KEY,
  tax_code_id     uuid NOT NULL REFERENCES tax.tax_code(id),
  rate_percent    numeric(7,4) NOT NULL CHECK (rate_percent >= 0 AND rate_percent < 100),
  effective_from  date NOT NULL,
  effective_to    date,
  created_at      timestamptz NOT NULL DEFAULT now(),
  created_by      uuid,
  CHECK (effective_to IS NULL OR effective_to > effective_from),
  -- No two rates of one code may overlap in time.
  CONSTRAINT tax_rate_no_overlap EXCLUDE USING gist (
    tax_code_id WITH =,
    daterange(effective_from, coalesce(effective_to, 'infinity'::date), '[)') WITH &&
  )
);

-- Rate in force for a code on a date (used by reports and checks; the app caches it).
CREATE OR REPLACE FUNCTION tax.rate_on(p_tax_code_id uuid, p_on date) RETURNS numeric
LANGUAGE sql STABLE AS $$
  SELECT rate_percent FROM tax.tax_rate
   WHERE tax_code_id = p_tax_code_id
     AND effective_from <= p_on
     AND (effective_to IS NULL OR effective_to > p_on)
$$;
