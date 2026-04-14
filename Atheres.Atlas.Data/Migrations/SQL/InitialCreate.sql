-- ============================================================
-- Atheres Atlas — Initial Database Schema
-- Generated from: 20260326000001_InitialCreate
-- Target:         Azure SQL / SQL Server 2019+
--
-- Run this script directly via SSMS, Azure Data Studio, or:
--   sqlcmd -S <server> -d <database> -i InitialCreate.sql
--
-- Tables created (in dependency order):
--   1. Routes
--   2. UserRouteSettings
--   3. Orders
--   4. RouteStops
--   5. Confirmations
--   6. AuditLogs
-- ============================================================

SET NOCOUNT ON;
SET QUOTED_IDENTIFIER ON;
GO

-- ============================================================
-- 1. Routes
-- ============================================================
IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'Routes')
BEGIN
    CREATE TABLE [dbo].[Routes] (
        [Id]                      UNIQUEIDENTIFIER  NOT NULL  DEFAULT NEWID(),
        [DeliveryDate]            DATETIME2         NOT NULL,
        [StartAddress]            NVARCHAR(500)     NOT NULL,
        [StartLatitude]           FLOAT             NOT NULL  DEFAULT 0,
        [StartLongitude]          FLOAT             NOT NULL  DEFAULT 0,
        [EndAddress]              NVARCHAR(500)     NOT NULL,
        [EndLatitude]             FLOAT             NOT NULL  DEFAULT 0,
        [EndLongitude]            FLOAT             NOT NULL  DEFAULT 0,
        [TotalStops]              INT               NOT NULL  DEFAULT 0,
        [TotalDistanceMeters]     FLOAT             NOT NULL  DEFAULT 0,
        [TotalDurationSeconds]    INT               NOT NULL  DEFAULT 0,
        [OptimizedWaypointOrder]  NVARCHAR(2000)    NULL,
        [OverviewPolyline]        NVARCHAR(4000)    NULL,
        [IsOptimized]             BIT               NOT NULL  DEFAULT 0,
        [CreatedAt]               DATETIME2         NOT NULL  DEFAULT SYSUTCDATETIME(),
        [UpdatedAt]               DATETIME2         NOT NULL  DEFAULT SYSUTCDATETIME(),
        CONSTRAINT [PK_Routes] PRIMARY KEY ([Id])
    );

    CREATE INDEX [IX_Routes_DeliveryDate]
        ON [dbo].[Routes] ([DeliveryDate]);

    PRINT 'Created table: Routes';
END
ELSE
    PRINT 'Skipped table: Routes (already exists)';
GO

-- ============================================================
-- 2. UserRouteSettings
-- ============================================================
IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'UserRouteSettings')
BEGIN
    CREATE TABLE [dbo].[UserRouteSettings] (
        [Id]                         UNIQUEIDENTIFIER  NOT NULL  DEFAULT NEWID(),
        [UserId]                     NVARCHAR(100)     NOT NULL,
        [StartAddress]               NVARCHAR(300)     NOT NULL,
        [StartCity]                  NVARCHAR(100)     NOT NULL  DEFAULT '',
        [StartState]                 NVARCHAR(50)      NOT NULL  DEFAULT '',
        [StartZip]                   NVARCHAR(20)      NOT NULL  DEFAULT '',
        [StartLatitude]              FLOAT             NULL,
        [StartLongitude]             FLOAT             NULL,
        [EndAddress]                 NVARCHAR(300)     NOT NULL,
        [EndCity]                    NVARCHAR(100)     NOT NULL  DEFAULT '',
        [EndState]                   NVARCHAR(50)      NOT NULL  DEFAULT '',
        [EndZip]                     NVARCHAR(20)      NOT NULL  DEFAULT '',
        [EndLatitude]                FLOAT             NULL,
        [EndLongitude]               FLOAT             NULL,
        [DeliveryWindowStart]        TIME              NOT NULL  DEFAULT '08:00:00',
        [DeliveryWindowEnd]          TIME              NOT NULL  DEFAULT '17:00:00',
        [ConfirmationDeadlineHours]  INT               NOT NULL  DEFAULT 3,
        [UpdatedAt]                  DATETIME2         NOT NULL  DEFAULT SYSUTCDATETIME(),
        CONSTRAINT [PK_UserRouteSettings] PRIMARY KEY ([Id])
    );

    CREATE UNIQUE INDEX [IX_UserRouteSettings_UserId]
        ON [dbo].[UserRouteSettings] ([UserId]);

    -- Seed default settings row (update addresses before first route run)
    INSERT INTO [dbo].[UserRouteSettings]
        ([Id], [UserId], [StartAddress], [StartCity], [StartState], [StartZip],
         [EndAddress], [EndCity], [EndState], [EndZip],
         [DeliveryWindowStart], [DeliveryWindowEnd], [ConfirmationDeadlineHours], [UpdatedAt])
    VALUES
        (NEWID(), 'default', '123 Depot Street', 'Denver', 'CO', '80201',
         '123 Depot Street', 'Denver', 'CO', '80201',
         '08:00:00', '17:00:00', 3, SYSUTCDATETIME());

    PRINT 'Created table: UserRouteSettings (seeded default row)';
END
ELSE
    PRINT 'Skipped table: UserRouteSettings (already exists)';
GO

-- ============================================================
-- 3. Orders
-- ============================================================
IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'Orders')
BEGIN
    CREATE TABLE [dbo].[Orders] (
        [Id]                   UNIQUEIDENTIFIER  NOT NULL  DEFAULT NEWID(),
        [StoreName]            NVARCHAR(200)     NOT NULL,
        [Address]              NVARCHAR(300)     NOT NULL,
        [City]                 NVARCHAR(100)     NOT NULL,
        [State]                NVARCHAR(50)      NOT NULL,
        [Zip]                  NVARCHAR(20)      NOT NULL,
        [County]               NVARCHAR(100)     NOT NULL  DEFAULT '',
        [LicenseNumber]        NVARCHAR(100)     NOT NULL  DEFAULT '',
        [District]             NVARCHAR(100)     NOT NULL  DEFAULT '',
        [Zone]                 NVARCHAR(100)     NOT NULL  DEFAULT '',
        [OrderDate]            DATETIME2         NOT NULL,
        [Email]                NVARCHAR(254)     NOT NULL,
        [Phone]                NVARCHAR(30)      NULL,
        [Latitude]             FLOAT             NULL,
        [Longitude]            FLOAT             NULL,
        [FormattedAddress]     NVARCHAR(500)     NULL,
        [Status]               NVARCHAR(50)      NOT NULL  DEFAULT 'Received',
        [ExpectedDeliveryDate] DATETIME2         NULL,
        [ConfirmationDeadline] DATETIME2         NULL,
        [RescheduleCount]      INT               NOT NULL  DEFAULT 0,
        [ConfirmationToken]    NVARCHAR(64)      NULL,
        [ConfirmedAt]          DATETIME2         NULL,
        [RouteId]              UNIQUEIDENTIFIER  NULL,
        [StopSequence]         INT               NULL,
        [CreatedAt]            DATETIME2         NOT NULL  DEFAULT SYSUTCDATETIME(),
        [UpdatedAt]            DATETIME2         NOT NULL  DEFAULT SYSUTCDATETIME(),
        [ValidationErrors]     NVARCHAR(2000)    NULL,
        [Notes]                NVARCHAR(1000)    NULL,
        CONSTRAINT [PK_Orders] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_Orders_Routes_RouteId]
            FOREIGN KEY ([RouteId]) REFERENCES [dbo].[Routes] ([Id])
            ON DELETE SET NULL
    );

    CREATE INDEX [IX_Orders_ConfirmationToken]    ON [dbo].[Orders] ([ConfirmationToken]);
    CREATE INDEX [IX_Orders_Email]                ON [dbo].[Orders] ([Email]);
    CREATE INDEX [IX_Orders_ExpectedDeliveryDate] ON [dbo].[Orders] ([ExpectedDeliveryDate]);
    CREATE INDEX [IX_Orders_OrderDate]            ON [dbo].[Orders] ([OrderDate]);
    CREATE INDEX [IX_Orders_RouteId]              ON [dbo].[Orders] ([RouteId]);
    CREATE INDEX [IX_Orders_Status]               ON [dbo].[Orders] ([Status]);

    PRINT 'Created table: Orders';
END
ELSE
    PRINT 'Skipped table: Orders (already exists)';
GO

-- ============================================================
-- 4. RouteStops
-- ============================================================
IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'RouteStops')
BEGIN
    CREATE TABLE [dbo].[RouteStops] (
        [Id]                  UNIQUEIDENTIFIER  NOT NULL  DEFAULT NEWID(),
        [RouteId]             UNIQUEIDENTIFIER  NOT NULL,
        [OrderId]             UNIQUEIDENTIFIER  NOT NULL,
        [Sequence]            INT               NOT NULL,
        [Address]             NVARCHAR(500)     NOT NULL,
        [Latitude]            FLOAT             NOT NULL  DEFAULT 0,
        [Longitude]           FLOAT             NOT NULL  DEFAULT 0,
        [StoreName]           NVARCHAR(200)     NOT NULL,
        [EstimatedArrival]    DATETIME2         NULL,
        [EstimatedDeparture]  DATETIME2         NULL,
        [ServiceTimeMinutes]  INT               NOT NULL  DEFAULT 15,
        [LegDistanceMeters]   FLOAT             NOT NULL  DEFAULT 0,
        [LegDurationSeconds]  INT               NOT NULL  DEFAULT 0,
        CONSTRAINT [PK_RouteStops] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_RouteStops_Routes_RouteId]
            FOREIGN KEY ([RouteId]) REFERENCES [dbo].[Routes] ([Id])
            ON DELETE CASCADE,
        -- NO ACTION: prevents multiple-cascade-path conflict.
        -- Application removes RouteStops before deleting Orders.
        CONSTRAINT [FK_RouteStops_Orders_OrderId]
            FOREIGN KEY ([OrderId]) REFERENCES [dbo].[Orders] ([Id])
            ON DELETE NO ACTION
    );

    CREATE INDEX [IX_RouteStops_OrderId]
        ON [dbo].[RouteStops] ([OrderId]);

    CREATE INDEX [IX_RouteStops_RouteId_Sequence]
        ON [dbo].[RouteStops] ([RouteId], [Sequence]);

    PRINT 'Created table: RouteStops';
END
ELSE
    PRINT 'Skipped table: RouteStops (already exists)';
GO

-- ============================================================
-- 5. Confirmations
-- ============================================================
IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'Confirmations')
BEGIN
    CREATE TABLE [dbo].[Confirmations] (
        [Id]            UNIQUEIDENTIFIER  NOT NULL  DEFAULT NEWID(),
        [OrderId]       UNIQUEIDENTIFIER  NOT NULL,
        [Status]        NVARCHAR(50)      NOT NULL  DEFAULT 'Pending',
        [Token]         NVARCHAR(64)      NOT NULL,
        [EmailSentTo]   NVARCHAR(254)     NULL,
        [SmsSentTo]     NVARCHAR(30)      NULL,
        [EmailSentAt]   DATETIME2         NULL,
        [SmsSentAt]     DATETIME2         NULL,
        [ConfirmedAt]   DATETIME2         NULL,
        [ConfirmedBy]   NVARCHAR(20)      NULL,
        [ExpiresAt]     DATETIME2         NOT NULL,
        [AttemptNumber] INT               NOT NULL  DEFAULT 1,
        [CreatedAt]     DATETIME2         NOT NULL  DEFAULT SYSUTCDATETIME(),
        CONSTRAINT [PK_Confirmations] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_Confirmations_Orders_OrderId]
            FOREIGN KEY ([OrderId]) REFERENCES [dbo].[Orders] ([Id])
            ON DELETE CASCADE
    );

    CREATE UNIQUE INDEX [IX_Confirmations_Token]
        ON [dbo].[Confirmations] ([Token]);

    CREATE INDEX [IX_Confirmations_ExpiresAt]
        ON [dbo].[Confirmations] ([ExpiresAt]);

    CREATE INDEX [IX_Confirmations_OrderId]
        ON [dbo].[Confirmations] ([OrderId]);

    CREATE INDEX [IX_Confirmations_Status]
        ON [dbo].[Confirmations] ([Status]);

    PRINT 'Created table: Confirmations';
END
ELSE
    PRINT 'Skipped table: Confirmations (already exists)';
GO

-- ============================================================
-- 6. AuditLogs
-- ============================================================
IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'AuditLogs')
BEGIN
    CREATE TABLE [dbo].[AuditLogs] (
        [Id]             UNIQUEIDENTIFIER  NOT NULL  DEFAULT NEWID(),
        [OrderId]        UNIQUEIDENTIFIER  NULL,
        [RouteId]        UNIQUEIDENTIFIER  NULL,
        [EventType]      NVARCHAR(100)     NOT NULL,
        [EventName]      NVARCHAR(100)     NOT NULL,
        [Details]        NVARCHAR(4000)    NULL,
        [PreviousStatus] NVARCHAR(50)      NULL,
        [NewStatus]      NVARCHAR(50)      NULL,
        [Success]        BIT               NOT NULL  DEFAULT 1,
        [ErrorMessage]   NVARCHAR(2000)    NULL,
        [AgentName]      NVARCHAR(100)     NULL,
        [OccurredAt]     DATETIME2         NOT NULL  DEFAULT SYSUTCDATETIME(),
        CONSTRAINT [PK_AuditLogs] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_AuditLogs_Orders_OrderId]
            FOREIGN KEY ([OrderId]) REFERENCES [dbo].[Orders] ([Id])
            ON DELETE SET NULL
    );

    CREATE INDEX [IX_AuditLogs_EventType]  ON [dbo].[AuditLogs] ([EventType]);
    CREATE INDEX [IX_AuditLogs_OccurredAt] ON [dbo].[AuditLogs] ([OccurredAt]);
    CREATE INDEX [IX_AuditLogs_OrderId]    ON [dbo].[AuditLogs] ([OrderId]);

    PRINT 'Created table: AuditLogs';
END
ELSE
    PRINT 'Skipped table: AuditLogs (already exists)';
GO

-- ============================================================
-- Verify
-- ============================================================
SELECT
    t.TABLE_NAME,
    COUNT(c.COLUMN_NAME) AS ColumnCount
FROM INFORMATION_SCHEMA.TABLES  t
JOIN INFORMATION_SCHEMA.COLUMNS c ON c.TABLE_NAME = t.TABLE_NAME
WHERE t.TABLE_TYPE = 'BASE TABLE'
  AND t.TABLE_NAME IN ('Routes','UserRouteSettings','Orders','RouteStops','Confirmations','AuditLogs')
GROUP BY t.TABLE_NAME
ORDER BY t.TABLE_NAME;
GO

PRINT '=== Schema deployment complete ===';
GO
