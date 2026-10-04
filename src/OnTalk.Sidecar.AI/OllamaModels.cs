using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OnTalk.Sidecar.AI;

/// <summary>이 PC의 Ollama에 설치된 모델 이름을 읽는다. 주소는 루프백으로 고정이다.</summary>
public static class OllamaModels
{
    public const string DefaultAnalysis = "gemma4:e2b";
    public const string DefaultEmbedding = "bge-m3";

    public static async Task<IReadOnlyList<string>> ListAsync(CancellationToken cancel, HttpMessageHandler? handler = null)
    {
        using var http = handler is null ? new HttpClient() : new HttpClient(handler);
        http.BaseAddress = new Uri("http://127.0.0.1:11434");
        http.Timeout = TimeSpan.FromSeconds(10);
        try
        {
            var reply = await http.GetFromJsonAsync<JsonObject>("/api/tags", cancel);
            return (reply?["models"] as JsonArray ?? [])
                .Select(m => m?["name"]?.GetValue<string>() ?? "")
                .Where(name => name.Length > 0)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (HttpRequestException ex)
        {
            throw new AnalysisUnavailableException("로컬 AI(Ollama)에 연결하지 못했습니다.", ex);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            throw new AnalysisUnavailableException("로컬 AI의 답을 해석하지 못했습니다.", ex);
        }
        catch (TaskCanceledException ex) when (!cancel.IsCancellationRequested)
        {
            throw new AnalysisUnavailableException("로컬 AI가 제한 시간 안에 답하지 않았습니다.", ex);
        }
    }
}
