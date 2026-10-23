ALTER TABLE publications ADD COLUMN build_job_id TEXT NOT NULL DEFAULT '';
CREATE TABLE build_jobs (
    id TEXT PRIMARY KEY,
    project_id TEXT NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
    revision_id TEXT NOT NULL,
    status TEXT NOT NULL CHECK(status IN ('queued','running','completed','failed','cancelled')),
    lease_token TEXT NOT NULL DEFAULT '',
    lease_until INTEGER NOT NULL DEFAULT 0,
    deadline INTEGER NOT NULL DEFAULT 0,
    attempts INTEGER NOT NULL DEFAULT 0,
    next_attempt INTEGER NOT NULL DEFAULT 0,
    created_at TEXT NOT NULL,
    finished_at TEXT NOT NULL DEFAULT ''
);
CREATE INDEX build_jobs_dispatch ON build_jobs(status, next_attempt, created_at);
-- Preserve requests that were pending when the in-process cooker was replaced.
INSERT INTO build_jobs(id, project_id, revision_id, status, created_at)
SELECT lower(hex(randomblob(16))), project_id, revision_id, 'queued', requested_at
FROM publications WHERE status='pending';
UPDATE publications SET build_job_id=(SELECT id FROM build_jobs WHERE build_jobs.project_id=publications.project_id)
WHERE status='pending';
PRAGMA user_version = 10;
