using System.Security.Claims;

using MediatR;

using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

using MohamedTransit.Application;
using MohamedTransit.Application.Helper;
using MohamedTransit.Domain.Common;
using MohamedTransit.Domain.Data;
using MohamedTransit.Domain.Entities;

namespace MohamedTransit.Application.Handlers.ShipmentStageHandler;

internal class UpdateServiceStageCommandHandler : IRequestHandler<UpdateServiceStageCommand, OperationResult<ServiceStageExecution>>
{
    private readonly ApplicationDbContext _context;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public UpdateServiceStageCommandHandler(
        ApplicationDbContext context,
        IHttpContextAccessor httpContextAccessor)
    {
        _context = context;
        _httpContextAccessor = httpContextAccessor;
    }

    public async Task<OperationResult<ServiceStageExecution>> Handle(UpdateServiceStageCommand request, CancellationToken cancellationToken)
    {
        var result = new OperationResult<ServiceStageExecution>();

        var stageExecution = await _context.ServiceStageExecutions
            .Include(s => s.Shipment)
            .FirstOrDefaultAsync(s => s.Id == request.ShipmentStageId && s.ShipmentId == request.ShipmentId, cancellationToken);

        if (stageExecution == null)
        {
            result.AddError(ErrorCode.NotFound, $"Stage execution with ID '{request.ShipmentStageId}' for Shipment '{request.ShipmentId}' was not found.");
            return result;
        }

        var updatedByUserId = request.UpdatedByUserId;
        if (updatedByUserId <= 0)
        {
            var userIdClaim = _httpContextAccessor.HttpContext?.User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                ?? _httpContextAccessor.HttpContext?.User.FindFirst("id")?.Value
                ?? _httpContextAccessor.HttpContext?.User.FindFirst("sub")?.Value;
            updatedByUserId = long.TryParse(userIdClaim, out var claimUserId) && claimUserId > 0
                ? claimUserId
                : 0;
        }

        var candidateUserIds = new long?[]
            {
                updatedByUserId > 0 ? updatedByUserId : null,
                stageExecution.Shipment?.AssignedCaseExecutorId,
                stageExecution.Shipment?.AssignedAssessorId
            }
            .Where(userId => userId is > 0)
            .Select(userId => userId!.Value)
            .Distinct()
            .ToArray();

        var existingUserIds = candidateUserIds.Length == 0
            ? new List<long>()
            : await _context.Users
                .Where(user => candidateUserIds.Contains(user.Id) && user.RecordStatus == RecordStatus.Active)
                .Select(user => user.Id)
                .ToListAsync(cancellationToken);

        long? resolvedUpdatedByUserId = candidateUserIds
            .Where(existingUserIds.Contains)
            .Select(userId => (long?)userId)
            .FirstOrDefault();

        resolvedUpdatedByUserId ??= await _context.Users
            .Where(user => user.RecordStatus == RecordStatus.Active)
            .OrderBy(user => user.Id)
            .Select(user => (long?)user.Id)
            .FirstOrDefaultAsync(cancellationToken);

        stageExecution.UpdateStatus(request.Status, request.Notes, resolvedUpdatedByUserId);

        // 4. ለውጦችን Save ማድረግ
        await _context.SaveChangesAsync(cancellationToken);

        // 5. Result መመለስ
        result.Payload = stageExecution;
        result.Message = "Stage execution status updated successfully.";

        return result;
    }
}
