using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Net.Http.Headers;

namespace IntegrationOps.Api.IntegrationRuns;

internal sealed record IntegrationRunIntakeInput(
    IntegrationRunIntakeRequest Request, byte[] KeyHash, byte[] Fingerprint)
{
    internal const int MaximumBodyBytes = 8192;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static async Task<(IntegrationRunIntakeInput? Input, int Error)> ReadAsync(
        HttpRequest request, CancellationToken cancellationToken)
    {
        if (!MediaTypeHeaderValue.TryParse(request.ContentType, out var media) ||
            !media.MediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase) ||
            media.Parameters.Any(parameter => !parameter.Name.Equals("charset", StringComparison.OrdinalIgnoreCase)) ||
            (media.Charset.HasValue && !HeaderUtilities.RemoveQuotes(media.Charset).Equals("utf-8", StringComparison.OrdinalIgnoreCase)) ||
            media.Parameters.Count(parameter => parameter.Name.Equals("charset", StringComparison.OrdinalIgnoreCase)) > 1 ||
            (request.Headers.ContentEncoding.Count > 0 &&
             !string.Equals(request.Headers.ContentEncoding.ToString(), "identity", StringComparison.OrdinalIgnoreCase)))
        {
            return (null, StatusCodes.Status415UnsupportedMediaType);
        }

        var keys = request.Headers["Idempotency-Key"];
        if (keys.Count != 1 || keys[0] is not { } key || !ValidKey(key))
        {
            return (null, StatusCodes.Status400BadRequest);
        }

        if (request.ContentLength > MaximumBodyBytes)
        {
            return (null, StatusCodes.Status413PayloadTooLarge);
        }

        // One extra byte detects overflow even when Content-Length is absent or misleading.
        var bytes = new byte[MaximumBodyBytes + 1];
        var length = 0;
        while (length < bytes.Length)
        {
            var read = await request.Body.ReadAsync(bytes.AsMemory(length), cancellationToken);
            if (read == 0)
            {
                break;
            }

            length += read;
        }

        if (length > MaximumBodyBytes)
        {
            return (null, StatusCodes.Status413PayloadTooLarge);
        }

        var body = ParseBody(bytes.AsMemory(0, length));
        return body is null
            ? (null, StatusCodes.Status400BadRequest)
            : (new(body, SHA256.HashData(StrictUtf8.GetBytes(key)), FingerprintFor(body)), 0);
    }

    private static bool ValidKey(string key) => key.Length is >= 1 and <= 128 &&
        key.All(character => character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_' or '-');

    internal static IntegrationRunIntakeRequest? ParseBody(ReadOnlyMemory<byte> bytes)
    {
        try
        {
            // Check raw UTF-8 without replacement; GetString also rejects malformed escaped surrogates.
            _ = StrictUtf8.GetCharCount(bytes.Span);
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 4 });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            string? partner = null;
            string? operation = null;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!seen.Add(property.Name) || property.Value.ValueKind != JsonValueKind.String)
                {
                    return null;
                }

                switch (property.Name)
                {
                    case "partner": partner = NormalizeLabel(property.Value.GetString()!); break;
                    case "operation": operation = NormalizeLabel(property.Value.GetString()!); break;
                    default: return null;
                }
            }

            return partner is not null && operation is not null ? new(partner, operation) : null;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException)
        {
            // Only parsing/normalization lives in this boundary. Never expose submitted text or diagnostics.
            return null;
        }
    }

    internal static string? NormalizeLabel(string value)
    {
        var remaining = value.AsSpan();
        while (!remaining.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(remaining, out var rune, out var consumed) != OperationStatus.Done ||
                Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)
            {
                return null;
            }

            remaining = remaining[consumed..];
        }

        var normalized = value.Trim().Normalize(NormalizationForm.FormC);
        var count = normalized.EnumerateRunes().Count();
        return count is >= 1 and <= 160 ? normalized : null;
    }

    internal static byte[] FingerprintFor(IntegrationRunIntakeRequest request)
    {
        var domain = StrictUtf8.GetBytes("IntegrationOps:POST:/api/integration-runs:v1");
        var partner = StrictUtf8.GetBytes(request.Partner);
        var operation = StrictUtf8.GetBytes(request.Operation);
        var frame = new byte[domain.Length + 1 + 4 + partner.Length + 4 + operation.Length];
        domain.CopyTo(frame, 0); // The next byte is the zero-initialized domain terminator.
        var offset = domain.Length + 1;
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(offset), (uint)partner.Length);
        offset += 4;
        partner.CopyTo(frame, offset);
        offset += partner.Length;
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(offset), (uint)operation.Length);
        operation.CopyTo(frame, offset + 4);
        return SHA256.HashData(frame);
    }
}
