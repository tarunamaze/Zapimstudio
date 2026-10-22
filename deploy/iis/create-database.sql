-- One-time SQL Server setup for Zapim Studio. Run as a SQL Server administrator, e.g.:
--   sqlcmd -S localhost -E -b -i create-database.sql -v DatabaseName="ZapimStudio" AppLogin="IIS APPPOOL\ZapimStudio"
-- In SSMS, enable Query > SQLCMD Mode first.
--
-- Creates an empty, dedicated database and gives the IIS application pool identity the rights the app
-- needs: create its tables on first start (db_ddladmin) and read/write data. The app must have its own
-- database: it only creates its tables when the database is empty.
:on error exit

IF DB_ID(N'$(DatabaseName)') IS NULL
    CREATE DATABASE [$(DatabaseName)];
GO

IF SUSER_ID(N'$(AppLogin)') IS NULL
    CREATE LOGIN [$(AppLogin)] FROM WINDOWS;
GO

USE [$(DatabaseName)];
GO

IF USER_ID(N'$(AppLogin)') IS NULL
    CREATE USER [$(AppLogin)] FOR LOGIN [$(AppLogin)];
ALTER ROLE db_ddladmin ADD MEMBER [$(AppLogin)];
ALTER ROLE db_datareader ADD MEMBER [$(AppLogin)];
ALTER ROLE db_datawriter ADD MEMBER [$(AppLogin)];
GO

PRINT N'Database $(DatabaseName) is ready for $(AppLogin).';
