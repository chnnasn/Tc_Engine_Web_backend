ALTER TABLE users ADD COLUMN email TEXT COLLATE NOCASE;
ALTER TABLE users ADD COLUMN email_verified_at TEXT;
CREATE UNIQUE INDEX users_email ON users(email) WHERE email IS NOT NULL;
CREATE TABLE email_challenges (
 id TEXT PRIMARY KEY, email TEXT NOT NULL COLLATE NOCASE, password_hash TEXT NOT NULL,
 owner_id TEXT REFERENCES users(id), code_hash TEXT NOT NULL, expires_at INTEGER NOT NULL,
 sent_at INTEGER NOT NULL, attempts INTEGER NOT NULL DEFAULT 0, setup_hash TEXT, verified INTEGER NOT NULL DEFAULT 0
);
CREATE INDEX email_challenges_email ON email_challenges(email, sent_at);
PRAGMA user_version = 4;
