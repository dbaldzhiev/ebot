namespace EBot.WebHost.Services;

/// <summary>
/// Abstraction over the AI backend (Anthropic Claude or Ollama).
/// Implementations process a natural-language user message and push
/// results back to the caller's SignalR connection.
/// </summary>
public interface IChatService
{
    Task ProcessAsync(string userMessage, string connectionId,
        CancellationToken cancellationToken = default);
}
