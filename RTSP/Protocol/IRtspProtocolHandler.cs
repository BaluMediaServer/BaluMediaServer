using BaluMediaServer.Models;

namespace BaluMediaServer.RTSP.Protocol;

/// <summary>
/// Interface for handling RTSP protocol requests and responses.
/// </summary>
public interface IRtspProtocolHandler
{
    /// <summary>
    /// Parses an RTSP request from a stream.
    /// </summary>
    /// <param name="reader">The stream reader.</param>
    /// <param name="requestLine">The first line of the request.</param>
    /// <returns>The parsed request, or null if invalid.</returns>
    Task<RtspRequest?> ParseRequestAsync(StreamReader reader, string requestLine);

    /// <summary>
    /// Sends an RTSP response.
    /// </summary>
    /// <param name="writer">The stream writer.</param>
    /// <param name="statusCode">The status code.</param>
    /// <param name="statusText">The status text.</param>
    /// <param name="cseq">The CSeq number.</param>
    /// <param name="headers">Additional headers.</param>
    /// <param name="body">The response body.</param>
    Task SendResponseAsync(StreamWriter writer, int statusCode, string statusText,
        int cseq, Dictionary<string, string>? headers = null, string? body = null);

    /// <summary>
    /// Handles an OPTIONS request.
    /// </summary>
    /// <param name="writer">The stream writer.</param>
    /// <param name="request">The request.</param>
    Task HandleOptionsAsync(StreamWriter writer, RtspRequest request);

    /// <summary>
    /// Handles a DESCRIBE request.
    /// </summary>
    /// <param name="writer">The stream writer.</param>
    /// <param name="request">The request.</param>
    /// <param name="client">The client.</param>
    Task HandleDescribeAsync(StreamWriter writer, RtspRequest request, Client client);

    /// <summary>
    /// Handles a SETUP request.
    /// </summary>
    /// <param name="writer">The stream writer.</param>
    /// <param name="request">The request.</param>
    /// <param name="client">The client.</param>
    Task HandleSetupAsync(StreamWriter writer, RtspRequest request, Client client);

    /// <summary>
    /// Handles a PLAY request.
    /// </summary>
    /// <param name="writer">The stream writer.</param>
    /// <param name="request">The request.</param>
    /// <param name="client">The client.</param>
    /// <returns>True if streaming should start.</returns>
    Task<bool> HandlePlayAsync(StreamWriter writer, RtspRequest request, Client client);

    /// <summary>
    /// Handles a TEARDOWN request.
    /// </summary>
    /// <param name="writer">The stream writer.</param>
    /// <param name="request">The request.</param>
    /// <param name="client">The client.</param>
    Task HandleTeardownAsync(StreamWriter writer, RtspRequest request, Client client);

    /// <summary>
    /// Processes the URI and sets client properties.
    /// </summary>
    /// <param name="uri">The URI path.</param>
    /// <param name="writer">The stream writer.</param>
    /// <param name="request">The request.</param>
    /// <param name="client">The client.</param>
    /// <returns>True if URI is valid.</returns>
    Task<bool> HandleUriAsync(string uri, StreamWriter writer, RtspRequest request, Client client);
}
