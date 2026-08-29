USE Corridor;
GO

/* Seed: synthetic, no real persons or licensees. Demo password for all users:
   Demo1234! (hash = sha256 of 'corridor-demo-' + password; demo only). */

IF NOT EXISTS (SELECT 1 FROM idn.Users)
BEGIN
    INSERT idn.Users (Upn, DisplayName, Role, PasswordHash, ScimExternalId, Active) VALUES
    (N'admin@corridor.example',     N'Dana Whitfield',  N'Admin',     CONVERT(NVARCHAR(128), HASHBYTES('SHA2_256', 'corridor-demo-Demo1234!'), 2), NULL, 1),
    (N'inspector@corridor.example', N'Miguel Sandoval', N'Inspector', CONVERT(NVARCHAR(128), HASHBYTES('SHA2_256', 'corridor-demo-Demo1234!'), 2), NULL, 1),
    (N'officer@corridor.example',   N'Priya Raman',     N'Officer',   CONVERT(NVARCHAR(128), HASHBYTES('SHA2_256', 'corridor-demo-Demo1234!'), 2), NULL, 1),
    (N'clerk@corridor.example',     N'Tom Biestecker',  N'Clerk',     CONVERT(NVARCHAR(128), HASHBYTES('SHA2_256', 'corridor-demo-Demo1234!'), 2), NULL, 1);
END;
GO

IF NOT EXISTS (SELECT 1 FROM idn.MigrationApps)
BEGIN
    INSERT idn.MigrationApps (AppKey, AppName, TrustMode) VALUES
    (N'portal', N'PermitPortal (OIDC web app)',      N'Adfs'),
    (N'legacy', N'TraceLink (SOAP case service)',    N'Adfs'),
    (N'spa',    N'FieldInsight (inspector SPA)',     N'Adfs');
END;
GO

IF NOT EXISTS (SELECT 1 FROM trace.TraceCases)
BEGIN
    INSERT trace.TraceCases (CaseNumber, LicenseeName, ItemDescription, Serial, Status, SubmittedBy, Disposition) VALUES
    (N'TRC-100101', N'Riverside Sporting Goods',      N'Kalvin KB-7 .22 bolt rifle',      N'KB7-0041882', N'Received',    N'officer@corridor.example', NULL),
    (N'TRC-100102', N'Northgate Firearms Exchange',   N'Merrin M-12 shotgun 12ga',        N'M12-771204',  N'UnderReview', N'officer@corridor.example', NULL),
    (N'TRC-100103', N'Lakeshore Ammo and Tackle',     N'Halden H-9 pistol 9mm',           N'H9-0004417',  N'Traced',      N'officer@corridor.example', NULL),
    (N'TRC-100104', N'Cedar Valley Pawn',             N'Orlan bolt rifle .308',           N'ORL-330991',  N'Closed',      N'officer@corridor.example', N'Set by officer@corridor.example at 2026-08-28 21:14:03'),
    (N'TRC-100105', N'Summit Range Supply',           N'Ardent AR-22 rimfire',            N'AR2-9013345', N'Received',    N'clerk@corridor.example',   NULL),
    (N'TRC-100106', N'Harborview Collectibles',       N'Vernley single-shot .410',        N'VN4-112900',  N'UnderReview', N'clerk@corridor.example',   NULL),
    (N'TRC-100107', N'Fieldstone Outfitters',         N'Merrin M-12 shotgun 12ga',        N'M12-774910',  N'Rejected',    N'officer@corridor.example', N'Set by officer@corridor.example at 2026-08-29 18:41:55'),
    (N'TRC-100108', N'Bluff Point Gun Club',          N'Kalvin KB-7 .22 bolt rifle',      N'KB7-0061033', N'Received',    N'clerk@corridor.example',   NULL),
    (N'TRC-100109', N'Millbrook Trade Post',          N'Halden H-9 pistol 9mm',           N'H9-0005180',  N'Traced',      N'officer@corridor.example', NULL),
    (N'TRC-100110', N'Quarry Ridge Distributors',     N'Orlan bolt rifle .308',           N'ORL-331540',  N'Received',    N'clerk@corridor.example',   NULL),
    (N'TRC-100111', N'Old Mill Firearms',             N'Ardent AR-22 rimfire',            N'AR2-9018220', N'UnderReview', N'officer@corridor.example', NULL),
    (N'TRC-100112', N'Greenfield Armory LLC',         N'Vernley single-shot .410',        N'VN4-113417',  N'Received',    N'clerk@corridor.example',   NULL);
END;
GO

IF NOT EXISTS (SELECT 1 FROM perm.ImportPermits)
BEGIN
    INSERT perm.ImportPermits (PermitNumber, LicenseeName, ItemDescription, Quantity, Purpose, Status, SubmittedBy) VALUES
    (N'IP-2026-0301', N'Harborview Collectibles',   N'Merrin M-12 shotgun 12ga',    24, N'Retail stock replenishment',      N'Approved',  N'clerk@corridor.example'),
    (N'IP-2026-0302', N'Fieldstone Outfitters',     N'Kalvin KB-7 .22 bolt rifle',  60, N'Seasonal hunting inventory',      N'UnderReview', N'clerk@corridor.example'),
    (N'IP-2026-0303', N'Summit Range Supply',       N'Ardent AR-22 rimfire',        120, N'Range rental fleet',              N'UnderReview', N'clerk@corridor.example'),
    (N'IP-2026-0304', N'Quarry Ridge Distributors', N'Halden H-9 pistol 9mm',       200, N'Wholesale distribution',          N'Approved',  N'clerk@corridor.example'),
    (N'IP-2026-0305', N'Old Mill Firearms',         N'Vernley single-shot .410',    30, N'Collector consignment',           N'Rejected',  N'clerk@corridor.example'),
    (N'IP-2026-0306', N'Cedar Valley Pawn',         N'Orlan bolt rifle .308',       15, N'Store inventory',                 N'UnderReview', N'clerk@corridor.example'),
    (N'IP-2026-0307', N'Northgate Firearms Exchange', N'Merrin M-12 shotgun 12ga',  45, N'Event promotion stock',           N'Approved',  N'clerk@corridor.example'),
    (N'IP-2026-0308', N'Millbrook Trade Post',      N'Kalvin KB-7 .22 bolt rifle',  80, N'Catalog resale',                  N'UnderReview', N'clerk@corridor.example');
END;
GO

IF NOT EXISTS (SELECT 1 FROM idn.Assignments)
BEGIN
    INSERT idn.Assignments (InspectorUpn, LicenseeName, Focus, DueAt, ChecklistJson) VALUES
    (N'inspector@corridor.example', N'Riverside Sporting Goods',    N'Bound-book reconciliation and inventory sampling', DATEADD(day, 7, SYSUTCDATETIME()), N'[{"item":"Review acquisition log","done":false},{"item":"Sample 10 percent of serials","done":false},{"item":"Verify permit records","done":false}]'),
    (N'inspector@corridor.example', N'Summit Range Supply',         N'Rental fleet maintenance and disposition records', DATEADD(day, 12, SYSUTCDATETIME()), N'[{"item":"Check rental logs","done":false},{"item":"Confirm transfer paperwork","done":false}]'),
    (N'inspector@corridor.example', N'Quarry Ridge Distributors',   N'Wholesale shipping documentation review',         DATEADD(day, 18, SYSUTCDATETIME()), N'[{"item":"Audit outbound manifests","done":false},{"item":"Spot-check three shipments","done":false},{"item":"Interview records custodian","done":false}]'),
    (N'inspector@corridor.example', N'Old Mill Firearms',           N'Consignment intake process walkthrough',          DATEADD(day, 21, SYSUTCDATETIME()), N'[{"item":"Observe intake procedure","done":false},{"item":"Review consignor files","done":false}]'),
    (N'inspector@corridor.example', N'Greenfield Armory LLC',       N'Annual compliance re-inspection',                 DATEADD(day, 28, SYSUTCDATETIME()), N'[{"item":"Full bound-book audit","done":false},{"item":"Security storage check","done":false},{"item":"Exit interview","done":false}]'),
    (N'inspector@corridor.example', N'Harborview Collectibles',     N'Import permit matching against received stock',    DATEADD(day, 35, SYSUTCDATETIME()), N'[{"item":"Match permits to items","done":false},{"item":"Flag mismatches","done":false}]');
END;
GO
