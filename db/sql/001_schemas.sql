/* Corridor: schema + seed. Idempotent: safe to re-run. SQL Server / azure-sql-edge. */
IF DB_ID('Corridor') IS NULL
BEGIN
    CREATE DATABASE Corridor;
END;
GO
USE Corridor;
GO

IF SCHEMA_ID('perm') IS NULL EXEC('CREATE SCHEMA perm;');
IF SCHEMA_ID('trace') IS NULL EXEC('CREATE SCHEMA trace;');
IF SCHEMA_ID('idn') IS NULL EXEC('CREATE SCHEMA idn;');
GO

IF OBJECT_ID('perm.ImportPermits') IS NULL
BEGIN
    CREATE TABLE perm.ImportPermits (
        Id INT IDENTITY PRIMARY KEY,
        PermitNumber NVARCHAR(20) NOT NULL UNIQUE,
        LicenseeName NVARCHAR(160) NOT NULL,
        ItemDescription NVARCHAR(200) NOT NULL,
        Quantity INT NOT NULL,
        Purpose NVARCHAR(300) NOT NULL,
        Status NVARCHAR(20) NOT NULL,
        SubmittedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
        SubmittedBy NVARCHAR(120) NOT NULL
    );
END;
GO

IF OBJECT_ID('trace.TraceCases') IS NULL
BEGIN
    CREATE TABLE trace.TraceCases (
        CaseNumber NVARCHAR(16) PRIMARY KEY,
        LicenseeName NVARCHAR(160) NOT NULL,
        ItemDescription NVARCHAR(200) NOT NULL,
        Serial NVARCHAR(32) NOT NULL,
        Status NVARCHAR(20) NOT NULL,
        SubmittedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
        SubmittedBy NVARCHAR(120) NOT NULL,
        Disposition NVARCHAR(300) NULL
    );
END;
GO

IF OBJECT_ID('idn.Users') IS NULL
BEGIN
    CREATE TABLE idn.Users (
        Id INT IDENTITY PRIMARY KEY,
        Upn NVARCHAR(160) NOT NULL UNIQUE,
        DisplayName NVARCHAR(120) NOT NULL,
        Role NVARCHAR(40) NOT NULL,
        PasswordHash NVARCHAR(128) NOT NULL, /* demo only: sha256, documented as not production */
        ScimExternalId NVARCHAR(64) NULL,
        Active BIT NOT NULL DEFAULT 1
    );
END;
GO

IF OBJECT_ID('idn.MigrationApps') IS NULL
BEGIN
    CREATE TABLE idn.MigrationApps (
        AppKey NVARCHAR(20) PRIMARY KEY,
        AppName NVARCHAR(80) NOT NULL,
        TrustMode NVARCHAR(10) NOT NULL, /* Adfs | Dual | Okta */
        LastFlippedAt DATETIME2 NULL,
        FlippedBy NVARCHAR(120) NULL
    );
END;
GO

IF OBJECT_ID('idn.AuditEvents') IS NULL
BEGIN
    CREATE TABLE idn.AuditEvents (
        Id INT IDENTITY PRIMARY KEY,
        At DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
        Actor NVARCHAR(120) NOT NULL,
        AppKey NVARCHAR(20) NOT NULL,
        Event NVARCHAR(60) NOT NULL,
        Detail NVARCHAR(400) NULL
    );
END;
GO

IF OBJECT_ID('idn.Assignments') IS NULL
BEGIN
    CREATE TABLE idn.Assignments (
        Id INT IDENTITY PRIMARY KEY,
        InspectorUpn NVARCHAR(160) NOT NULL,
        LicenseeName NVARCHAR(160) NOT NULL,
        Focus NVARCHAR(200) NOT NULL,
        DueAt DATETIME2 NOT NULL,
        ChecklistJson NVARCHAR(MAX) NOT NULL
    );
END;
GO
