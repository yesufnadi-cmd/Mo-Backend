namespace MohamedTransit.API.DTO.MOT.Request;

public class ManagerEscalateServiceRequest
{
    public long ServiceId { get; set; }
    public string Reason { get; set; } = string.Empty;
    public string? Notes { get; set; }
}
