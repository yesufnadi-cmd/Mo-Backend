using MohamedTransit.Domain.Common;
using MohamedTransit.Domain.Entities;

namespace MohamedTransit.UnitTests;

public class ShipmentWorkflowTests
{
    [Fact]
    public void EnsureWorkflowStages_Unimodal_CreatesExpectedStagesAndStatuses()
    {
        var shipment = Shipment.Create(
            "MT-TEST-UNIMODAL",
            "Test item",
            1,
            "Test description",
            TransportMode.UniModalRoad,
            HubLocation.Adama,
            "Origin",
            "Destination");
        shipment.UpdateServiceType(ServiceType.Unimodal);

        var added = shipment.EnsureWorkflowStages();

        Assert.True(added);
        Assert.Collection(
            shipment.Stages.OrderBy(stage => stage.Stage),
            stage =>
            {
                Assert.Equal(ShipmentStage.LocalPermission, stage.Stage);
                Assert.Equal(StageStatus.Completed, stage.Status);
            },
            stage =>
            {
                Assert.Equal(ShipmentStage.Arrival, stage.Stage);
                Assert.Equal(StageStatus.InProgress, stage.Status);
            },
            stage =>
            {
                Assert.Equal(ShipmentStage.ClearanceRequest, stage.Stage);
                Assert.Equal(StageStatus.NotStarted, stage.Status);
            });

        Assert.False(shipment.EnsureWorkflowStages());

    }
}
