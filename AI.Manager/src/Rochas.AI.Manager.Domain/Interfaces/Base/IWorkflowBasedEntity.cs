namespace Rochas.AI.Manager.Domain.Interfaces.Base;

public interface IWorkflowBasedEntity
{
    int Id { get; set; }
    int StatusId { get; set; }
    int? NextStatusId { get; set; }
    int? RequesterId { get; set; }
    int? ApproverId { get; set; }
    string? RequestNote { get; set; }
    string? ApprovalNote { get; set; }
    bool Approved { get; set; }

    void SetStatus(int statusId, int? nextStatusId = null);
    void RequestApproval(int requesterId, string? requestNote = null);
    void Approve(int approverId, string? approvalNote = null);
    void Reject(int approverId, string? approvalNote = null);
}
