CREATE TABLE publications (
    project_id TEXT PRIMARY KEY REFERENCES projects(id) ON DELETE CASCADE,
    revision_id TEXT NOT NULL,
    engine_commit TEXT NOT NULL,
    title TEXT NOT NULL,
    description TEXT NOT NULL DEFAULT '',
    status TEXT NOT NULL CHECK(status IN ('pending','published','failed')),
    error TEXT NOT NULL DEFAULT '',
    package BLOB,
    byte_length INTEGER NOT NULL DEFAULT 0,
    sha256 TEXT NOT NULL DEFAULT '',
    requested_at TEXT NOT NULL,
    published_at TEXT NOT NULL DEFAULT '',
    CHECK(byte_length >= 0),
    CHECK(status != 'published' OR (package IS NOT NULL AND length(sha256) = 64))
);
CREATE INDEX publications_pending ON publications(status, requested_at);
CREATE INDEX publications_listed ON publications(status, published_at DESC);
PRAGMA user_version = 3;
