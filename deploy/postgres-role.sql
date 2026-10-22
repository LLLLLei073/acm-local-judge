-- Run after creating acm_backend with a separately generated password.
-- Never include credentials in this file.
ALTER ROLE acm_backend NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;
GRANT USAGE ON SCHEMA acm TO acm_backend;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA acm TO acm_backend;
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA acm TO acm_backend;
DO $$
DECLARE t record;
BEGIN
  FOR t IN SELECT tablename FROM pg_tables WHERE schemaname='acm' LOOP
    EXECUTE format('DROP POLICY IF EXISTS backend_access ON acm.%I', t.tablename);
    EXECUTE format('CREATE POLICY backend_access ON acm.%I TO acm_backend USING (true) WITH CHECK (true)', t.tablename);
  END LOOP;
END $$;
