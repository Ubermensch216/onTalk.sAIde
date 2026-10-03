using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using OnTalk.Sidecar.Core;

namespace OnTalk.Sidecar.AI;

/// <summary>의미 검색용 벡터를 이 PC의 Ollama에서 만든다. 주소는 루프백으로 고정이다.</summary>
public sealed class OllamaEmbedder : IDisposable
{
    // 모델이 받을 수 있는 길이를 넘지 않게 자른다. 쪽지는 4000바이트 이하라 보통 잘리지 않는다.
    private const int MaxChars = 6000;
    private readonly HttpClient _http;

    public string Model { get; }

    public OllamaEmbedder(string model = "bge-m3", HttpMessageHandler? handler = null)
    {
        Model = model;
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.BaseAddress = new Uri("http://127.0.0.1:11434");
        _http.Timeout = TimeSpan.FromMinutes(2);
    }

    public static string TextOf(ReceivedNote note) => $"{note.SenderName} {note.SenderDetail}\n{note.Body}";

    /// <summary>길이 1로 맞춘 벡터를 돌려준다.</summary>
    public async Task<float[]> EmbedAsync(string text, CancellationToken cancel)
    {
        var request = new JsonObject
        {
            ["model"] = Model,
            ["input"] = text.Length > MaxChars ? text[..MaxChars] : text,
        };
        try
        {
            using var response = await _http.PostAsJsonAsync("/api/embed", request, cancel);
            if (!response.IsSuccessStatusCode)
            {
                throw new AnalysisUnavailableException($"로컬 AI가 오류를 돌려주었습니다 ({(int)response.StatusCode}).");
            }
            var reply = await response.Content.ReadFromJsonAsync<JsonObject>(cancel);
            var vector = reply?["embeddings"] is JsonArray { Count: > 0 } vectors ? vectors[0].Deserialize<float[]>() : null;
            if (vector is not { Length: > 0 })
            {
                throw new AnalysisUnavailableException("로컬 AI가 빈 벡터를 돌려주었습니다.");
            }
            return Similarity.Normalize(vector);
        }
        catch (HttpRequestException ex)
        {
            throw new AnalysisUnavailableException("로컬 AI(Ollama)에 연결하지 못했습니다.", ex);
        }
        catch (JsonException ex)
        {
            throw new AnalysisUnavailableException("로컬 AI의 답을 해석하지 못했습니다.", ex);
        }
        catch (TaskCanceledException ex) when (!cancel.IsCancellationRequested)
        {
            throw new AnalysisUnavailableException("로컬 AI가 제한 시간 안에 답하지 않았습니다.", ex);
        }
    }

    public void Dispose() => _http.Dispose();
}
