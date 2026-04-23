using System.Net;
using System.Text.Json;
using Atheres.Atlas.Data;
using Atheres.Atlas.Data.Repositories;
using Atheres.Atlas.Domain.DTOs;
using Atheres.Atlas.Domain.Entities;
using Atheres.Atlas.Domain.Enums;
using Atheres.Atlas.Domain.Messages;
using Atheres.Atlas.Functions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Atheres.Atlas.Functions.Agents;

/// <summary>
/// Public endpoint for receiving orders from warehouse systems.
/// Orders are matched to Warehouse and Store by license number.
/// No geocoding needed — coordinates come from the Store master data.
/// </summary>
public class OrderIngestionAgent
{
    private readonly IOrderRepository _orders;
    private readonly IServiceBusPublisher _bus;
    private readonly AtlasDbContext _db;
    private readonly IGoogleMapsService _maps;
    private readonly ILogger<OrderIngestionAgent> _logger;

    public OrderIngestionAgent(
        IOrderRepository orders,
        IServiceBusPublisher bus,
        AtlasDbContext db,
        IGoogleMapsService maps,
        ILogger<OrderIngestionAgent> logger)
    {
        _orders = orders;
        _bus    = bus;
        _db     = db;
        _maps   = maps;
        _logger = logger;
    }

    /// <summary>
    /// Public endpoint: accepts a JSON array of orders identified by license numbers.
    /// POST /api/orders
    ///
    /// AuthorizationLevel.Anonymous because the demo simulator (served from a
    /// different origin, with no auth token plumbed in) posts to this endpoint.
    /// The request body carries CompanySlug explicitly, so there's no need for
    /// a caller identity at the Functions-host layer. If we later want to gate
    /// this behind a JWT, add a Bearer-token check inside the handler rather
    /// than switching back to Function-level auth (which would require shipping
    /// a function key to a public-facing SPA).
    /// </summary>
    [Function(nameof(IngestOrders))]
    public async Task<HttpResponseData> IngestOrders(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "orders")] HttpRequestData req,
        CancellationToken ct)
    {
        OrderInputDto? dto;
        try
        {
            dto = await JsonSerializer.DeserializeAsync<OrderInputDto>(
                req.Body,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true },
                ct);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Invalid JSON body on order ingest");
            var bad = req.CreateResponse(HttpStatusCode.BadRequest);
            await bad.WriteStringAsync("Invalid JSON body.", ct);
            return bad;
        }

        if (dto is null)
        {
            var empty = req.CreateResponse(HttpStatusCode.BadRequest);
            await empty.WriteStringAsync("No order provided.", ct);
            return empty;
        }

        // Resolve company
        var company = await _db.Companies.IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => (c.Slug == dto.CompanySlug || c.Id.ToString() == dto.CompanySlug) && c.IsActive, ct);

        if (company is null)
        {
            var notFound = req.CreateResponse(HttpStatusCode.BadRequest);
            await notFound.WriteAsJsonAsync(new { error = $"Company not found: {dto.CompanySlug}" }, ct);
            return notFound;
        }

        var warehouse = await _db.Warehouses.IgnoreQueryFilters()
            .FirstOrDefaultAsync(w => w.LicenseNumber == dto.WarehouseLicenseNumber && w.CompanyId == company.Id, ct);

        if (warehouse is null)
        {
            var notFound = req.CreateResponse(HttpStatusCode.BadRequest);
            await notFound.WriteAsJsonAsync(new { error = $"Warehouse not found: {dto.WarehouseLicenseNumber}" }, ct);
            return notFound;
        }

        var store = await _db.Stores.IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.LicenseNumber == dto.StoreLicenseNumber && s.CompanyId == company.Id, ct);

        if (store is null)
        {
            var notFound = req.CreateResponse(HttpStatusCode.BadRequest);
            await notFound.WriteAsJsonAsync(new { error = $"Store not found: {dto.StoreLicenseNumber}" }, ct);
            return notFound;
        }

        if (dto.Items.Count == 0)
        {
            var noItems = req.CreateResponse(HttpStatusCode.BadRequest);
            await noItems.WriteAsJsonAsync(new { error = "Order must have at least one item." }, ct);
            return noItems;
        }

        // Self-heal: if this store has never been geocoded, do it once and persist
        // to the Store row. Stops orders from being silently unroutable when
        // imports skip lat/lng.
        if (store.Latitude is null || store.Longitude is null)
        {
            var geo = await _maps.GeocodeAsync(store.FullAddress, ct);
            if (geo is not null)
            {
                store.Latitude         = geo.Latitude;
                store.Longitude        = geo.Longitude;
                store.FormattedAddress = geo.FormattedAddress;
                store.UpdatedAt        = DateTime.UtcNow;
            }
            else
            {
                _logger.LogWarning("Could not geocode store {Store} ({License}); order will be unroutable.",
                    store.Name, store.LicenseNumber);
            }
        }

        // Upsert products
        var skus = dto.Items.Select(i => i.Sku).Distinct().ToList();
        var existingProducts = await _db.Products
            .Where(p => skus.Contains(p.Sku))
            .ToDictionaryAsync(p => p.Sku, ct);

        var order = new Order
        {
            CompanyId              = company.Id,
            WarehouseLicenseNumber = dto.WarehouseLicenseNumber,
            StoreLicenseNumber     = dto.StoreLicenseNumber,
            WarehouseId            = warehouse.Id,
            StoreId                = store.Id,
            StoreName              = store.Name,
            Address                = store.Address,
            City                   = store.City,
            State                  = store.State,
            Zip                    = store.Zip,
            County                 = store.County ?? string.Empty,
            LicenseNumber          = store.LicenseNumber ?? string.Empty,
            District               = store.Region ?? string.Empty,
            Zone                   = store.Customer,
            Email                  = store.Email ?? string.Empty,
            Phone                  = store.Phone,
            Latitude               = store.Latitude,
            Longitude              = store.Longitude,
            FormattedAddress       = store.FormattedAddress,
            OrderDate              = dto.OrderDate,
            Notes                  = dto.Notes,
            Status                 = OrderStatus.Ordered,
        };

        foreach (var itemDto in dto.Items)
        {
            if (!existingProducts.TryGetValue(itemDto.Sku, out var product))
            {
                product = new Product
                {
                    Sku      = itemDto.Sku,
                    Name     = itemDto.Name,
                    Category = itemDto.Category,
                };
                _db.Products.Add(product);
                existingProducts[itemDto.Sku] = product;
            }
            else if (product.Name != itemDto.Name)
            {
                product.Name = itemDto.Name;
                product.UpdatedAt = DateTime.UtcNow;
            }

            order.Items.Add(new OrderItem
            {
                ProductId = product.Id,
                Sku       = itemDto.Sku,
                Name      = itemDto.Name,
                Quantity  = itemDto.Quantity,
            });
        }

        await _orders.AddAsync(order, ct);
        await _orders.SaveChangesAsync(ct);

        // The broadcast notification is cosmetic — a SignalR "Order Received"
        // toast for whoever's watching the main app. Don't let a Service Bus
        // outage fail the ingest; the order is already persisted.
        try
        {
            await _bus.PublishAsync(ServiceBusQueues.Notifications, new NotificationMessage(
                Guid.NewGuid(), company.Id, NotificationType.OrderReceived,
                "Order Received",
                $"Order with {order.Items.Count} items for {order.StoreName}.",
                order.Id, null, null, null, DateTime.UtcNow), ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Order {OrderId} saved but notification publish failed (queue unavailable)", order.Id);
        }

        _logger.LogInformation("Ingested order {OrderId} with {ItemCount} items", order.Id, order.Items.Count);

        var ok = req.CreateResponse(HttpStatusCode.Accepted);
        await ok.WriteAsJsonAsync(new
        {
            orderId = order.Id,
            storeName = order.StoreName,
            itemCount = order.Items.Count,
            items = order.Items.Select(i => new { i.Id, i.Sku, i.Name, i.Quantity }),
        }, ct);
        return ok;
    }
}
