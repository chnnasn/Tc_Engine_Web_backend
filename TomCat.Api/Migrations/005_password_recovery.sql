ALTER TABLE users ADD COLUMN session_version INTEGER NOT NULL DEFAULT 0;
CREATE TABLE password_resets (
 id TEXT PRIMARY KEY, owner_id TEXT NOT NULL REFERENCES users(id), email TEXT NOT NULL COLLATE NOCASE,
 code_hash TEXT NOT NULL, expires_at INTEGER NOT NULL, sent_at INTEGER NOT NULL,
 attempts INTEGER NOT NULL DEFAULT 0, session_version INTEGER NOT NULL
);
CREATE INDEX password_resets_email ON password_resets(email, sent_at);
PRAGMA user_version = 5;
