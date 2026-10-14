using Mapster;

using MediatR;

using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MohamedTransit.Domain.Common;
using MohamedTransit.API.DTO.MOT.Request;
using MohamedTransit.API.DTO.MOT.Response;
using MohamedTransit.API.Helpers;
using MohamedTransit.Application;
using MohamedTransit.Domain.Data;
using MohamedTransit.Domain.Entities;



namespace MohamedTransit.API.Controllers.MOT;
 
[ApiController]
[Route("api/v1/[controller]")]
public class ManagerController : BaseController
{
    private readonly ApplicationDbContext _context;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IMediator _mediator;

    public ManagerController(ApplicationDbContext context, IHttpContextAccessor httpContextAccessor, IMediator mediator)
    {
        _context = context;
        _httpContextAccessor = httpContextAccessor;
        _mediator = mediator;
    }

    /// <summary>
    /// Get dashboard analytics and key metrics
    /// </summary>
    [HttpGet("GetDashboard")]
    public async Task<IActionResult> GetDashboard()
    {
        var currentUserId = JwtHelper.GetCurrentUserId(_httpContextAccessor, _context);
        if (currentUserId == null)
            return Unauthorized("User not authenticated");

        // Verify user is manager
        if (!await IsManager(currentUserId.Value))
            return StatusCode(StatusCodes.Status403Forbidden, "Access denied. Manager role required.");

        var currentDate = DateTime.UtcNow;
        var currentMonthStart = new DateTime(currentDate.Year, currentDate.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var nextMonthStart = currentMonthStart.AddMonths(1);
        var openStatuses = new[]
        {
            ShipmentStatus.Submitted,
            ShipmentStatus.UnderReview,
            ShipmentStatus.Approved,
            ShipmentStatus.InProgress
        };
        var openCases = _context.Shipments.Where(s =>
            s.RecordStatus == RecordStatus.Active &&
            openStatuses.Contains(s.Status));
        var dueWindowStart = currentDate.AddHours(-48);

        var riskAttentionIds = openCases
            .Where(s => s.RiskLevel == RiskLevel.Red || s.RiskLevel == RiskLevel.Yellow)
            .Select(s => s.Id);
        var stageAttentionIds = _context.ServiceStageExecutions
            .Where(stage =>
                stage.RecordStatus == RecordStatus.Active &&
                stage.ShipmentId.HasValue &&
                stage.Shipment != null &&
                stage.Shipment.RecordStatus == RecordStatus.Active &&
                openStatuses.Contains(stage.Shipment.Status) &&
                (stage.IsBlocked ||
                 stage.RequiresCustomerAction ||
                 stage.Status == StageStatus.NeedsReview))
            .Select(stage => stage.ShipmentId!.Value)
            .Distinct();

        var dashboard = new MohamedTransit.API.DTO.MOT.Response.ManagerDashboardResponse
        {
            TotalServices = await _context.Shipments.CountAsync(),
            PendingServices = await _context.Shipments.CountAsync(s => s.Status == ShipmentStatus.Submitted),
            InProgressServices = await _context.Shipments.CountAsync(s => s.Status == ShipmentStatus.InProgress),
            CompletedServices = await _context.Shipments.CountAsync(s => s.Status == ShipmentStatus.Completed),
            OpenCases = await openCases.CountAsync(),
            NeedsAttention = await riskAttentionIds.Union(stageAttentionIds).CountAsync(),
            DueWithin48Hours = await openCases.CountAsync(s =>
                (s.Status == ShipmentStatus.Submitted || s.Status == ShipmentStatus.UnderReview) &&
                s.CreateAt >= dueWindowStart &&
                s.CreateAt <= currentDate),
            ClearedThisMonth = await _context.Shipments.CountAsync(s =>
                s.RecordStatus == RecordStatus.Active &&
                s.Status == ShipmentStatus.Completed &&
                s.UpdatedAt >= currentMonthStart &&
                s.UpdatedAt < nextMonthStart),
            TotalCustomers = await _context.Customers.CountAsync(),
            VerifiedCustomers = await _context.Customers.CountAsync(c => c.IsVerified),
            TotalStaff = await _context.Users.CountAsync(),
            ActiveStaff = await _context.Users.CountAsync(u => u.RecordStatus == RecordStatus.Active)
        };

        // Load complete service details for the priority queue and recent activity.
        dashboard.RecentServices = await _context.Shipments
            .OrderByDescending(s => s.CreateAt)
            .Take(10)
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
                    .Where(customer => customer.UserId == service.ImporterId)
                    .Select(customer => customer.BusinessName)
                    .FirstOrDefault() ?? service.Importer.Username,
                AssignedCaseExecutorId = service.AssignedCaseExecutorId,
                AssignedAssessorId = service.AssignedAssessorId,
                RegisteredDate = service.CreateAt,
                DueAt = service.Status == ShipmentStatus.Submitted || service.Status == ShipmentStatus.UnderReview
                    ? service.CreateAt.AddHours(48)
                    : null,
                LastUpdateDate = service.UpdatedAt
            })
            .ToListAsync();

        var recentServiceIds = dashboard.RecentServices.Select(service => service.Id).ToArray();
        var recentStageFlags = await _context.ServiceStageExecutions
            .Where(stage =>
                stage.RecordStatus == RecordStatus.Active &&
                stage.ShipmentId.HasValue &&
                recentServiceIds.Contains(stage.ShipmentId.Value))
            .GroupBy(stage => stage.ShipmentId!.Value)
            .Select(group => new
            {
                ShipmentId = group.Key,
                RequiresCustomerAction = group.Any(stage => stage.RequiresCustomerAction),
                IsBlocked = group.Any(stage => stage.IsBlocked || stage.Status == StageStatus.Blocked)
            })
            .ToDictionaryAsync(flags => flags.ShipmentId);

        foreach (var service in dashboard.RecentServices)
        {
            if (service.Status == ShipmentStatus.Submitted || service.Status == ShipmentStatus.UnderReview)
                service.DueAt = service.RegisteredDate.AddHours(48);

            if (recentStageFlags.TryGetValue(service.Id, out var flags))
            {
                service.RequiresCustomerAction = flags.RequiresCustomerAction;
                service.IsBlocked = flags.IsBlocked;
            }
        }

        // Get service completion rates by month
        var sixMonthsAgo = currentDate.AddMonths(-6);

        dashboard.MonthlyServiceStats = await _context.Shipments
            .Where(s => s.CreateAt >= sixMonthsAgo)
            .GroupBy(s => new { s.CreateAt.Year, s.CreateAt.Month })
            .Select(g => new MonthlyServiceStat
            {
                Year = g.Key.Year,
                Month = g.Key.Month,
                TotalServices = g.Count(),
                CompletedServices = g.Count(s => s.Status == ShipmentStatus.Completed),
                CompletionRate = g.Count(s => s.Status == ShipmentStatus.Completed) * 100.0 / g.Count()
            })
            .OrderBy(s => s.Year)
            .ThenBy(s => s.Month)
            .ToListAsync();

        return HandleSuccessResponse(dashboard);
    }

    /// <summary>
    /// Get live reporting metrics for the manager analytics page.
    /// </summary>
    [HttpGet("GetReportsAnalytics")]
    public async Task<IActionResult> GetReportsAnalytics(CancellationToken cancellationToken)
    {
        var currentUserId = JwtHelper.GetCurrentUserId(_httpContextAccessor, _context);
        if (currentUserId == null)
            return Unauthorized("User not authenticated");

        if (!await IsManager(currentUserId.Value))
            return StatusCode(StatusCodes.Status403Forbidden, "Access denied. Manager role required.");

        var now = DateTime.UtcNow;
        var yearStart = new DateTime(now.Year, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var recentStart = now.AddDays(-90);
        var firstReportMonth = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(-5);
        var openStatuses = new[]
        {
            ShipmentStatus.Submitted,
            ShipmentStatus.UnderReview,
            ShipmentStatus.Approved,
            ShipmentStatus.InProgress
        };

        var shipments = await _context.Shipments
            .Where(shipment => shipment.RecordStatus == RecordStatus.Active)
            .Select(shipment => new
            {
                shipment.Id,
                shipment.Status,
                shipment.ServiceType,
                shipment.Mode,
                shipment.RiskLevel,
                shipment.AssignedCaseExecutorId,
                shipment.CreateAt,
                shipment.CompletedAt,
                ExecutorFirstName = shipment.AssignedCaseExecutor == null
                    ? null
                    : shipment.AssignedCaseExecutor.FirstName,
                ExecutorLastName = shipment.AssignedCaseExecutor == null
                    ? null
                    : shipment.AssignedCaseExecutor.LastName,
                ExecutorUsername = shipment.AssignedCaseExecutor == null
                    ? null
                    : shipment.AssignedCaseExecutor.Username
            })
            .ToListAsync(cancellationToken);

        var monthlyThroughput = Enumerable.Range(0, 6)
            .Select(offset => firstReportMonth.AddMonths(offset))
            .Select(monthStart =>
            {
                var nextMonth = monthStart.AddMonths(1);
                var monthShipments = shipments.Where(shipment =>
                    shipment.CreateAt >= monthStart && shipment.CreateAt < nextMonth);
                var completedShipments = shipments.Count(shipment =>
                    shipment.CompletedAt >= monthStart &&
                    shipment.CompletedAt < nextMonth);

                return new ManagerMonthlyThroughput
                {
                    Year = monthStart.Year,
                    Month = monthStart.Month,
                    Multimodal = monthShipments.Count(shipment =>
                        shipment.ServiceType == ServiceType.Multimodal ||
                        (shipment.ServiceType == null && shipment.Mode == TransportMode.MultiModalSeaRail)),
                    Unimodal = monthShipments.Count(shipment =>
                        shipment.ServiceType == ServiceType.Unimodal ||
                        (shipment.ServiceType == null && shipment.Mode == TransportMode.UniModalRoad)),
                    Completed = completedShipments
                };
            })
            .ToList();

        var riskChannels = shipments
            .Where(shipment => shipment.RiskLevel.HasValue)
            .GroupBy(shipment => shipment.RiskLevel!.Value)
            .Select(group => new ManagerRiskChannelCount
            {
                Channel = group.Key.ToString(),
                Count = group.Count()
            })
            .OrderBy(item => item.Channel)
            .ToList();

        var completedStages = await _context.ServiceStageExecutions
            .Where(stage =>
                stage.RecordStatus == RecordStatus.Active &&
                stage.Shipment != null &&
                stage.Shipment.RecordStatus == RecordStatus.Active &&
                stage.StartedAt.HasValue &&
                stage.CompletedAt.HasValue &&
                stage.CompletedAt >= recentStart &&
                stage.CompletedAt <= now)
            .Select(stage => new
            {
                stage.Stage,
                stage.StartedAt,
                stage.CompletedAt
            })
            .ToListAsync(cancellationToken);

        var stageDurations = completedStages
            .Select(stage => new
            {
                stage.Stage,
                Hours = (stage.CompletedAt!.Value - stage.StartedAt!.Value).TotalHours
            })
            .Where(stage => stage.Hours >= 0)
            .GroupBy(stage => stage.Stage)
            .OrderBy(group => group.Key)
            .Select(group => new ManagerStageDuration
            {
                Stage = group.Key.ToString(),
                AverageHours = Math.Round(group.Average(stage => stage.Hours), 1)
            })
            .ToList();

        var executorPerformance = shipments
            .Where(shipment =>
                shipment.AssignedCaseExecutorId.HasValue &&
                (openStatuses.Contains(shipment.Status) ||
                 (shipment.Status == ShipmentStatus.Completed &&
                  shipment.CompletedAt >= recentStart &&
                  shipment.CompletedAt <= now)))
            .GroupBy(shipment => new
            {
                Id = shipment.AssignedCaseExecutorId!.Value,
                shipment.ExecutorFirstName,
                shipment.ExecutorLastName,
                shipment.ExecutorUsername
            })
            .Select(group =>
            {
                var recentCompleted = group
                    .Where(shipment =>
                        shipment.Status == ShipmentStatus.Completed &&
                        shipment.CompletedAt >= recentStart &&
                        shipment.CompletedAt <= now)
                    .Where(shipment => shipment.CompletedAt.HasValue)
                    .ToList();
                var cycleDays = recentCompleted
                    .Select(shipment => (shipment.CompletedAt!.Value - shipment.CreateAt).TotalDays)
                    .Where(days => days >= 0)
                    .ToList();

                return new ManagerExecutorPerformance
                {
                    ExecutorId = group.Key.Id,
                    ExecutorName = string.Join(" ", new[]
                        {
                            group.Key.ExecutorFirstName,
                            group.Key.ExecutorLastName
                        }
                        .Where(name => !string.IsNullOrWhiteSpace(name)))
                        .Trim() is { Length: > 0 } name
                        ? name
                        : group.Key.ExecutorUsername ?? "Unnamed executor",
                    CasesClosed = recentCompleted.Count,
                    AverageCycleDays = cycleDays.Count > 0
                        ? Math.Round(cycleDays.Average(), 1)
                        : null,
                    OpenLoad = group.Count(shipment => openStatuses.Contains(shipment.Status))
                };
            })
            .Where(item => item.CasesClosed > 0 || item.OpenLoad > 0)
            .OrderByDescending(item => item.CasesClosed + item.OpenLoad)
            .ToList();

        var validDurations = completedStages
            .Select(stage => (stage.CompletedAt!.Value - stage.StartedAt!.Value).TotalHours)
            .Where(hours => hours >= 0)
            .ToList();

        var report = new ManagerReportsResponse
        {
            OpenCases = shipments.Count(shipment => openStatuses.Contains(shipment.Status)),
            CompletedYearToDate = shipments.Count(shipment =>
                shipment.Status == ShipmentStatus.Completed &&
                shipment.CompletedAt >= yearStart &&
                shipment.CompletedAt <= now),
            OnTimeClearanceRate = null,
            AverageStageDurationHours = validDurations.Count > 0
                ? Math.Round(validDurations.Average(), 1)
                : null,
            MonthlyThroughput = monthlyThroughput,
            RiskChannels = riskChannels,
            StageDurations = stageDurations,
            ExecutorPerformance = executorPerformance
        };

        return HandleSuccessResponse(report);
    }

    /// <summary>
    /// Get all services with filtering options
    /// </summary>
    [HttpGet("GetAllServices")]
    public async Task<IActionResult> GetAllServices(
        [FromQuery] ShipmentStatus? status = null,
        [FromQuery] ServiceType? type = null,
        [FromQuery] long? customerId = null,
        [FromQuery] long? caseExecutorId = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        var currentUserId = JwtHelper.GetCurrentUserId(_httpContextAccessor, _context);
        if (currentUserId == null)
            return Unauthorized("User not authenticated");

        if (!await IsManager(currentUserId.Value))
            return StatusCode(StatusCodes.Status403Forbidden, "Access denied. Manager role required.");
        if (page < 1 || pageSize < 1 || pageSize > 100)
            return BadRequest("Page must be positive and pageSize must be between 1 and 100.");

        var query = _context.Shipments
            .AsNoTracking()
            .Where(s => s.RecordStatus == RecordStatus.Active)
            .AsQueryable();

        if (status.HasValue)
            query = query.Where(s => s.Status == status.Value);

        if (type.HasValue)
            query = query.Where(s => s.ServiceType == type.Value);

        if (customerId.HasValue)
            query = query.Where(s => s.ImporterId == customerId.Value);

        if (caseExecutorId.HasValue)
            query = query.Where(s => s.AssignedCaseExecutorId == caseExecutorId.Value);

        var totalCount = await query.CountAsync(cancellationToken);
        var services = await query
            .OrderByDescending(s => s.CreateAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(service => new
            {
                service.Id,
                service.TrackingNumber,
                service.Description,
                service.Mode,
                service.ServiceType,
                service.Status,
                service.RouteCategory,
                service.DeclaredValue,
                service.TaxCategory,
                service.CountryOfOrigin,
                service.RiskLevel,
                service.Origin,
                service.Destination,
                service.ImporterId,
                Importer = new
                {
                    service.Importer.Id,
                    service.Importer.FirstName,
                    service.Importer.LastName,
                    service.Importer.Username,
                    service.Importer.Email,
                    service.Importer.Phone
                },
                Customer = _context.Customers
                    .Where(customer =>
                        customer.UserId == service.ImporterId &&
                        customer.RecordStatus == RecordStatus.Active)
                    .OrderByDescending(customer => customer.CreateAt)
                    .Select(customer => new
                    {
                        customer.Id,
                        customer.BusinessName,
                        customer.TINNumber
                    })
                    .FirstOrDefault(),
                AssignedCaseExecutor = service.AssignedCaseExecutor == null
                    ? null
                    : new
                    {
                        service.AssignedCaseExecutor.Id,
                        service.AssignedCaseExecutor.FirstName,
                        service.AssignedCaseExecutor.LastName,
                        service.AssignedCaseExecutor.Username
                    },
                AssignedAssessor = service.AssignedAssessor == null
                    ? null
                    : new
                    {
                        service.AssignedAssessor.Id,
                        service.AssignedAssessor.FirstName,
                        service.AssignedAssessor.LastName,
                        service.AssignedAssessor.Username
                    },
                service.AssignedCaseExecutorId,
                service.AssignedAssessorId,
                service.AssignmentNotes,
                service.CreateAt,
                service.UpdatedAt,
                RequiresCustomerAction = _context.ServiceStageExecutions.Any(stage =>
                    stage.ShipmentId == service.Id &&
                    stage.RecordStatus == RecordStatus.Active &&
                    stage.RequiresCustomerAction),
                IsBlocked = _context.ServiceStageExecutions.Any(stage =>
                    stage.ShipmentId == service.Id &&
                    stage.RecordStatus == RecordStatus.Active &&
                    (stage.IsBlocked || stage.Status == StageStatus.Blocked))
            })
            .ToListAsync(cancellationToken);

        var result = new
        {
            Data = services,
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize,
            TotalPages = (int)Math.Ceiling((double)totalCount / pageSize)
        };

        return HandleSuccessResponse(result);
    }

    /// <summary>
    /// Get service details for management oversight
    /// </summary>
    [HttpGet("GetServiceById")]
    public async Task<IActionResult> GetServiceById(
        [FromQuery] long serviceId,
        CancellationToken cancellationToken)
    {
        var currentUserId = JwtHelper.GetCurrentUserId(_httpContextAccessor, _context);
        if (currentUserId == null)
            return Unauthorized("User not authenticated");

        if (!await IsManager(currentUserId.Value))
            return StatusCode(StatusCodes.Status403Forbidden, "Access denied. Manager role required.");
        if (serviceId <= 0)
            return BadRequest("A valid service ID is required.");

        var service = await _context.Shipments
            .AsNoTracking()
            .AsSplitQuery()
            .Include(s => s.Importer)
            .Include(s => s.AssignedCaseExecutor)
            .Include(s => s.AssignedAssessor)
            .Include(s => s.Stages)
                .ThenInclude(stage => stage.StageComments)
                    .ThenInclude(comment => comment.CommentedByUser)
            .Include(s => s.Stages)
                .ThenInclude(stage => stage.Documents)
                    .ThenInclude(document => document.UploadedByUser)
            .Include(s => s.Documents)
                .ThenInclude(document => document.UploadedByUser)
            .Include(s => s.Messages)
                .ThenInclude(message => message.SenderUser)
            .Include(s => s.Messages)
                .ThenInclude(message => message.RecipientUser)
            .Where(s => s.RecordStatus == RecordStatus.Active)
            .FirstOrDefaultAsync(s => s.Id == serviceId, cancellationToken);

        if (service == null)
            return NotFound("Service not found");

        var customer = await _context.Customers
            .AsNoTracking()
            .Where(item =>
                item.UserId == service.ImporterId &&
                item.RecordStatus == RecordStatus.Active)
            .OrderByDescending(item => item.CreateAt)
            .Select(item => new
            {
                item.Id,
                item.BusinessName,
                item.TINNumber,
                item.BusinessAddress,
                item.ContactPerson,
                item.ContactPhone,
                item.ContactEmail
            })
            .FirstOrDefaultAsync(cancellationToken);
        var assessments = await _context.ServiceAssessments
            .AsNoTracking()
            .Where(assessment =>
                assessment.ServiceId == service.Id &&
                assessment.RecordStatus == RecordStatus.Active)
            .OrderByDescending(assessment => assessment.CreateAt)
            .Select(assessment => new
            {
                assessment.Id,
                assessment.Decision,
                assessment.TaxAmount,
                assessment.DutyAmount,
                assessment.Notes,
                assessment.CreateAt,
                Assessor = assessment.Assessor == null
                    ? null
                    : assessment.Assessor.FirstName + " " + assessment.Assessor.LastName
            })
            .ToListAsync(cancellationToken);

        return HandleSuccessResponse(new
        {
            service.Id,
            service.TrackingNumber,
            service.Description,
            service.Mode,
            service.ServiceType,
            service.Status,
            service.RouteCategory,
            service.DeclaredValue,
            service.TaxCategory,
            service.CountryOfOrigin,
            service.RiskLevel,
            service.Origin,
            service.Destination,
            service.AssignedHub,
            service.AssignmentNotes,
            service.CreateAt,
            service.UpdatedAt,
            Importer = new
            {
                service.Importer.Id,
                service.Importer.FirstName,
                service.Importer.LastName,
                service.Importer.Username,
                service.Importer.Email,
                service.Importer.Phone
            },
            Customer = customer,
            AssignedCaseExecutor = service.AssignedCaseExecutor == null
                ? null
                : new
                {
                    service.AssignedCaseExecutor.Id,
                    service.AssignedCaseExecutor.FirstName,
                    service.AssignedCaseExecutor.LastName,
                    service.AssignedCaseExecutor.Username,
                    service.AssignedCaseExecutor.Email,
                    service.AssignedCaseExecutor.Phone
                },
            AssignedAssessor = service.AssignedAssessor == null
                ? null
                : new
                {
                    service.AssignedAssessor.Id,
                    service.AssignedAssessor.FirstName,
                    service.AssignedAssessor.LastName,
                    service.AssignedAssessor.Username,
                    service.AssignedAssessor.Email,
                    service.AssignedAssessor.Phone
                },
            Stages = service.Stages
                .Where(stage => stage.RecordStatus == RecordStatus.Active)
                .OrderBy(stage => stage.Stage)
                .Select(stage => new
                {
                    stage.Id,
                    stage.Stage,
                    stage.Status,
                    stage.Comments,
                    stage.RequiresCustomerAction,
                    stage.IsBlocked,
                    stage.BlockReason,
                    stage.StartedAt,
                    stage.CompletedAt,
                    Documents = stage.Documents
                        .Where(document => document.RecordStatus == RecordStatus.Active)
                        .Select(document => new
                        {
                            document.Id,
                            document.OriginalFileName,
                            document.DocumentType,
                            document.Description,
                            document.IsRequired,
                            document.IsVerified,
                            document.CreateAt,
                            UploadedBy = document.UploadedByUser == null
                                ? null
                                : document.UploadedByUser.FirstName + " " + document.UploadedByUser.LastName
                        })
                        .ToList(),
                    Discussions = stage.StageComments
                        .Where(comment => comment.RecordStatus == RecordStatus.Active)
                        .OrderBy(comment => comment.CreateAt)
                        .Select(comment => new
                        {
                            comment.Id,
                            comment.Comment,
                            comment.CreateAt,
                            Author = comment.CommentedByUser == null
                                ? null
                                : comment.CommentedByUser.FirstName + " " + comment.CommentedByUser.LastName
                        })
                        .ToList()
                })
                .ToList(),
            Documents = service.Documents
                .Where(document => document.RecordStatus == RecordStatus.Active)
                .OrderByDescending(document => document.CreateAt)
                .Select(document => new
                {
                    document.Id,
                    document.OriginalFileName,
                    document.DocumentType,
                    document.Description,
                    document.IsRequired,
                    document.IsVerified,
                    document.CreateAt,
                    document.ShipmentStageId,
                    UploadedBy = document.UploadedByUser == null
                        ? null
                        : document.UploadedByUser.FirstName + " " + document.UploadedByUser.LastName
                })
                .ToList(),
            Messages = service.Messages
                .Where(message => message.RecordStatus == RecordStatus.Active)
                .OrderBy(message => message.CreatedDate)
                .Select(message => new
                {
                    message.Id,
                    message.Subject,
                    message.Content,
                    message.MessageType,
                    message.IsUrgent,
                    message.IsRead,
                    message.CreatedDate,
                    message.ServiceStageId,
                    Sender = message.SenderUser == null
                        ? null
                        : message.SenderUser.FirstName + " " + message.SenderUser.LastName,
                    Recipient = message.RecipientUser == null
                        ? null
                        : message.RecipientUser.FirstName + " " + message.RecipientUser.LastName
                })
                .ToList(),
            Assessments = assessments
        });
    }

    /// <summary>
    /// Assign case executor to a service
    /// </summary>
    [HttpPut("AssignExecutor")]
    public async Task<IActionResult> AssignExecutor(
        [FromBody] AssignExecutorRequest request,
        CancellationToken cancellationToken)
    {
        var currentUserId = JwtHelper.GetCurrentUserId(_httpContextAccessor, _context);
        if (currentUserId == null)
            return Unauthorized("User not authenticated");

        if (!await IsManager(currentUserId.Value))
            return StatusCode(StatusCodes.Status403Forbidden, "Access denied. Manager role required.");
        if (request.ShipmentId <= 0 || request.CaseExecutorId <= 0)
            return BadRequest("A valid service ID and case executor ID are required.");

        var isActiveCaseExecutor = await _context.UserRoles
            .AsNoTracking()
            .AnyAsync(userRole =>
                userRole.UserId == request.CaseExecutorId &&
                userRole.User.RecordStatus == RecordStatus.Active &&
                userRole.Role.RecordStatus == RecordStatus.Active &&
                userRole.Role.Name.ToLower() == "caseexecutor",
                cancellationToken);
        if (!isActiveCaseExecutor)
            return BadRequest("The selected user is not an active case executor.");

        var command = new AssignShipmentCommand
        {
            ShipmentId = request.ShipmentId,
            AssignedCaseExecutorId = request.CaseExecutorId,
            AssignedAssessorId = null,
            AssignmentNotes = null,
            AssignedByUserId = currentUserId.Value
        };

        var result = await _mediator.Send(command, cancellationToken);

        return result.IsError
            ? HandleErrorResponse(result.Errors)
            : HandleSuccessResponse(new
            {
                ShipmentId = request.ShipmentId,
                CaseExecutorId = request.CaseExecutorId,
                Message = result.Message
            });
    }

    /// <summary>
    /// Get all customers with filtering
    /// </summary>
    [HttpGet("GetAllCustomers")]
    public async Task<IActionResult> GetAllCustomers(
        [FromQuery] bool? isVerified = null,
        [FromQuery] string? searchTerm = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        var currentUserId = JwtHelper.GetCurrentUserId(_httpContextAccessor, _context);
        if (currentUserId == null)
            return Unauthorized("User not authenticated");

        if (!await IsManager(currentUserId.Value))
            return StatusCode(StatusCodes.Status403Forbidden, "Access denied. Manager role required.");
        if (page < 1 || pageSize < 1 || pageSize > 100)
            return BadRequest("Page must be positive and pageSize must be between 1 and 100.");

        var query = _context.Customers
            .AsNoTracking()
            .Where(c => c.RecordStatus == RecordStatus.Active)
            .AsQueryable();

        if (isVerified.HasValue)
            query = query.Where(c => c.IsVerified == isVerified.Value);

        if (!string.IsNullOrWhiteSpace(searchTerm))
        {
            searchTerm = searchTerm.Trim();
            query = query.Where(c =>
                c.BusinessName.Contains(searchTerm) ||
                c.TINNumber.Contains(searchTerm) ||
                c.ContactEmail.Contains(searchTerm));
        }

        var totalCount = await query.CountAsync(cancellationToken);
        var customers = await query
            .OrderByDescending(c => c.CreateAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(customer => new
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
                customer.IsVerified,
                customer.IsReturnedForRevision,
                customer.VerifiedAt,
                customer.VerifiedByUserId,
                customer.VerificationNotes,
                customer.UserId,
                customer.CreateAt,
                customer.UpdatedAt,
                Importer = customer.User == null
                    ? null
                    : new
                    {
                        customer.User.Id,
                        customer.User.FirstName,
                        customer.User.LastName,
                        customer.User.Username,
                        customer.User.Email,
                        customer.User.Phone
                    },
                VerifiedBy = customer.VerifiedByUser == null
                    ? null
                    : new
                    {
                        customer.VerifiedByUser.Id,
                        customer.VerifiedByUser.FirstName,
                        customer.VerifiedByUser.LastName,
                        customer.VerifiedByUser.Username
                    },
                Documents = customer.Documents
                    .Where(document => document.RecordStatus == RecordStatus.Active)
                    .Select(document => new
                    {
                        document.Id,
                        document.OriginalFileName,
                        document.DocumentType,
                        document.Description,
                        document.IsRequired,
                        document.IsVerified,
                        document.ExpiryDate,
                        document.VerificationNotes,
                        document.CreateAt
                    })
                    .ToList()
            })
            .ToListAsync(cancellationToken);

        var result = new
        {
            Data = customers,
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize,
            TotalPages = (int)Math.Ceiling((double)totalCount / pageSize)
        };

        return HandleSuccessResponse(result);
    }

    /// <summary>
    /// Get all staff members
    /// </summary>
    [HttpGet("GetAllStaff")]
    public async Task<IActionResult> GetAllStaff(
        [FromQuery] string? role = null,
        CancellationToken cancellationToken = default)
    {
        var currentUserId = JwtHelper.GetCurrentUserId(_httpContextAccessor, _context);
        if (currentUserId == null)
            return Unauthorized("User not authenticated");

        if (!await IsManager(currentUserId.Value))
            return StatusCode(StatusCodes.Status403Forbidden, "Access denied. Manager role required.");

        var query = _context.Users
            .AsNoTracking()
            .Where(u => u.RecordStatus == RecordStatus.Active)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(role))
        {
            var normalizedRole = role.Trim().ToLower();
            query = query.Where(u => u.UserRoles.Any(userRole =>
                userRole.Role.RecordStatus == RecordStatus.Active &&
                userRole.Role.Name.ToLower() == normalizedRole));
        }

        var staff = await query
            .OrderBy(user => user.FirstName)
            .ThenBy(user => user.LastName)
            .Select(user => new
            {
                user.Id,
                user.Username,
                user.Email,
                user.FirstName,
                user.LastName,
                user.ProfilePhoto,
                user.Phone,
                user.IsSuperAdmin,
                user.IsAccountLocked,
                user.AccountStatus,
                Roles = user.UserRoles
                    .Where(userRole => userRole.Role.RecordStatus == RecordStatus.Active)
                    .Select(userRole => userRole.Role.Name)
                    .ToList()
            })
            .ToListAsync(cancellationToken);

        return HandleSuccessResponse(staff);
    }

    /// <summary>
    /// Get system notifications for managers
    /// </summary>
    [HttpGet("GetNotifications")]
    public async Task<IActionResult> GetNotifications(
        [FromQuery] bool unreadOnly = false,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        var currentUserId = JwtHelper.GetCurrentUserId(_httpContextAccessor, _context);
        if (currentUserId == null)
            return Unauthorized("User not authenticated");

        if (!await IsManager(currentUserId.Value))
            return StatusCode(StatusCodes.Status403Forbidden, "Access denied. Manager role required.");
        if (page < 1 || pageSize < 1 || pageSize > 100)
            return BadRequest("Page must be positive and pageSize must be between 1 and 100.");

        var query = _context.Notifications
            .AsNoTracking()
            .Where(n =>
                n.RecordStatus == RecordStatus.Active &&
                n.UserId == currentUserId.Value &&
                (n.Type == NotificationType.SystemAlert || n.IsUrgent));

        if (unreadOnly)
            query = query.Where(n => !n.IsRead);

        var totalCount = await query.CountAsync(cancellationToken);
        var notifications = await query
            .OrderByDescending(n => n.CreateAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(notification => new
            {
                notification.Id,
                notification.Title,
                notification.Message,
                notification.Type,
                notification.IsRead,
                notification.ReadAt,
                notification.IsSent,
                notification.SentAt,
                notification.ActionUrl,
                notification.ActionText,
                notification.IsUrgent,
                notification.UserId,
                notification.ServiceId,
                notification.ServiceStageId,
                notification.CreateAt,
                ServiceNumber = notification.Shipment == null
                    ? null
                    : notification.Shipment.TrackingNumber
            })
            .ToListAsync(cancellationToken);

        return HandleSuccessResponse(new
        {
            Data = notifications,
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize,
            TotalPages = (int)Math.Ceiling((double)totalCount / pageSize)
        });
    }

    /// <summary>
    /// Escalate a service and notify the staff responsible for it.
    /// </summary>
    [HttpPost("EscalateService")]
    public async Task<IActionResult> EscalateService(
        [FromBody] ManagerEscalateServiceRequest request,
        CancellationToken cancellationToken)
    {
        var currentUserId = JwtHelper.GetCurrentUserId(_httpContextAccessor, _context);
        if (currentUserId == null)
            return Unauthorized("User not authenticated");
        if (!await IsManager(currentUserId.Value))
            return StatusCode(StatusCodes.Status403Forbidden, "Access denied. Manager role required.");
        if (request.ServiceId <= 0 || string.IsNullOrWhiteSpace(request.Reason))
            return BadRequest("A valid service ID and escalation reason are required.");

        var service = await _context.Shipments
            .FirstOrDefaultAsync(shipment =>
                shipment.Id == request.ServiceId &&
                shipment.RecordStatus == RecordStatus.Active,
                cancellationToken);
        if (service == null)
            return NotFound("Service not found.");

        var reason = request.Reason.Trim();
        var message = string.IsNullOrWhiteSpace(request.Notes)
            ? reason
            : $"{reason}{Environment.NewLine}{request.Notes.Trim()}";
        var escalation = ServiceMessage.Create(
            "Service escalation",
            message,
            MessageType.System,
            service.Id,
            currentUserId.Value,
            isUrgent: true,
            priority: "High");
        _context.ServiceMessages.Add(escalation);

        var recipientIds = new HashSet<long>();
        if (service.AssignedCaseExecutorId.HasValue)
            recipientIds.Add(service.AssignedCaseExecutorId.Value);
        if (service.AssignedAssessorId.HasValue)
            recipientIds.Add(service.AssignedAssessorId.Value);

        var managerIds = await _context.UserRoles
            .AsNoTracking()
            .Where(userRole =>
                userRole.UserId != currentUserId.Value &&
                userRole.User.RecordStatus == RecordStatus.Active &&
                userRole.Role.RecordStatus == RecordStatus.Active &&
                (userRole.Role.Name.ToLower() == "superadmin" ||
                 userRole.Role.Name.ToLower() == "admin"))
            .Select(userRole => userRole.UserId)
            .Distinct()
            .ToListAsync(cancellationToken);
        recipientIds.UnionWith(managerIds);

        foreach (var recipientId in recipientIds)
        {
            _context.Notifications.Add(Notification.Create(
                "Service escalation",
                message,
                NotificationType.SystemAlert,
                recipientId,
                service.Id,
                isUrgent: true));
        }

        await _context.SaveChangesAsync(cancellationToken);
        return HandleSuccessResponse(new
        {
            escalation.Id,
            ServiceId = service.Id,
            service.TrackingNumber,
            escalation.Subject,
            escalation.Content,
            escalation.CreatedDate,
            NotifiedUsers = recipientIds.Count
        });
    }

    /// <summary>
    /// Send an importer a service message and an in-app notification.
    /// </summary>
    [HttpPost("NotifyImporter")]
    public async Task<IActionResult> NotifyImporter(
        [FromBody] ManagerNotifyImporterRequest request,
        CancellationToken cancellationToken)
    {
        var currentUserId = JwtHelper.GetCurrentUserId(_httpContextAccessor, _context);
        if (currentUserId == null)
            return Unauthorized("User not authenticated");
        if (!await IsManager(currentUserId.Value))
            return StatusCode(StatusCodes.Status403Forbidden, "Access denied. Manager role required.");
        if (request.ServiceId <= 0 ||
            string.IsNullOrWhiteSpace(request.Subject) ||
            string.IsNullOrWhiteSpace(request.Message))
            return BadRequest("A valid service ID, subject, and message are required.");

        var service = await _context.Shipments
            .FirstOrDefaultAsync(shipment =>
                shipment.Id == request.ServiceId &&
                shipment.RecordStatus == RecordStatus.Active,
                cancellationToken);
        if (service == null)
            return NotFound("Service not found.");

        var subject = request.Subject.Trim();
        var message = request.Message.Trim();
        var serviceMessage = ServiceMessage.Create(
            subject,
            message,
            MessageType.Direct,
            service.Id,
            currentUserId.Value,
            service.ImporterId,
            isUrgent: request.IsUrgent);
        _context.ServiceMessages.Add(serviceMessage);
        _context.Notifications.Add(Notification.Create(
            subject,
            message,
            NotificationType.Message,
            service.ImporterId,
            service.Id,
            isUrgent: request.IsUrgent));

        await _context.SaveChangesAsync(cancellationToken);
        return HandleSuccessResponse(new
        {
            serviceMessage.Id,
            serviceMessage.ServiceId,
            serviceMessage.Subject,
            serviceMessage.Content,
            serviceMessage.CreatedDate,
            RecipientUserId = service.ImporterId
        });
    }

    /// <summary>
    /// Get documents uploaded to a service stage.
    /// </summary>
    [HttpGet("GetStageDocuments")]
    public async Task<IActionResult> GetStageDocuments(
        [FromQuery] long serviceId,
        [FromQuery] long stageId,
        CancellationToken cancellationToken)
    {
        var currentUserId = JwtHelper.GetCurrentUserId(_httpContextAccessor, _context);
        if (currentUserId == null)
            return Unauthorized("User not authenticated");
        if (!await IsManager(currentUserId.Value))
            return StatusCode(StatusCodes.Status403Forbidden, "Access denied. Manager role required.");
        if (serviceId <= 0 || stageId <= 0)
            return BadRequest("Valid service and stage IDs are required.");

        var stageExists = await _context.ServiceStageExecutions.AnyAsync(stage =>
            stage.Id == stageId &&
            stage.ShipmentId == serviceId &&
            stage.RecordStatus == RecordStatus.Active &&
            stage.Shipment != null &&
            stage.Shipment.RecordStatus == RecordStatus.Active,
            cancellationToken);
        if (!stageExists)
            return NotFound("Service stage not found.");

        var documents = await _context.StageDocuments
            .AsNoTracking()
            .Where(document =>
                document.ServiceStageId == stageId &&
                document.RecordStatus == RecordStatus.Active)
            .OrderByDescending(document => document.CreateAt)
            .Select(document => new
            {
                document.Id,
                document.OriginalFileName,
                document.DocumentType,
                document.Description,
                document.FileSizeBytes,
                document.MimeType,
                document.IsRequired,
                document.IsVerified,
                document.VerificationNotes,
                document.CreateAt,
                UploadedBy = document.UploadedByUser == null
                    ? null
                    : new
                    {
                        document.UploadedByUser.Id,
                        document.UploadedByUser.FirstName,
                        document.UploadedByUser.LastName,
                        document.UploadedByUser.Username
                    }
            })
            .ToListAsync(cancellationToken);

        return HandleSuccessResponse(documents);
    }

    /// <summary>
    /// Get discussion comments for a service stage.
    /// </summary>
    [HttpGet("GetStageDiscussions")]
    public async Task<IActionResult> GetStageDiscussions(
        [FromQuery] long serviceId,
        [FromQuery] long stageId,
        CancellationToken cancellationToken)
    {
        var currentUserId = JwtHelper.GetCurrentUserId(_httpContextAccessor, _context);
        if (currentUserId == null)
            return Unauthorized("User not authenticated");
        if (!await IsManager(currentUserId.Value))
            return StatusCode(StatusCodes.Status403Forbidden, "Access denied. Manager role required.");
        if (serviceId <= 0 || stageId <= 0)
            return BadRequest("Valid service and stage IDs are required.");

        var stageExists = await _context.ServiceStageExecutions.AnyAsync(stage =>
            stage.Id == stageId &&
            stage.ShipmentId == serviceId &&
            stage.RecordStatus == RecordStatus.Active &&
            stage.Shipment != null &&
            stage.Shipment.RecordStatus == RecordStatus.Active,
            cancellationToken);
        if (!stageExists)
            return NotFound("Service stage not found.");

        var discussions = await _context.StageComments
            .AsNoTracking()
            .Where(comment =>
                comment.ServiceStageId == stageId &&
                comment.RecordStatus == RecordStatus.Active)
            .OrderBy(comment => comment.CreateAt)
            .Select(comment => new
            {
                comment.Id,
                comment.Comment,
                comment.CreateAt,
                Author = comment.CommentedByUser == null
                    ? null
                    : new
                    {
                        comment.CommentedByUser.Id,
                        comment.CommentedByUser.FirstName,
                        comment.CommentedByUser.LastName,
                        comment.CommentedByUser.Username
                    }
            })
            .ToListAsync(cancellationToken);

        return HandleSuccessResponse(discussions);
    }

    /// <summary>
    /// Add a discussion comment to a service stage.
    /// </summary>
    [HttpPost("AddStageDiscussion")]
    public async Task<IActionResult> AddStageDiscussion(
        [FromBody] AddManagerStageDiscussionRequest request,
        CancellationToken cancellationToken)
    {
        var currentUserId = JwtHelper.GetCurrentUserId(_httpContextAccessor, _context);
        if (currentUserId == null)
            return Unauthorized("User not authenticated");
        if (!await IsManager(currentUserId.Value))
            return StatusCode(StatusCodes.Status403Forbidden, "Access denied. Manager role required.");
        if (request.ServiceId <= 0 ||
            request.StageId <= 0 ||
            string.IsNullOrWhiteSpace(request.Comment))
            return BadRequest("Valid service and stage IDs and a comment are required.");

        var stageExists = await _context.ServiceStageExecutions.AnyAsync(stage =>
            stage.Id == request.StageId &&
            stage.ShipmentId == request.ServiceId &&
            stage.RecordStatus == RecordStatus.Active &&
            stage.Shipment != null &&
            stage.Shipment.RecordStatus == RecordStatus.Active,
            cancellationToken);
        if (!stageExists)
            return NotFound("Service stage not found.");

        var comment = StageComment.Create(
            request.Comment.Trim(),
            request.StageId,
            currentUserId.Value);
        _context.StageComments.Add(comment);
        await _context.SaveChangesAsync(cancellationToken);

        return HandleSuccessResponse(new
        {
            comment.Id,
            comment.ServiceStageId,
            comment.Comment,
            comment.CreateAt
        });
    }

    /// <summary>
    /// Get service cargo, transport, and uploaded document details.
    /// </summary>
    [HttpGet("GetConsignmentDetails")]
    public async Task<IActionResult> GetConsignmentDetails(
        [FromQuery] long serviceId,
        CancellationToken cancellationToken)
    {
        var currentUserId = JwtHelper.GetCurrentUserId(_httpContextAccessor, _context);
        if (currentUserId == null)
            return Unauthorized("User not authenticated");
        if (!await IsManager(currentUserId.Value))
            return StatusCode(StatusCodes.Status403Forbidden, "Access denied. Manager role required.");
        if (serviceId <= 0)
            return BadRequest("A valid service ID is required.");

        var service = await _context.Shipments
            .AsNoTracking()
            .Where(shipment =>
                shipment.Id == serviceId &&
                shipment.RecordStatus == RecordStatus.Active)
            .Select(shipment => new
            {
                shipment.Id,
                shipment.TrackingNumber,
                shipment.Description,
                shipment.Mode,
                shipment.ServiceType,
                shipment.RouteCategory,
                shipment.DeclaredValue,
                shipment.TaxCategory,
                shipment.CountryOfOrigin,
                shipment.Origin,
                shipment.Destination,
                Transports = _context.Transports
                    .Where(transport =>
                        transport.RecordStatus == RecordStatus.Active &&
                        transport.ServiceStageId.HasValue &&
                        _context.ServiceStageExecutions.Any(stage =>
                            stage.Id == transport.ServiceStageId.Value &&
                            stage.ShipmentId == shipment.Id &&
                            stage.RecordStatus == RecordStatus.Active))
                    .OrderBy(transport => transport.Id)
                    .Select(transport => new
                    {
                        transport.Id,
                        transport.FullName,
                        transport.PlateNumber,
                        transport.PhoneNumber,
                        transport.ProductAmount,
                        transport.ServiceStageId
                    })
                    .ToList(),
                Documents = shipment.Documents
                    .Where(document => document.RecordStatus == RecordStatus.Active)
                    .OrderByDescending(document => document.CreateAt)
                    .Select(document => new
                    {
                        document.Id,
                        document.OriginalFileName,
                        document.DocumentType,
                        document.Description,
                        document.FileSizeBytes,
                        document.CreateAt,
                        document.ShipmentStageId
                    })
                    .ToList()
            })
            .FirstOrDefaultAsync(cancellationToken);

        return service == null
            ? NotFound("Service not found.")
            : HandleSuccessResponse(service);
    }

    /// <summary>
    /// Get recent stage, discussion, messaging, and assessment activity for a service.
    /// </summary>
    [HttpGet("GetServiceActivities")]
    public async Task<IActionResult> GetServiceActivities(
        [FromQuery] long serviceId,
        CancellationToken cancellationToken)
    {
        var currentUserId = JwtHelper.GetCurrentUserId(_httpContextAccessor, _context);
        if (currentUserId == null)
            return Unauthorized("User not authenticated");
        if (!await IsManager(currentUserId.Value))
            return StatusCode(StatusCodes.Status403Forbidden, "Access denied. Manager role required.");
        if (serviceId <= 0)
            return BadRequest("A valid service ID is required.");
        if (!await _context.Shipments.AnyAsync(shipment =>
                shipment.Id == serviceId &&
                shipment.RecordStatus == RecordStatus.Active,
                cancellationToken))
            return NotFound("Service not found.");

        var stages = await _context.ServiceStageExecutions
            .AsNoTracking()
            .Where(stage =>
                stage.ShipmentId == serviceId &&
                stage.RecordStatus == RecordStatus.Active)
            .Select(stage => new
            {
                stage.Stage,
                stage.Status,
                stage.CreateAt,
                stage.UpdatedAt,
                stage.StartedAt,
                stage.CompletedAt,
                stage.RequiresCustomerAction,
                stage.IsBlocked
            })
            .ToListAsync(cancellationToken);
        var discussions = await _context.StageComments
            .AsNoTracking()
            .Where(comment =>
                comment.RecordStatus == RecordStatus.Active &&
                comment.ServiceStage != null &&
                comment.ServiceStage.ShipmentId == serviceId &&
                comment.ServiceStage.RecordStatus == RecordStatus.Active)
            .Select(comment => new
            {
                comment.Comment,
                comment.CreateAt,
                Stage = comment.ServiceStage!.Stage,
                Author = comment.CommentedByUser == null
                    ? null
                    : comment.CommentedByUser.FirstName + " " + comment.CommentedByUser.LastName
            })
            .ToListAsync(cancellationToken);
        var messages = await _context.ServiceMessages
            .AsNoTracking()
            .Where(message =>
                message.ServiceId == serviceId &&
                message.RecordStatus == RecordStatus.Active)
            .Select(message => new
            {
                message.Subject,
                message.Content,
                message.MessageType,
                message.IsUrgent,
                message.CreatedDate,
                Author = message.SenderUser == null
                    ? null
                    : message.SenderUser.FirstName + " " + message.SenderUser.LastName
            })
            .ToListAsync(cancellationToken);
        var assessments = await _context.ServiceAssessments
            .AsNoTracking()
            .Where(assessment =>
                assessment.ServiceId == serviceId &&
                assessment.RecordStatus == RecordStatus.Active)
            .Select(assessment => new
            {
                assessment.Decision,
                assessment.TaxAmount,
                assessment.DutyAmount,
                assessment.CreateAt,
                Author = assessment.Assessor == null
                    ? null
                    : assessment.Assessor.FirstName + " " + assessment.Assessor.LastName
            })
            .ToListAsync(cancellationToken);

        var activities = stages
            .Select(stage => new ServiceActivityItem(
                "Stage",
                stage.Stage.ToString(),
                $"Stage status: {stage.Status}",
                stage.CompletedAt ?? stage.UpdatedAt ?? stage.StartedAt ?? stage.CreateAt,
                null,
                stage.RequiresCustomerAction,
                stage.IsBlocked))
            .Concat(discussions.Select(discussion => new ServiceActivityItem(
                "Discussion",
                discussion.Stage.ToString(),
                discussion.Comment,
                discussion.CreateAt,
                discussion.Author,
                false,
                false)))
            .Concat(messages.Select(message => new ServiceActivityItem(
                message.MessageType == MessageType.System ? "System message" : "Message",
                message.Subject,
                message.Content,
                message.CreatedDate,
                message.Author,
                false,
                message.IsUrgent)))
            .Concat(assessments.Select(assessment => new ServiceActivityItem(
                "Assessment",
                assessment.Decision,
                $"Tax: {assessment.TaxAmount}; Duty: {assessment.DutyAmount}",
                assessment.CreateAt,
                assessment.Author,
                false,
                false)))
            .OrderByDescending(activity => activity.Timestamp)
            .Take(100)
            .ToList();

        return HandleSuccessResponse(activities);
    }


    private async Task<bool> IsManager(long userId)
    {
        return await _context.UserRoles
            .AsNoTracking()
            .AnyAsync(userRole =>
                userRole.UserId == userId &&
                userRole.User.RecordStatus == RecordStatus.Active &&
                userRole.Role.RecordStatus == RecordStatus.Active &&
                userRole.Role.Name.ToLower() == "manager");
    }

    private sealed record ServiceActivityItem(
        string Type,
        string Title,
        string Description,
        DateTime Timestamp,
        string? Actor,
        bool RequiresCustomerAction,
        bool IsUrgent);
}
