-- =============================================================================
-- Nuke order + route data — destructive wipe of every order and every routing
-- artifact built from those orders.
--
-- Runs unconditionally at the end of each deploy script (deploy-azure.ps1,
-- deploy-debug.ps1, deploy-docker.ps1) so iterative end-to-end testing
-- (CSV import → schedule → optimize) never accumulates carry-over rows
-- between runs.
--
-- Scope:
--   * DELETE Orders.
--   * DELETE Routes, RouteStops, OrderBatches, Confirmations.
--   * Master / reference data (Companies, Stores, Warehouses, Hubs, Trucks,
--     Districts, Zones, Users, etc.) is left intact.
--
-- FK order: children of Orders first (RouteStops, Confirmations), then
-- Orders themselves (whose own FKs point at Routes and OrderBatches),
-- then the now-unreferenced parents.
--
-- Table names mirror the EF configurations:
--   Confirmations  (DeliveryConfirmation entity, ToTable("Confirmations"))
--   Routes         (DeliveryRoute entity,        ToTable("Routes"))
--   RouteStops     (RouteStop entity)
--   Orders         (Order entity)
--   OrderBatches   (OrderBatch entity)
-- =============================================================================
SET XACT_ABORT ON;
BEGIN TRANSACTION;

DELETE FROM RouteStops;
DELETE FROM Confirmations;

DELETE FROM Orders;

DELETE FROM Routes;
DELETE FROM OrderBatches;

COMMIT TRANSACTION;
