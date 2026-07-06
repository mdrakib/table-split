namespace TableSplit.Models;

public class EDIMessage
{
    public int Id { get; set; }
    public required string MessageType { get; set; }
    public required string SenderId { get; set; }
    public required string ReceiverId { get; set; }
    public required string Content { get; set; }
    public string? Status { get; set; }
    public DateTime CreatedAt { get; set; }
    public bool IsArchived { get; set; }
}
