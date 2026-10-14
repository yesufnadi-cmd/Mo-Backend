namespace MohamedTransit.API.DTO.MOT.Request;

public class ManagerNotifyImporterRequest
{
    public long ServiceId { get; set; }
    public string Subject { get; set; } = "Service update";
    public string Message { get; set; } = string.Empty;
    public bool IsUrgent { get; set; }
}
