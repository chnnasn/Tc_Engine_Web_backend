CREATE TABLE ai_sessions (
    id TEXT PRIMARY KEY,
    project_id TEXT NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
    title TEXT NOT NULL,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL
);
CREATE INDEX ai_sessions_project ON ai_sessions(project_id, updated_at);
CREATE TABLE ai_turns (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    session_id TEXT NOT NULL REFERENCES ai_sessions(id) ON DELETE CASCADE,
    run_id TEXT NOT NULL,
    prompt TEXT NOT NULL,
    state TEXT NOT NULL CHECK (state IN ('running', 'succeeded', 'failed')),
    output TEXT,
    error TEXT,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    UNIQUE(session_id, run_id)
);
CREATE UNIQUE INDEX ai_one_running_turn ON ai_turns(session_id) WHERE state = 'running';
PRAGMA user_version = 6;
