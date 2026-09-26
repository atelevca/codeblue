using System.Text.Json;

namespace SemanticKernel.MedicalCorrection
{
    /// <summary>Extracts the <c>[{"id","text"}]</c> array from a model answer that may be fenced or wrapped in text.</summary>
    public static class CorrectionResponseParser
    {
        private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

        public static bool TryParse(string? response, out IReadOnlyList<CorrectedSegment> segments)
        {
            segments = [];
            if (string.IsNullOrWhiteSpace(response))
            {
                return false;
            }

            var text = response.Replace("```json", "", StringComparison.OrdinalIgnoreCase).Replace("```", "");
            var start = text.IndexOf('[');
            var end = text.LastIndexOf(']');
            if (start < 0 || end <= start)
            {
                return false;
            }

            try
            {
                var parsed = JsonSerializer.Deserialize<List<CorrectedSegment?>>(text[start..(end + 1)], JsonOptions);
                if (parsed == null || parsed.Any(s => s == null))
                {
                    return false;
                }
                segments = parsed!;
                return true;
            }
            catch (JsonException)
            {
                return false;
            }
        }
    }
}
