-- migrate:up

ALTER TABLE reminder_occurrences ADD COLUMN IF NOT EXISTS custom_body_ciphertext bytea NULL;

-- migrate:down

ALTER TABLE reminder_occurrences DROP COLUMN IF EXISTS custom_body_ciphertext;
