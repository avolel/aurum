-- Runs once, on an empty data volume, as the superuser.
--
-- These live here rather than in an EF migration because CREATE EXTENSION needs
-- superuser rights, and the application role should not have them. If the image
-- ever stops shipping one of these, first boot fails here with a clear error
-- instead of failing later inside a migration.

CREATE EXTENSION IF NOT EXISTS timescaledb;
CREATE EXTENSION IF NOT EXISTS vector;
