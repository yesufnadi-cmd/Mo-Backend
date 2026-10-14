namespace MohamedTransit.API.DTO.MOT.Request;

public class AddManagerStageDiscussionRequest
{
    public long ServiceId { get; set; }
    public long StageId { get; set; }
    public string Comment { get; set; } = string.Empty;
}
