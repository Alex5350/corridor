USE Corridor;
GO

/* Trace procs: raw ADO.NET callers consume these. Legal transitions enforced in SQL. */

IF OBJECT_ID('trace.usp_SearchCases') IS NOT NULL DROP PROCEDURE trace.usp_SearchCases;
GO
CREATE PROCEDURE trace.usp_SearchCases
    @Requester NVARCHAR(120),
    @StatusFilter NVARCHAR(20) = NULL,
    @MaxRows INT = 50
AS
BEGIN
    SET NOCOUNT ON;
    SELECT TOP (@MaxRows)
        CaseNumber, LicenseeName, ItemDescription, Serial, Status,
        SubmittedAt, SubmittedBy, Disposition
    FROM trace.TraceCases
    WHERE (@StatusFilter IS NULL OR Status = @StatusFilter)
    ORDER BY SubmittedAt DESC;
END;
GO

IF OBJECT_ID('trace.usp_GetCase') IS NOT NULL DROP PROCEDURE trace.usp_GetCase;
GO
CREATE PROCEDURE trace.usp_GetCase
    @CaseNumber NVARCHAR(16)
AS
BEGIN
    SET NOCOUNT ON;
    SELECT CaseNumber, LicenseeName, ItemDescription, Serial, Status,
           SubmittedAt, SubmittedBy, Disposition
    FROM trace.TraceCases
    WHERE CaseNumber = @CaseNumber;
END;
GO

IF OBJECT_ID('trace.usp_CreateTraceRequest') IS NOT NULL DROP PROCEDURE trace.usp_CreateTraceRequest;
GO
CREATE PROCEDURE trace.usp_CreateTraceRequest
    @LicenseeName NVARCHAR(160),
    @ItemDescription NVARCHAR(200),
    @Serial NVARCHAR(32),
    @RequesterUpn NVARCHAR(120),
    @CaseNumber NVARCHAR(16) OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @Next INT = (SELECT ISNULL(MAX(TRY_CAST(SUBSTRING(CaseNumber, 5, 8) AS INT)), 100000)
                         FROM trace.TraceCases WHERE CaseNumber LIKE 'TRC-%') + 1;
    SET @CaseNumber = N'TRC-' + RIGHT('000000' + CAST(@Next AS NVARCHAR(8)), 6);
    INSERT trace.TraceCases (CaseNumber, LicenseeName, ItemDescription, Serial, Status, SubmittedBy)
    VALUES (@CaseNumber, @LicenseeName, @ItemDescription, @Serial, N'Received', @RequesterUpn);
END;
GO

IF OBJECT_ID('trace.usp_UpdateStatus') IS NOT NULL DROP PROCEDURE trace.usp_UpdateStatus;
GO
CREATE PROCEDURE trace.usp_UpdateStatus
    @CaseNumber NVARCHAR(16),
    @NewStatus NVARCHAR(20),
    @Actor NVARCHAR(120),
    @RowsAffected INT OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    SET @RowsAffected = 0;

    IF @NewStatus NOT IN (N'Received', N'UnderReview', N'Traced', N'Closed', N'Rejected')
    BEGIN
        RAISERROR('Unknown status %s', 16, 1, @NewStatus);
        RETURN;
    END;

    DECLARE @Current NVARCHAR(20) = (SELECT Status FROM trace.TraceCases WHERE CaseNumber = @CaseNumber);
    IF @Current IS NULL
    BEGIN
        RAISERROR('Case %s not found', 16, 1, @CaseNumber);
        RETURN;
    END;

    DECLARE @Legal BIT = 0;
    IF (@Current = N'Received'    AND @NewStatus IN (N'UnderReview', N'Rejected'))            OR
       (@Current = N'UnderReview' AND @NewStatus IN (N'Traced', N'Rejected'))                   OR
       (@Current = N'Traced'      AND @NewStatus = N'Closed')
        SET @Legal = 1;

    IF @Legal = 0
    BEGIN
        DECLARE @Msg NVARCHAR(200) = N'Illegal transition ' + @Current + N' to ' + @NewStatus +
                                     N' for case ' + @CaseNumber;
        RAISERROR(@Msg, 16, 1); /* xState 40001 handled by the caller */
        RETURN;
    END;

    UPDATE trace.TraceCases
       SET Status = @NewStatus,
           Disposition = CASE WHEN @NewStatus IN (N'Closed', N'Rejected')
                              THEN N'Set by ' + @Actor + N' at ' +
                                   CONVERT(NVARCHAR(19), SYSUTCDATETIME(), 120)
                              ELSE Disposition END
     WHERE CaseNumber = @CaseNumber;
    SET @RowsAffected = @@ROWCOUNT;
END;
GO
