CREATE TABLE ai_tool_events (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    project_id TEXT NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
    lease_id TEXT NOT NULL,
    run_id TEXT,
    request_id TEXT NOT NULL,
    tool TEXT NOT NULL,
    arguments TEXT NOT NULL,
    state TEXT NOT NULL,
    result TEXT,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    UNIQUE(lease_id, request_id)
);
CREATE INDEX ai_tool_events_run ON ai_tool_events(project_id, run_id, id);
CREATE TABLE project_knowledge (
    project_id TEXT NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
    key TEXT NOT NULL,
    content TEXT NOT NULL,
    version INTEGER NOT NULL,
    source_run_id TEXT,
    engine_commit TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    PRIMARY KEY(project_id, key)
);
CREATE TABLE project_experiments (
    project_id TEXT PRIMARY KEY REFERENCES projects(id) ON DELETE CASCADE,
    source_project_id TEXT REFERENCES projects(id) ON DELETE SET NULL,
    base_revision_id TEXT NOT NULL,
    created_at TEXT NOT NULL
);
PRAGMA user_version = 7;
