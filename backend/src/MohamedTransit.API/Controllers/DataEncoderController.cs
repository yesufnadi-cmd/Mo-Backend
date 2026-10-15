using Mapster;

using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

using MohamedTransit.API.DTO.MOT.Request;
using MohamedTransit.Application.Commands.DataEncoder;
using MohamedTransit.API.DTO.MOT.Response;
using MohamedTransit.API.Helpers;
using MohamedTransit.API.Services;
using MohamedTransit.Application.Commands.Shipment;
using MohamedTransit.Application.DTO;
using MohamedTransit.Application.Helper;
using MohamedTransit.Application.Queries;
using MohamedTransit.Domain.Common;
using MohamedTransit.Domain.Data;
using MohamedTransit.Domain.Entities;


namespace MohamedTransit.API.Controllers.MOT;

[ApiController]
[Route("api/v1/[controller]")]
public class DataEncoderController : BaseController
{
    private readonly ApplicationDbContext _context;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IFileStorageService _fileStorageService;

    public DataEncoderController(
        ApplicationDbContext context,
        IHttpContextAccessor httpContextAccessor,
        IFileStorageService fileStorageService)
    {
        _context = context;
        _httpContextAccessor = httpContextAccessor;
        _fileStorageService = fileStorageService;
    }

    /// <summary>
    /// Create a new customer
    /// </summary>
    [HttpPost("CreateCustomer")]
    public async Task<IActionResult> CreateCustomer(
        [FromBody] MohamedTransit.API.DTO.MasterData.Request.CreateCustomerRequest request,
        CancellationToken cancellationToken)
    {
        var currentUserId = JwtHelper.GetCurrentUserId(_httpContextAccessor, _context);
        if (currentUserId == null)
            return Unauthorized("User not authenticated");
        if (!await IsDataEncoder(currentUserId.Value, cancellationToken))
            return Forbid();
        if (request.UserId <= 0)
            return BadRequest("A valid customer user ID is required.");

        var isActiveCustomerUser = await _context.Users
            .AsNoTracking()
            .AnyAsync(user =>
                user.Id == request.UserId &&
                user.RecordStatus == RecordStatus.Active &&
                user.UserRoles.Any(userRole =>
                    userRole.Role.RecordStatus == RecordStatus.Active &&
                    userRole.Role.Name.ToLower() == "customer"),
                cancellationToken);
        if (!isActiveCustomerUser)
            return BadRequest("The selected user is not an active customer account.");

        var customerAlreadyExists = await _context.Customers
            .AsNoTracking()
            .AnyAsync(customer => customer.UserId == request.UserId, cancellationToken);
        if (customerAlreadyExists)
            return Conflict("A customer profile already exists for this user.");

        // Create customer profile
        var customer = Customer.Create(
            request.BusinessName,
            request.TINNumber,
            request.BusinessLicense,
            request.BusinessAddress,
            request.City,
            request.State,
            request.PostalCode,
            request.ContactPerson,
            request.ContactPhone,
            request.ContactEmail,
            request.BusinessType,
            request.ImportLicense,
            request.ImportLicenseExpiry,
            request.UserId,
            currentUserId.Value
        );

        _context.Customers.Add(customer);
        await _context.SaveChangesAsync(cancellationToken);

        return HandleSuccessResponse(new
        {
            customer.Id,
            customer.BusinessName,
            customer.TINNumber,
            customer.BusinessLicense,
            customer.BusinessAddress,
            customer.City,
            customer.State,
            customer.PostalCode,
            customer.ContactPerson,
            customer.ContactPhone,
            customer.ContactEmail,
            customer.BusinessType,
            customer.ImportLicense,
            customer.ImportLicenseExpiry,
            customer.UserId,
            customer.IsVerified
        });
    }

    /// <summary>
    /// Get all customers created by the data encoder
    /// </summary>
    [HttpGet("GetAllCustomers/{recordStatus}")]
    public async Task<IActionResult> GetAllCustomers(
        RecordStatus? recordStatus,
        CancellationToken cancellationToken)
    {
        var currentUserId = JwtHelper.GetCurrentUserId(_httpContextAccessor, _context);
        if (currentUserId == null)
            return Unauthorized("User not authenticated");
        if (!await IsDataEncoder(currentUserId.Value, cancellationToken))
            return Forbid();

        var query = new GetAllCustomersQuery { RecordStatus = recordStatus };
        var result = await _mediator.Send(query, cancellationToken);
        if (result.IsError)
            return HandleErrorResponse(result.Errors);

        var customers = result.Payload ?? [];
        var customerUserIds = customers
            .Where(customer => customer.UserId.HasValue)
            .Select(customer => customer.UserId!.Value)
            .Distinct()
            .ToArray();
        var activeStatuses = new[]
        {
            ShipmentStatus.Submitted,
            ShipmentStatus.UnderReview,
            ShipmentStatus.Approved,
            ShipmentStatus.InProgress
        };
        var activeCasesByImporter = await _context.Shipments
            .Where(shipment =>
                customerUserIds.Contains(shipment.ImporterId) &&
                shipment.RecordStatus == RecordStatus.Active &&
                activeStatuses.Contains(shipment.Status))
            .GroupBy(shipment => shipment.ImporterId)
            .Select(group => new { ImporterId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(item => item.ImporterId, item => item.Count, cancellationToken);

        var rolesList = customers.Adapt<List<CustomerDetail>>();
        for (var index = 0; index < customers.Count; index++)
        {
            var customer = customers[index];
            rolesList[index].RegisteredAt = customer.CreateAt;
            rolesList[index].ActiveCases =
                customer.UserId.HasValue && activeCasesByImporter.TryGetValue(customer.UserId.Value, out var count)
                    ? count
                    : 0;
        }

        return HandleSuccessResponse(rolesList);

    }
    /// <summary>
    /// Update customer information before approval
    /// </summary>
    [HttpPut("UpdateCustomer")]
    public async Task<IActionResult> UpdateCustomer(
        [FromBody] MohamedTransit.API.DTO.MOT.Request.UpdateCustomerRequest request,
        CancellationToken cancellationToken)
    {
        var currentUserId = JwtHelper.GetCurrentUserId(_httpContextAccessor, _context);
        if (currentUserId == null)
            return Unauthorized("User not authenticated");
        if (!await IsDataEncoder(currentUserId.Value, cancellationToken))
            return Forbid();
        if (request.Id <= 0)
            return BadRequest("A valid customer ID is required.");

        var customer = await _context.Customers
            .FirstOrDefaultAsync(c =>
                c.Id == request.Id &&
                c.CreatedByDataEncoderId == currentUserId.Value &&
                c.RecordStatus == RecordStatus.Active,
                cancellationToken);

        if (customer == null)
            return NotFound("Customer not found.");
        if (customer.IsVerified)
            return Conflict("Verified customer information cannot be changed.");


        customer.UpdateBusinessInfo(
            request.BusinessName,
            request.BusinessAddress,
            request.City,
            request.State,
            request.PostalCode,
            request.ContactPerson,
            request.ContactPhone,
            request.ContactEmail
        );

        await _context.SaveChangesAsync(cancellationToken);

        return HandleSuccessResponse(new
        {
            customer.Id,
            customer.BusinessName,
            customer.BusinessAddress,
            customer.City,
            customer.State,
            customer.PostalCode,
            customer.ContactPerson,
            customer.ContactPhone,
            customer.ContactEmail,
            customer.IsVerified
        });
    }
    // POST: api/v1/DataEncoder/services
    [HttpPost("services")]
    [HttpPost("Create services")]
    public async Task<IActionResult> CreateService([FromBody] CreateServiceRequestDto request, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        var currentUserId = JwtHelper.GetCurrentUserId(_httpContextAccessor, _context);
        if (currentUserId == null)
            return Unauthorized("User not authenticated");

        if (!await IsDataEncoder(currentUserId.Value, cancellationToken))
            return Forbid();

        if (request.CustomerId <= 0 ||
            string.IsNullOrWhiteSpace(request.ItemDescription) ||
            string.IsNullOrWhiteSpace(request.RouteCategory) ||
            request.DeclaredValue < 0 ||
            string.IsNullOrWhiteSpace(request.TaxCategory) ||
            string.IsNullOrWhiteSpace(request.CountryOfOrigin) ||
            string.IsNullOrWhiteSpace(request.ServiceType))
        {
            return BadRequest("Customer, shipment details, and a non-negative declared value are required.");
        }

        var serviceType = request.ServiceType.Trim().ToLowerInvariant() switch
        {
            "multimodal" => ServiceType.Multimodal,
            "unimodal" => ServiceType.Unimodal,
            _ => (ServiceType?)null
        };
        if (serviceType == null)
            return BadRequest("Service type must be Multimodal or Unimodal.");

        var customer = await _context.Customers
            .AsNoTracking()
            .FirstOrDefaultAsync(
                customer => customer.Id == request.CustomerId &&
                            customer.CreatedByDataEncoderId == currentUserId.Value &&
                            customer.RecordStatus == RecordStatus.Active,
                cancellationToken);
        if (customer == null)
            return NotFound("Customer not found for this data encoder.");
        if (customer.UserId == null)
            return BadRequest("The selected customer is not linked to an importer account.");

        var service = Shipment.Create(
            $"MT-{Guid.NewGuid():N}",
            request.ItemDescription.Trim(),
            customer.UserId.Value,
            request.ItemDescription.Trim(),
            serviceType == ServiceType.Multimodal
                ? TransportMode.MultiModalSeaRail
                : TransportMode.UniModalRoad,
            serviceType == ServiceType.Multimodal ? HubLocation.Mojo : HubLocation.Adama,
            request.CountryOfOrigin.Trim(),
            request.RouteCategory.Trim(),
            currentUserId.Value);
        service.UpdateServiceType(serviceType.Value);
        service.UpdateStatus(ShipmentStatus.Draft);
        service.UpdateDetails(
            request.ItemDescription.Trim(),
            request.RouteCategory.Trim(),
            request.DeclaredValue,
            request.TaxCategory.Trim(),
            request.CountryOfOrigin.Trim(),
            null);
        service.SetCreatedByDataEncoder(currentUserId.Value);
        service.AssignmentNotes = request.Notes?.Trim();

        foreach (var stage in DefaultServiceWorkflowStages.CreateFor(service, serviceType.Value))
        {
            service.AddStage(stage);
        }

        _context.Shipments.Add(service);
        await _context.SaveChangesAsync(cancellationToken);

        return Ok(new
        {
            success = true,
            message = "Service draft created successfully.",
            data = new
            {
                id = service.Id,
                trackingNumber = service.TrackingNumber,
                status = service.Status.ToString()
            }
        });
    }
    [HttpPut("services/{id}/service-type")]
    public async Task<IActionResult> UpdateServiceType(
        long id,
        [FromBody] UpdateServiceTypeRequest request,
        CancellationToken cancellationToken)
    {
        var currentUserId = JwtHelper.GetCurrentUserId(_httpContextAccessor, _context);
        if (currentUserId == null)
            return Unauthorized("User not authenticated");
        if (!await IsDataEncoder(currentUserId.Value, cancellationToken))
            return Forbid();

        var service = await _context.Shipments.FirstOrDefaultAsync(
            shipment => shipment.Id == id &&
                        shipment.CreatedByDataEncoderId == currentUserId.Value &&
                        shipment.RecordStatus == RecordStatus.Active,
            cancellationToken);
        if (service == null)
            return NotFound("Service not found");
        if (service.Status != ShipmentStatus.Draft)
            return Conflict("Service details can only be updated before submission.");
        if (!Enum.IsDefined(typeof(ServiceType), request.ServiceType))
            return BadRequest("A valid service type is required.");

        var serviceTypeChanged = service.ServiceType != request.ServiceType;
        if (serviceTypeChanged)
        {
            var existingStages = await _context.ServiceStageExecutions
                .Where(stage => stage.ShipmentId == service.Id)
                .ToListAsync(cancellationToken);
            var stageIds = existingStages.Select(stage => stage.Id).ToArray();
            var hasStageData = stageIds.Length > 0 &&
                (await _context.StageDocuments.AnyAsync(
                    document => document.ServiceStageId.HasValue &&
                                stageIds.Contains(document.ServiceStageId.Value),
                    cancellationToken) ||
                 await _context.StageComments.AnyAsync(
                    comment => comment.ServiceStageId.HasValue &&
                               stageIds.Contains(comment.ServiceStageId.Value),
                    cancellationToken) ||
                 await _context.Transports.AnyAsync(
                    transport => transport.ServiceStageId.HasValue &&
                                 stageIds.Contains(transport.ServiceStageId.Value),
                    cancellationToken));
            var hasProgress = existingStages.Any(stage =>
                stage.Status != (stage.SequenceOrder == 1 ? StageStatus.InProgress : StageStatus.NotStarted));

            if (hasStageData || hasProgress)
                return Conflict("The service type cannot be changed after workflow stage activity has started.");

            _context.ServiceStageExecutions.RemoveRange(existingStages);
        }

        service.UpdateServiceType(request.ServiceType);
        if (serviceTypeChanged)
        {
            foreach (var stage in DefaultServiceWorkflowStages.CreateFor(service, request.ServiceType))
            {
                service.AddStage(stage);
            }
        }

        await _context.SaveChangesAsync(cancellationToken);

        return HandleSuccessResponse(new { Success = true, Message = "Service type updated successfully." });
    }
    [HttpPost("services/{id}/documents")]
    public async Task<IActionResult> UploadServiceDocuments(
        long id,
        [FromForm] List<IFormFile> files,
        CancellationToken cancellationToken)
    {
        var currentUserId = JwtHelper.GetCurrentUserId(_httpContextAccessor, _context);
        if (currentUserId == null)
            return Unauthorized("User not authenticated");
        if (!await IsDataEncoder(currentUserId.Value, cancellationToken))
            return Forbid();

        var service = await _context.Shipments
            .FirstOrDefaultAsync(
                shipment => shipment.Id == id &&
                            shipment.CreatedByDataEncoderId == currentUserId.Value &&
                            shipment.RecordStatus == RecordStatus.Active,
                cancellationToken);

        if (service == null)
            return NotFound("Service not found");
        if (service.Status != ShipmentStatus.Draft)
            return Conflict("Documents can only be added before service submission.");

        if (files == null || files.Count == 0)
            return BadRequest("No files uploaded.");

        var allowedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".pdf", ".jpg", ".jpeg", ".png", ".xlsx"
        };
        foreach (var file in files)
        {
            var extension = Path.GetExtension(file.FileName);
            if (file.Length <= 0 ||
                file.Length > 25 * 1024 * 1024 ||
                !allowedExtensions.Contains(extension))
                return BadRequest("Each document must be a PDF, JPG, PNG, or XLSX file no larger than 25 MB.");
        }

        foreach (var file in files)
        {
            var filePath = await _fileStorageService.SaveFileAsync(file, "uploads/documents");
            var document = ServiceDocument.Create(
                Path.GetFileName(filePath),
                filePath,
                Path.GetFileName(file.FileName),
                Path.GetExtension(file.FileName),
                file.Length,
                file.ContentType,
                DocumentType.Other,
                id,
                currentUserId.Value,
                description: "Supporting document");
            _context.ServiceDocuments.Add(document);
        }
        await _context.SaveChangesAsync(cancellationToken);

        return HandleSuccessResponse(new { Success = true, Message = "Documents uploaded successfully." });
    }
    [HttpPost("services/{id}/submit")]
    public async Task<IActionResult> SubmitService(long id, CancellationToken cancellationToken)
    {
        var currentUserId = JwtHelper.GetCurrentUserId(_httpContextAccessor, _context);
        if (currentUserId == null)
            return Unauthorized("User not authenticated");
        if (!await IsDataEncoder(currentUserId.Value, cancellationToken))
            return Forbid();

        var service = await _context.Shipments.FirstOrDefaultAsync(
            shipment => shipment.Id == id &&
                        shipment.CreatedByDataEncoderId == currentUserId.Value &&
                        shipment.RecordStatus == RecordStatus.Active,
            cancellationToken);
        if (service == null)
            return NotFound("Service not found");

        if (service.Status != ShipmentStatus.Draft)
            return Conflict("Only draft services can be submitted.");

        var stagesExist = await _context.ServiceStageExecutions
            .AnyAsync(stage => stage.ShipmentId == service.Id, cancellationToken);
        if (!stagesExist)
        {
            var serviceType = service.ServiceType ?? ServiceType.Multimodal;
            foreach (var stage in DefaultServiceWorkflowStages.CreateFor(service, serviceType))
            {
                service.AddStage(stage);
            }
        }

        service.Submit();
        await _context.SaveChangesAsync(cancellationToken);

        return HandleSuccessResponse(new { Success = true, Message = "Service submitted successfully and locked for approval." });
    }

    // GET: api/v1/DataEncoder/services
    [HttpGet("services")]
    public async Task<ActionResult<IEnumerable<ShipmentDetail>>> GetAllServices(CancellationToken cancellationToken)
    {
        var currentUserId = JwtHelper.GetCurrentUserId(_httpContextAccessor, _context);
        if (currentUserId == null)
            return Unauthorized("User not authenticated");
        if (!await IsDataEncoder(currentUserId.Value, cancellationToken))
            return Forbid();

        var services = await _context.Shipments
            .AsNoTracking()
            .Where(service =>
                service.CreatedByDataEncoderId == currentUserId.Value &&
                service.RecordStatus == RecordStatus.Active)
            .OrderByDescending(service => service.CreateAt)
            .Select(service => new ShipmentDetail
            {
                Id = service.Id,
                ServiceNumber = service.TrackingNumber,
                ItemDescription = service.Description,
                RouteCategory = service.RouteCategory,
                DeclaredValue = service.DeclaredValue,
                TaxCategory = service.TaxCategory,
                CountryOfOrigin = service.CountryOfOrigin,
                Origin = service.Origin,
                Destination = service.Destination,
                ServiceType = service.ServiceType ?? ServiceType.Multimodal,
                Status = service.Status,
                RiskLevel = service.RiskLevel,
                CustomerId = service.ImporterId,
                ImporterName = _context.Customers
                    .Where(customer =>
                        customer.UserId == service.ImporterId &&
                        customer.RecordStatus == RecordStatus.Active)
                    .Select(customer => customer.BusinessName)
                    .FirstOrDefault() ?? string.Empty,
                ImporterTinNumber = _context.Customers
                    .Where(customer =>
                        customer.UserId == service.ImporterId &&
                        customer.RecordStatus == RecordStatus.Active)
                    .Select(customer => customer.TINNumber)
                    .FirstOrDefault() ?? string.Empty,
                AssignedCaseExecutorId = service.AssignedCaseExecutorId,
                AssignedAssessorId = service.AssignedAssessorId,
                RegisteredDate = service.CreateAt,
                DueAt = service.Status == ShipmentStatus.Submitted || service.Status == ShipmentStatus.UnderReview
                    ? service.CreateAt.AddHours(48)
                    : null,
                LastUpdateDate = service.UpdatedAt
            })
            .ToListAsync(cancellationToken);

        return Ok(services);
    }
    /// <summary>
    /// Get all documents across all services for the Document Centre
    /// </summary>
    /// <summary>
    /// Get all documents across all services for the Document Centre
    /// </summary>
    /// <summary>
    /// Get all documents across all services for the Document Centre
    /// <summary>
    /// Get all documents across all services for the Document Centre
    /// </summary>
    /// <summary>
    /// Get all documents across all services for the Document Centre
    /// </summary>
    /// <summary>
    /// Get all documents across all services for the Document Centre
    /// </summary>
    [HttpGet("documents")]
    public async Task<IActionResult> GetAllDocuments(CancellationToken cancellationToken)
    {
        var currentUserId = JwtHelper.GetCurrentUserId(_httpContextAccessor, _context);
        if (currentUserId == null)
            return Unauthorized("User not authenticated");
        if (!await IsDataEncoder(currentUserId.Value, cancellationToken))
            return Forbid();

        var documents = await _context.ServiceDocuments
            .AsNoTracking()
            .Where(document =>
                document.RecordStatus == RecordStatus.Active &&
                document.Shipment != null &&
                document.Shipment.RecordStatus == RecordStatus.Active &&
                document.Shipment.CreatedByDataEncoderId == currentUserId.Value)
            .OrderByDescending(document => document.CreateAt)
            .Select(document => new
            {
                document.Id,
                document.OriginalFileName,
                document.DocumentType,
                ServiceReference = document.Shipment!.TrackingNumber,
                document.ShipmentStageId,
                UploadedByFirstName = document.UploadedByUser == null
                    ? null
                    : document.UploadedByUser.FirstName,
                UploadedByLastName = document.UploadedByUser == null
                    ? null
                    : document.UploadedByUser.LastName,
                document.CreateAt,
                document.RecordStatus
            })
            .ToListAsync(cancellationToken);

        var documentDtos = documents.Select(document => new
        {
            id = document.Id,
            documentName = document.OriginalFileName,
            documentType = document.DocumentType.ToString(),
            // Reference ፋንታ የ Shipmentን Id መጠቀም (ወይም በሞዴልዎ ያለውን ትክክለኛ ስም ማስገባት)
            serviceReference = document.ServiceReference,
            stage = document.ShipmentStageId?.ToString() ?? "N/A",
            uploadedBy = string.IsNullOrWhiteSpace(document.UploadedByFirstName) &&
                         string.IsNullOrWhiteSpace(document.UploadedByLastName)
                ? "System"
                : $"{document.UploadedByFirstName} {document.UploadedByLastName}".Trim(),
            uploadedDate = document.CreateAt.ToString("dd MMM yyyy, HH:mm"),
            status = document.RecordStatus.ToString()
        });

        return Ok(new { Success = true, Data = documentDtos });
    }
    /// <summary>
    /// Get data encoder dashboard
    /// </summary>
    [HttpGet("GetDashboard")]
    public async Task<IActionResult> GetDashboard(CancellationToken cancellationToken)
    {
        var currentUserId = JwtHelper.GetCurrentUserId(_httpContextAccessor, _context);
        if (currentUserId == null)
            return Unauthorized("User not authenticated");
        if (!await IsDataEncoder(currentUserId.Value, cancellationToken))
            return Forbid();

        var now = DateTime.UtcNow;
        var serviceReviewStatuses = new[] { ShipmentStatus.Submitted, ShipmentStatus.UnderReview };

        var dashboard = new MohamedTransit.API.DTO.MOT.Response.DataEncoderDashboardResponse
        {
            TotalCustomersCreated = await _context.Customers.CountAsync(c =>
                c.CreatedByDataEncoderId == currentUserId.Value &&
                c.RecordStatus == RecordStatus.Active, cancellationToken),
            PendingCustomerApprovals = await _context.Customers.CountAsync(c =>
                c.CreatedByDataEncoderId == currentUserId.Value &&
                c.RecordStatus == RecordStatus.Active &&
                !c.IsVerified, cancellationToken),
            TotalServicesCreated = await _context.Shipments.CountAsync(s =>
                s.CreatedByDataEncoderId == currentUserId.Value &&
                s.RecordStatus == RecordStatus.Active, cancellationToken),
            PendingServiceApprovals = await _context.Shipments.CountAsync(s =>
                s.CreatedByDataEncoderId == currentUserId.Value &&
                s.RecordStatus == RecordStatus.Active &&
                s.Status == ShipmentStatus.Submitted, cancellationToken),
            DraftServices = await _context.Shipments.CountAsync(s =>
                s.CreatedByDataEncoderId == currentUserId.Value &&
                s.RecordStatus == RecordStatus.Active &&
                s.Status == ShipmentStatus.Draft, cancellationToken),
            DueWithin48Hours = await _context.Shipments.CountAsync(s =>
                s.CreatedByDataEncoderId == currentUserId.Value &&
                s.RecordStatus == RecordStatus.Active &&
                serviceReviewStatuses.Contains(s.Status) &&
                s.CreateAt > now.AddHours(-48) &&
                s.CreateAt <= now,
                cancellationToken)
        };

        // Get recent activities
        var recentCustomersList = await _context.Customers
            .AsNoTracking()
            .Where(c =>
                c.CreatedByDataEncoderId == currentUserId.Value &&
                c.RecordStatus == RecordStatus.Active)
            .OrderByDescending(c => c.CreateAt)
            .Take(5)
            .ToListAsync(cancellationToken);

        dashboard.RecentCustomers = recentCustomersList.Adapt<List<CustomerDetail>>();

        dashboard.RecentServices = await _context.Shipments
            .AsNoTracking()
            .Where(s =>
                s.CreatedByDataEncoderId == currentUserId.Value &&
                s.RecordStatus == RecordStatus.Active)
            .OrderByDescending(s => s.CreateAt)
            .Take(5)
            .Select(service => new ShipmentDetail
            {
                Id = service.Id,
                ServiceNumber = service.TrackingNumber,
                ItemDescription = service.Description,
                RouteCategory = service.RouteCategory,
                DeclaredValue = service.DeclaredValue,
                TaxCategory = service.TaxCategory,
                CountryOfOrigin = service.CountryOfOrigin,
                Origin = service.Origin,
                Destination = service.Destination,
                ServiceType = service.ServiceType ?? ServiceType.Multimodal,
                Status = service.Status,
                RiskLevel = service.RiskLevel,
                CustomerId = service.ImporterId,
                ImporterName = _context.Customers
                    .Where(customer =>
                        customer.UserId == service.ImporterId &&
                        customer.RecordStatus == RecordStatus.Active)
                    .Select(customer => customer.BusinessName)
                    .FirstOrDefault() ?? string.Empty,
                ImporterTinNumber = _context.Customers
                    .Where(customer =>
                        customer.UserId == service.ImporterId &&
                        customer.RecordStatus == RecordStatus.Active)
                    .Select(customer => customer.TINNumber)
                    .FirstOrDefault() ?? string.Empty,
                AssignedCaseExecutorId = service.AssignedCaseExecutorId,
                AssignedAssessorId = service.AssignedAssessorId,
                RegisteredDate = service.CreateAt,
                DueAt = service.Status == ShipmentStatus.Submitted || service.Status == ShipmentStatus.UnderReview
                    ? service.CreateAt.AddHours(48)
                    : null,
                LastUpdateDate = service.UpdatedAt
            })
            .ToListAsync(cancellationToken);

        return HandleSuccessResponse(dashboard);
    }

    private async Task<bool> IsDataEncoder(long userId, CancellationToken cancellationToken)
    {
        return await _context.UserRoles
            .AsNoTracking()
            .AnyAsync(userRole =>
                userRole.UserId == userId &&
                userRole.User.RecordStatus == RecordStatus.Active &&
                userRole.Role.RecordStatus == RecordStatus.Active &&
                userRole.Role.Name.ToLower() == "dataencoder",
                cancellationToken);
    }
}
