-- Private backend schema. Do not add acm to the Supabase Data API exposed schemas.
-- No secrets or user data belong in this file.
CREATE SCHEMA IF NOT EXISTS acm;
REVOKE ALL ON SCHEMA acm FROM PUBLIC, anon, authenticated;

CREATE TABLE IF NOT EXISTS acm.metadata(key text PRIMARY KEY, value text NOT NULL);
CREATE TABLE IF NOT EXISTS acm.problems(rowid bigint GENERATED ALWAYS AS IDENTITY, id text PRIMARY KEY, body text NOT NULL);
CREATE TABLE IF NOT EXISTS acm.contests(rowid bigint GENERATED ALWAYS AS IDENTITY, id text PRIMARY KEY, body text NOT NULL);
CREATE TABLE IF NOT EXISTS acm.users(rowid bigint GENERATED ALWAYS AS IDENTITY, id text PRIMARY KEY, studentid text NOT NULL, body text NOT NULL);
CREATE UNIQUE INDEX IF NOT EXISTS users_studentid_ci ON acm.users(lower(studentid));
CREATE UNIQUE INDEX IF NOT EXISTS users_email_ci ON acm.users(lower(body::jsonb->>'email'));
CREATE UNIQUE INDEX IF NOT EXISTS users_cf_ci ON acm.users(lower(body::jsonb->>'codeforcesHandle')) WHERE coalesce(body::jsonb->>'codeforcesHandle','') <> '';
CREATE TABLE IF NOT EXISTS acm.submissions(id text PRIMARY KEY, problemid text NOT NULL, title text NOT NULL, mode text NOT NULL, status text NOT NULL, createdat text NOT NULL, userid text);
CREATE INDEX IF NOT EXISTS submissions_user_created ON acm.submissions(userid, createdat DESC);
CREATE INDEX IF NOT EXISTS submissions_problem_created ON acm.submissions(problemid, createdat DESC);
CREATE INDEX IF NOT EXISTS submissions_status_created ON acm.submissions(status, createdat);
CREATE TABLE IF NOT EXISTS acm.ratings(rowid bigint GENERATED ALWAYS AS IDENTITY, id text PRIMARY KEY, userid text NOT NULL, body text NOT NULL);
CREATE INDEX IF NOT EXISTS ratings_user ON acm.ratings(userid, rowid);
CREATE TABLE IF NOT EXISTS acm.cf_history(userid text PRIMARY KEY, body text NOT NULL);
CREATE TABLE IF NOT EXISTS acm.remote_jobs(
    id text PRIMARY KEY, kind text NOT NULL, state text NOT NULL DEFAULT 'Queued',
    payload bytea NOT NULL, output bytea, createdat timestamptz NOT NULL DEFAULT now(),
    lease text, leaseuntil timestamptz
);
CREATE INDEX IF NOT EXISTS jobs_pending ON acm.remote_jobs(createdat) WHERE state='Queued';

-- Enable RLS defensively even though this schema is not exposed to the Data API.
ALTER TABLE acm.metadata ENABLE ROW LEVEL SECURITY;
ALTER TABLE acm.problems ENABLE ROW LEVEL SECURITY;
ALTER TABLE acm.contests ENABLE ROW LEVEL SECURITY;
ALTER TABLE acm.users ENABLE ROW LEVEL SECURITY;
ALTER TABLE acm.submissions ENABLE ROW LEVEL SECURITY;
ALTER TABLE acm.ratings ENABLE ROW LEVEL SECURITY;
ALTER TABLE acm.cf_history ENABLE ROW LEVEL SECURITY;
ALTER TABLE acm.remote_jobs ENABLE ROW LEVEL SECURITY;
REVOKE ALL ON ALL TABLES IN SCHEMA acm FROM PUBLIC, anon, authenticated;
REVOKE ALL ON ALL SEQUENCES IN SCHEMA acm FROM PUBLIC, anon, authenticated;
INSERT INTO acm.metadata(key,value) VALUES('schema_version','1') ON CONFLICT(key) DO NOTHING;
