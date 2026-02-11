using System;
using System.Collections.Generic;

namespace GarminTempApi.Models;

/// <summary>
/// Persists a chat session so the AI can remember previous conversations.
/// </summary>
public class ChatSession
{
    public int Id { get; set; }
    public string SessionId { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime LastMessageUtc { get; set; } = DateTime.UtcNow;
    public string? Summary { get; set; }

    public List<ChatSessionMessage> Messages { get; set; } = new();
}

/// <summary>
/// A single message within a chat session.
/// </summary>
public class ChatSessionMessage
{
    public int Id { get; set; }
    public int ChatSessionId { get; set; }
    public ChatSession ChatSession { get; set; } = null!;
    public string Role { get; set; } = "user";
    public string Content { get; set; } = string.Empty;
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Tracks which tool functions the AI called for this message (JSON array).
    /// </summary>
    public string? ToolCallsJson { get; set; }
}
