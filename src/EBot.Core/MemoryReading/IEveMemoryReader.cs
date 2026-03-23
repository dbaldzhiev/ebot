namespace EBot.Core.MemoryReading;

/// <summary>
/// Abstraction over the two EVE memory reading strategies:
///   • <see cref="SanderlingReader"/> — HTTP or CLI mode (original)
///   • <see cref="DirectMemoryReader"/> — in-process, no file I/O (preferred)
/// </summary>
public interface IEveMemoryReader : IDisposable
{
    Task<MemoryReadingResult> ReadMemoryAsync(CancellationToken ct = default);
}
