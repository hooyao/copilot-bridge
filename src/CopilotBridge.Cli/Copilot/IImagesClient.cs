namespace CopilotBridge.Cli.Copilot;

internal enum ImageOperation { Generate, Edit }

/// <summary>Forwards an opaque Images API request to Copilot.</summary>
internal interface IImagesClient
{
    /// <remarks>The caller owns the returned response and its body stream.</remarks>
    ValueTask<HttpResponseMessage> PostImageAsync(
        ImageOperation operation, ReadOnlyMemory<byte> body, string? contentType,
        CancellationToken ct = default);
}
