ALTER TABLE users ADD COLUMN is_admin INTEGER NOT NULL DEFAULT 0 CHECK(is_admin IN (0,1));
ALTER TABLE users ADD COLUMN is_suspended INTEGER NOT NULL DEFAULT 0 CHECK(is_suspended IN (0,1));
CREATE TABLE community_topics (
    id TEXT PRIMARY KEY,
    owner_id TEXT NOT NULL REFERENCES users(id),
    title TEXT NOT NULL,
    content TEXT NOT NULL,
    category TEXT NOT NULL,
    pinned INTEGER NOT NULL DEFAULT 0 CHECK(pinned IN (0,1)),
    created_at TEXT NOT NULL
);
CREATE INDEX community_topics_order ON community_topics(pinned DESC, created_at DESC, id DESC);
CREATE TABLE community_replies (
    id TEXT PRIMARY KEY,
    topic_id TEXT NOT NULL REFERENCES community_topics(id) ON DELETE CASCADE,
    owner_id TEXT NOT NULL REFERENCES users(id),
    content TEXT NOT NULL,
    created_at TEXT NOT NULL
);
CREATE INDEX community_replies_topic ON community_replies(topic_id, created_at, id);
CREATE TABLE publication_moderation (
    project_id TEXT PRIMARY KEY REFERENCES projects(id) ON DELETE CASCADE,
    reason TEXT NOT NULL,
    created_at TEXT NOT NULL
);
CREATE TABLE admin_audit (
    id TEXT PRIMARY KEY,
    actor_id TEXT NOT NULL REFERENCES users(id),
    action TEXT NOT NULL,
    target_id TEXT NOT NULL,
    reason TEXT NOT NULL,
    created_at TEXT NOT NULL
);
CREATE INDEX admin_audit_order ON admin_audit(created_at DESC, id DESC);
PRAGMA user_version = 8;
