using System.Text.Json;

namespace CopilotBridge.Cli.Models;

/// <summary>Strict AOT-safe parser for the one scalar the estimator accepts.</summary>
internal static class CountTokensResponseParser
{
    public static bool TryParse(
        ReadOnlySpan<byte> body,
        out int inputTokens,
        out string? error)
    {
        inputTokens = 0;
        error = null;
        if (body.IsEmpty)
        {
            error = "empty count_tokens response";
            return false;
        }

        try
        {
            var reader = new Utf8JsonReader(body);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                error = "count_tokens response is not a JSON object";
                return false;
            }

            var found = false;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType != JsonTokenType.PropertyName)
                {
                    error = "malformed count_tokens response";
                    return false;
                }

                var isInputTokens = reader.ValueTextEquals("input_tokens"u8);
                if (!reader.Read())
                {
                    error = "malformed count_tokens response";
                    return false;
                }

                if (!isInputTokens)
                {
                    reader.Skip();
                    continue;
                }

                if (found || reader.TokenType != JsonTokenType.Number
                    || !reader.TryGetInt32(out inputTokens) || inputTokens < 0)
                {
                    error = "input_tokens must be one non-negative 32-bit integer";
                    return false;
                }
                found = true;
            }

            if (reader.TokenType != JsonTokenType.EndObject)
            {
                error = "malformed count_tokens response";
                return false;
            }
            if (reader.Read())
            {
                error = "count_tokens response contains trailing JSON";
                return false;
            }
            if (!found)
            {
                error = "count_tokens response omitted input_tokens";
                return false;
            }
            return true;
        }
        catch (JsonException ex)
        {
            error = "malformed count_tokens response: " + ex.Message;
            return false;
        }
    }
}
