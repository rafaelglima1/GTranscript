using VideoTranscriptAutomator.Models;

namespace VideoTranscriptAutomator.Interfaces;

public interface ITranscriptionService
{
    Task<TranscriptionResult> TranscribeAsync(Stream fileStream, string fileName, CancellationToken cancellationToken = default);
}
