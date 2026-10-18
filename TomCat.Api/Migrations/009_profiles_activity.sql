ALTER TABLE users ADD COLUMN display_name TEXT NOT NULL DEFAULT '';
ALTER TABLE users ADD COLUMN avatar BLOB;
ALTER TABLE users ADD COLUMN avatar_version TEXT NOT NULL DEFAULT '';
ALTER TABLE users ADD COLUMN online_seconds INTEGER NOT NULL DEFAULT 0 CHECK(online_seconds>=0);
ALTER TABLE users ADD COLUMN activity_cursor INTEGER NOT NULL DEFAULT 0;
ALTER TABLE users ADD COLUMN last_seen_at INTEGER NOT NULL DEFAULT 0;
CREATE TABLE activity_presence (
    owner_id TEXT NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    tab_id TEXT NOT NULL,
    last_heartbeat INTEGER NOT NULL,
    PRIMARY KEY(owner_id,tab_id)
);
CREATE INDEX activity_presence_seen ON activity_presence(last_heartbeat);
CREATE TABLE activity_daily (
    owner_id TEXT NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    day TEXT NOT NULL,
    online_seconds INTEGER NOT NULL DEFAULT 0 CHECK(online_seconds>=0),
    PRIMARY KEY(owner_id,day)
);
CREATE INDEX activity_daily_day ON activity_daily(day);
CREATE TABLE remembered_logins (
    id TEXT PRIMARY KEY,
    owner_id TEXT NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    session_version INTEGER NOT NULL,
    expires_at INTEGER NOT NULL
);
CREATE INDEX remembered_logins_expiry ON remembered_logins(expires_at);
PRAGMA user_version = 9;
