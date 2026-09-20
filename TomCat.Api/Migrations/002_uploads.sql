CREATE TABLE uploads (
    id TEXT PRIMARY KEY,
    project_id TEXT NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
    content_hash TEXT NOT NULL,
    byte_length INTEGER NOT NULL,
    content BLOB NOT NULL,
    created_at TEXT NOT NULL,
    UNIQUE(project_id, content_hash),
    CHECK(length(content) = byte_length)
);
CREATE TABLE revision_files (
    revision_id TEXT NOT NULL REFERENCES revisions(id) ON DELETE CASCADE,
    path TEXT NOT NULL,
    upload_id TEXT NOT NULL REFERENCES uploads(id) ON DELETE CASCADE,
    PRIMARY KEY(revision_id, path)
);
CREATE INDEX revision_files_upload ON revision_files(upload_id);
PRAGMA user_version = 2;
