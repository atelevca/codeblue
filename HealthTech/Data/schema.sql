-- Схема прикладных таблиц. Идемпотентна: прогоняется при каждом старте.
CREATE TABLE IF NOT EXISTS Jobs (
    Id           TEXT    PRIMARY KEY,
    FileName     TEXT    NOT NULL,
    ProfileKey   TEXT    NOT NULL,
    Status       TEXT    NOT NULL,
    CurrentStep  TEXT    NULL,
    Percent      INTEGER NOT NULL DEFAULT 0,
    WorkflowId   TEXT    NULL,
    Error        TEXT    NULL,
    CreatedAt    TEXT    NOT NULL,
    CompletedAt  TEXT    NULL
);

CREATE TABLE IF NOT EXISTS Persons (
    Id        TEXT PRIMARY KEY,
    FullName  TEXT NOT NULL,
    Specialty TEXT NULL
);

CREATE TABLE IF NOT EXISTS SpeakerBindings (
    JobId        TEXT NOT NULL,
    SpeakerLabel TEXT NOT NULL,
    PersonId     TEXT NOT NULL,
    PRIMARY KEY (JobId, SpeakerLabel)
);

-- Справочник врачей заводится руками; эндпоинта на добавление нет сознательно.
INSERT OR IGNORE INTO Persons (Id, FullName, Specialty) VALUES
    ('11111111-1111-1111-1111-111111111111', 'Ion Popescu',   'Cardiologie'),
    ('22222222-2222-2222-2222-222222222222', 'Maria Ciobanu', 'Oncologie'),
    ('33333333-3333-3333-3333-333333333333', 'Andrei Rusu',   'Terapie intensivă');
