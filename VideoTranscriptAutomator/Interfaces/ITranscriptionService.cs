using VideoTranscriptAutomator.Models;

namespace VideoTranscriptAutomator.Interfaces;

public interface ITranscriptionService
{
    Task<TranscriptionResult> TranscribeAsync(byte[] fileBytes, string fileName, CancellationToken cancellationToken = default);
}
