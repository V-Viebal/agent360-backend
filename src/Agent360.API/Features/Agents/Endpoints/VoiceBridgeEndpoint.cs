using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Asp.Versioning;
using Agent360.API.Abstractions;
using Agent360.API.Features.Agents.Models;
using Agent360.API.Features.Agents.Security;
using Agent360.API.Features.Agents.Services;

namespace Agent360.API.Features.Agents.Endpoints;

public sealed record VoiceSpeechRequestDto(
    [property: JsonPropertyName("input")] string Input,
    [property: JsonPropertyName("model")] string? Model = "eleven_flash_v2_5",
    [property: JsonPropertyName("voice")] string? Voice = "alloy",
    [property: JsonPropertyName("response_format")] string? ResponseFormat = "mp3",
    [property: JsonPropertyName("language_code")] string? LanguageCode = null,
    [property: JsonPropertyName("speed")] double? Speed = 1.0);

public sealed class VoiceBridgeEndpoint : IEndpoint
{
    private const string DefaultLlm360BaseUrl = "http://100.91.180.20:59528/v1";
    private const string DefaultLlm360ApiKey = "sk-cli-90f6bff83292427e97f330cb329f07db";
    private const string DefaultSttModel = "scribe_v1";
    private const string DefaultTtsModel = "eleven_flash_v2_5";
    private const string DefaultTtsVoice = "alloy";

    private static readonly HttpClient SharedHttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(45)
    };

    private static (string BaseUrl, string ApiKey) ResolveLlm360Config(IConfiguration config)
    {
        var baseUrl = config["Llm360:SkyerAudioBaseUrl"]
            ?? Environment.GetEnvironmentVariable("LLM360_AUDIO_BASE_URL")
            ?? Environment.GetEnvironmentVariable("SKYER_LLM360_BASE_URL")
            ?? DefaultLlm360BaseUrl;
        var apiKey = config["Llm360:SkyerAudioApiKey"]
            ?? Environment.GetEnvironmentVariable("LLM360_AUDIO_API_KEY")
            ?? Environment.GetEnvironmentVariable("SKYER_LLM360_API_KEY")
            ?? DefaultLlm360ApiKey;
        return (baseUrl.TrimEnd('/'), apiKey.Trim());
    }

    public void MapEndpoint(IEndpointRouteBuilder routeBuilder)
    {
        // 1. GET /api/v1/voice/models (and /api/v1/agents/voice/models)
        foreach (var route in new[] { "voice/models", "agents/voice/models" })
        {
            routeBuilder.MapGet(route, (IConfiguration config) =>
            {
                var (baseUrl, _) = ResolveLlm360Config(config);
                return Results.Ok(new
                {
                    node = "5a54d0",
                    server = "1st",
                    endpoint = baseUrl,
                    stt_models = new object[]
                    {
                        new { id = "scribe_v1", name = "ElevenLabs Scribe v1", provider = "elevenlabs", @default = true },
                        new { id = "scribe_v2", name = "ElevenLabs Scribe v2", provider = "elevenlabs", @default = false },
                        new { id = "gemini-3.8-flash", name = "Gemini 3.8 Flash Multimodal STT", provider = "gemini", @default = false },
                        new { id = "gemini-2.5-flash", name = "Gemini 2.5 Flash Multimodal STT", provider = "gemini", @default = false },
                    },
                    tts_models = new object[]
                    {
                        new { id = "eleven_flash_v2_5", name = "ElevenLabs Flash v2.5 (Ultra Low Latency)", provider = "elevenlabs", @default = true },
                        new { id = "eleven_turbo_v2_5", name = "ElevenLabs Turbo v2.5", provider = "elevenlabs", @default = false },
                        new { id = "eleven_multilingual_v2", name = "ElevenLabs Multilingual v2", provider = "elevenlabs", @default = false },
                        new { id = "gemini-2.5-flash-preview-tts", name = "Gemini 2.5 Flash Native TTS (WAV 24kHz)", provider = "gemini", @default = false },
                        new { id = "gemini-2.5-pro-preview-tts", name = "Gemini 2.5 Pro Native TTS (WAV 24kHz)", provider = "gemini", @default = false },
                    },
                    voices = new object[]
                    {
                        new { id = "alloy", name = "Sarah (Female - Multilingual)", provider = "elevenlabs/gemini" },
                        new { id = "echo", name = "Roger (Male - Multilingual)", provider = "elevenlabs/gemini" },
                        new { id = "fable", name = "George (Male - Storyteller)", provider = "elevenlabs/gemini" },
                        new { id = "nova", name = "Kore (Female - Gemini Native)", provider = "gemini" },
                        new { id = "onyx", name = "Puck (Male - Gemini Native)", provider = "gemini" },
                    }
                });
            })
            .MapToApiVersion(ApiVersion.Default)
            .AllowAnonymous();
        }

        // 2. Mode A Step 1: POST /api/v1/voice/transcribe (Audio Chunk -> CLIProxy /v1/audio/transcriptions)
        foreach (var route in new[] { "voice/transcribe", "agents/voice/transcribe" })
        {
            routeBuilder.MapPost(route, async (
                HttpContext httpContext,
                IConfiguration config,
                CancellationToken ct) =>
            {
                if (!httpContext.Request.HasFormContentType)
                {
                    return Results.BadRequest(new { error = "multipart/form-data with audio 'file' is required" });
                }

                var form = await httpContext.Request.ReadFormAsync(ct);
                var file = form.Files.GetFile("file") ?? (form.Files.Count > 0 ? form.Files[0] : null);
                if (file is null || file.Length == 0)
                {
                    return Results.BadRequest(new { error = "Uploaded audio file is empty" });
                }

                var model = form.TryGetValue("model", out var m) && !string.IsNullOrWhiteSpace(m)
                    ? m.ToString().Trim()
                    : DefaultSttModel;
                var language = form.TryGetValue("language", out var l) && !string.IsNullOrWhiteSpace(l)
                    ? l.ToString().Trim()
                    : null;
                var prompt = form.TryGetValue("prompt", out var p) && !string.IsNullOrWhiteSpace(p)
                    ? p.ToString().Trim()
                    : null;
                var responseFormat = form.TryGetValue("response_format", out var rf) && !string.IsNullOrWhiteSpace(rf)
                    ? rf.ToString().Trim()
                    : "json";

                await using var ms = new MemoryStream();
                await file.CopyToAsync(ms, ct);
                var audioBytes = ms.ToArray();

                var (body, contentType, statusCode) = await CallLlm360TranscribeAsync(
                    config,
                    audioBytes,
                    string.IsNullOrWhiteSpace(file.FileName) ? "audio.webm" : file.FileName,
                    string.IsNullOrWhiteSpace(file.ContentType) ? "audio/webm" : file.ContentType,
                    model,
                    language,
                    prompt,
                    responseFormat,
                    ct);

                if (statusCode < 200 || statusCode >= 300)
                {
                    return Results.Text(Encoding.UTF8.GetString(body), contentType: "application/json", statusCode: statusCode);
                }
                return Results.Bytes(body, contentType);
            })
            .DisableAntiforgery()
            .MapToApiVersion(ApiVersion.Default)
            .AllowAnonymous();
        }

        // 3. Mode A Step 3: POST /api/v1/voice/speech (Assistant Text -> CLIProxy /v1/audio/speech)
        foreach (var route in new[] { "voice/speech", "agents/voice/speech" })
        {
            routeBuilder.MapPost(route, async (
                VoiceSpeechRequestDto req,
                IConfiguration config,
                CancellationToken ct) =>
            {
                if (string.IsNullOrWhiteSpace(req.Input))
                {
                    return Results.BadRequest(new { error = "input text is required" });
                }

                var (body, contentType, statusCode) = await CallLlm360SpeechAsync(
                    config,
                    req.Input.Trim(),
                    string.IsNullOrWhiteSpace(req.Model) ? DefaultTtsModel : req.Model.Trim(),
                    string.IsNullOrWhiteSpace(req.Voice) ? DefaultTtsVoice : req.Voice.Trim(),
                    string.IsNullOrWhiteSpace(req.ResponseFormat) ? "mp3" : req.ResponseFormat.Trim(),
                    req.LanguageCode,
                    ct);

                if (statusCode < 200 || statusCode >= 300)
                {
                    return Results.Text(Encoding.UTF8.GetString(body), contentType: "application/json", statusCode: statusCode);
                }
                return Results.Bytes(body, contentType);
            })
            .MapToApiVersion(ApiVersion.Default)
            .AllowAnonymous();
        }

        // 3b. Mode A Full Turn: POST /api/v1/voice/turn & /api/v1/agents/voice/turn (Audio In -> Text LLM -> Audio Out)
        foreach (var route in new[] { "voice/turn", "agents/voice/turn" })
        {
            routeBuilder.MapPost(route, async (
                HttpContext httpContext,
                IConfiguration config,
                CancellationToken ct) =>
            {
                if (!httpContext.Request.HasFormContentType)
                {
                    return Results.BadRequest(new { error = "multipart/form-data with audio 'file' is required" });
                }

                var form = await httpContext.Request.ReadFormAsync(ct);
                var file = form.Files.GetFile("file") ?? (form.Files.Count > 0 ? form.Files[0] : null);
                if (file is null || file.Length == 0)
                {
                    return Results.BadRequest(new { error = "Uploaded audio file is empty" });
                }

                var sttModel = form.TryGetValue("stt_model", out var sm) && !string.IsNullOrWhiteSpace(sm) ? sm.ToString().Trim() : DefaultSttModel;
                var llmModel = form.TryGetValue("llm_model", out var lm) && !string.IsNullOrWhiteSpace(lm) ? lm.ToString().Trim() : "grok-4.7";
                var ttsModel = form.TryGetValue("tts_model", out var tm) && !string.IsNullOrWhiteSpace(tm) ? tm.ToString().Trim() : DefaultTtsModel;
                var voice = form.TryGetValue("voice", out var v) && !string.IsNullOrWhiteSpace(v) ? v.ToString().Trim() : DefaultTtsVoice;
                var language = form.TryGetValue("language", out var l) && !string.IsNullOrWhiteSpace(l) ? l.ToString().Trim() : null;

                await using var ms = new MemoryStream();
                await file.CopyToAsync(ms, ct);
                var audioBytes = ms.ToArray();

                var (sttBody, _, sttStatus) = await CallLlm360TranscribeAsync(
                    config,
                    audioBytes,
                    string.IsNullOrWhiteSpace(file.FileName) ? "audio.webm" : file.FileName,
                    string.IsNullOrWhiteSpace(file.ContentType) ? "audio/webm" : file.ContentType,
                    sttModel,
                    language,
                    null,
                    "json",
                    ct);

                if (sttStatus < 200 || sttStatus >= 300)
                {
                    return Results.Text(Encoding.UTF8.GetString(sttBody), contentType: "application/json", statusCode: sttStatus);
                }

                var transcript = "";
                try
                {
                    using var sttDoc = JsonDocument.Parse(sttBody);
                    if (sttDoc.RootElement.TryGetProperty("text", out var tProp))
                    {
                        transcript = tProp.GetString()?.Trim() ?? "";
                    }
                }
                catch
                {
                    transcript = Encoding.UTF8.GetString(sttBody).Trim();
                }

                if (string.IsNullOrWhiteSpace(transcript))
                {
                    return Results.Ok(new { transcript = "", reply = "", audio_base64 = "", mime_type = "audio/mpeg", node = "5a54d0" });
                }

                var (baseUrl, apiKey) = ResolveLlm360Config(config);
                var chatPayload = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    model = llmModel,
                    messages = new[]
                    {
                        new { role = "system", content = "Bạn là trợ lý giọng nói AI thông minh trên nền tảng Skyer. Hãy trả lời tự nhiên, súc tích, rõ ràng." },
                        new { role = "user", content = transcript }
                    }
                });
                using var chatReq = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/chat/completions")
                {
                    Content = new ByteArrayContent(chatPayload)
                };
                chatReq.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                chatReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

                var replyText = "";
                using (var chatResp = await SharedHttpClient.SendAsync(chatReq, ct))
                {
                    if (chatResp.IsSuccessStatusCode)
                    {
                        var chatBytes = await chatResp.Content.ReadAsByteArrayAsync(ct);
                        using var chatDoc = JsonDocument.Parse(chatBytes);
                        if (chatDoc.RootElement.TryGetProperty("choices", out var choices) &&
                            choices.ValueKind == JsonValueKind.Array &&
                            choices.GetArrayLength() > 0)
                        {
                            replyText = choices[0].GetProperty("message").GetProperty("content").GetString()?.Trim() ?? "";
                        }
                    }
                }

                var cleanTts = CleanTextForSpeech(replyText);
                byte[] ttsBytes = [];
                var ttsMime = "audio/mpeg";
                if (!string.IsNullOrEmpty(cleanTts))
                {
                    var (tb, tmime, tstatus) = await CallLlm360SpeechAsync(config, cleanTts, ttsModel, voice, "mp3", language, ct);
                    if (tstatus >= 200 && tstatus < 300)
                    {
                        ttsBytes = tb;
                        ttsMime = tmime.Split(';')[0].Trim();
                    }
                }

                return Results.Ok(new
                {
                    node = "5a54d0",
                    stt_model = sttModel,
                    llm_model = llmModel,
                    tts_model = ttsModel,
                    transcript,
                    reply = replyText,
                    mime_type = ttsMime,
                    audio_base64 = ttsBytes.Length > 0 ? Convert.ToBase64String(ttsBytes) : ""
                });
            })
            .DisableAntiforgery()
            .MapToApiVersion(ApiVersion.Default)
            .AllowAnonymous();
        }

        // 4. Mode B: Full-Duplex Realtime WebSocket (GET /api/v1/voice/realtime & /api/v1/agents/ws/voice)
        foreach (var route in new[] { "voice/realtime", "agents/ws/voice" })
        {
            routeBuilder.MapGet(route, async (
                HttpContext context,
                IQwenPawClient qwenPawClient,
                SessionQueueManager queueManager,
                IConfiguration config,
                ILogger<VoiceBridgeEndpoint> logger,
                CancellationToken ct) =>
            {
                if (!context.WebSockets.IsWebSocketRequest)
                {
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                    return;
                }

                using var webSocket = await context.WebSockets.AcceptWebSocketAsync();
                await HandleRealtimeVoiceWebSocketAsync(context, webSocket, qwenPawClient, queueManager, config, logger, ct);
            })
            .MapToApiVersion(ApiVersion.Default)
            .AllowAnonymous();
        }
    }

    private static async Task<(byte[] Body, string ContentType, int StatusCode)> CallLlm360TranscribeAsync(
        IConfiguration config,
        byte[] fileBytes,
        string fileName,
        string contentType,
        string model,
        string? language,
        string? prompt,
        string responseFormat,
        CancellationToken ct)
    {
        var (baseUrl, apiKey) = ResolveLlm360Config(config);
        using var form = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(fileBytes);
        fileContent.Headers.ContentType = MediaTypeHeaderValue.TryParse(contentType, out var parsedMime)
            ? parsedMime
            : new MediaTypeHeaderValue("audio/webm");
        form.Add(fileContent, "file", fileName);
        form.Add(new StringContent(model), "model");
        form.Add(new StringContent(responseFormat), "response_format");
        if (!string.IsNullOrWhiteSpace(language))
        {
            form.Add(new StringContent(language), "language");
        }
        if (!string.IsNullOrWhiteSpace(prompt))
        {
            form.Add(new StringContent(prompt), "prompt");
        }

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/audio/transcriptions")
        {
            Content = form
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        using var resp = await SharedHttpClient.SendAsync(req, ct);
        var respBytes = await resp.Content.ReadAsByteArrayAsync(ct);
        var respContentType = resp.Content.Headers.ContentType?.ToString() ?? "application/json";
        return (respBytes, respContentType, (int)resp.StatusCode);
    }

    private static async Task<(byte[] Body, string ContentType, int StatusCode)> CallLlm360SpeechAsync(
        IConfiguration config,
        string inputText,
        string model,
        string voice,
        string responseFormat,
        string? languageCode,
        CancellationToken ct)
    {
        var (baseUrl, apiKey) = ResolveLlm360Config(config);
        var payloadObj = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["input"] = inputText,
            ["voice"] = voice,
            ["response_format"] = responseFormat,
        };
        if (!string.IsNullOrWhiteSpace(languageCode))
        {
            payloadObj["language_code"] = languageCode;
        }

        var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(payloadObj);
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/audio/speech")
        {
            Content = new ByteArrayContent(jsonBytes)
        };
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        using var resp = await SharedHttpClient.SendAsync(req, ct);
        var respBytes = await resp.Content.ReadAsByteArrayAsync(ct);
        var respContentType = resp.Content.Headers.ContentType?.ToString() ?? "audio/mpeg";
        return (respBytes, respContentType, (int)resp.StatusCode);
    }

    private static string CleanTextForSpeech(string rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText)) return string.Empty;
        var s = Regex.Replace(rawText, @"```[\s\S]*?```", " ");
        s = Regex.Replace(s, @"`([^`]+)`", "$1");
        s = Regex.Replace(s, @"!\[.*?\]\(.*?\)", "");
        s = Regex.Replace(s, @"\[(.*?)\]\(.*?\)", "$1");
        s = Regex.Replace(s, @"[#*_~>]", "");
        s = Regex.Replace(s, @"\s+", " ").Trim();
        return s;
    }

    private static async Task SendWsJsonAsync(WebSocket ws, object payload, CancellationToken ct)
    {
        if (ws.State != WebSocketState.Open) return;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
    }

    private static async Task HandleRealtimeVoiceWebSocketAsync(
        HttpContext context,
        WebSocket webSocket,
        IQwenPawClient qwenPawClient,
        SessionQueueManager queueManager,
        IConfiguration config,
        ILogger logger,
        CancellationToken ct)
    {
        var agentId = context.Request.Query["agent_id"].FirstOrDefault() ?? "mons";
        var sessionId = context.Request.Query["session_id"].FirstOrDefault()
            ?? $"voice-rt-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
        var sttModel = context.Request.Query["stt_model"].FirstOrDefault() ?? DefaultSttModel;
        var ttsModel = context.Request.Query["tts_model"].FirstOrDefault() ?? DefaultTtsModel;
        var voice = context.Request.Query["voice"].FirstOrDefault() ?? DefaultTtsVoice;
        var language = context.Request.Query["language"].FirstOrDefault();

        await SendWsJsonAsync(webSocket, new
        {
            type = "ready",
            node = "5a54d0",
            agent_id = agentId,
            session_id = sessionId,
            stt_model = sttModel,
            tts_model = ttsModel,
            voice
        }, ct);

        using var audioBuffer = new MemoryStream();
        CancellationTokenSource? turnCts = null;
        var recvBuf = new byte[64 * 1024];

        async Task RunVoiceAgentTurnAsync(string userText, CancellationToken turnToken)
        {
            try
            {
                var (accessAllowed, _) = await AgentOwnershipGuard.CheckAccessAsync(
                    agentId, context, context.RequestServices.GetRequiredService<Agent360.API.Identity.CurrentUser>(),
                    config, turnToken);

                var wsChatScope = accessAllowed ? await ChatVisibilityGuard.ResolveScopeAsync(agentId, context, turnToken) : null;
                queueManager.SetSessionTurnActive(sessionId, true, agentId);
                var accumulated = new StringBuilder();

                try
                {
                    if (accessAllowed)
                    {
                        var sendReq = new ConsoleChatSendRequestDto(
                            SessionId: sessionId,
                            Message: userText,
                            AgentId: agentId,
                            ModelSlotOverride: null,
                            Input: null,
                            UserId: wsChatScope);

                        using var responseStream = await qwenPawClient.StreamConsoleChatAsync(sendReq, turnToken);
                    if (responseStream.IsSuccessStatusCode)
                    {
                        await using var stream = await responseStream.Content.ReadAsStreamAsync(turnToken);
                        using var reader = new StreamReader(stream);
                        string? line;
                        while ((line = await reader.ReadLineAsync(turnToken)) is not null)
                        {
                            if (webSocket.State != WebSocketState.Open || turnToken.IsCancellationRequested) break;
                            if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data:", StringComparison.Ordinal)) continue;
                            var jsonPart = line[5..].Trim();
                            if (jsonPart == "[DONE]" || string.IsNullOrEmpty(jsonPart)) continue;

                            try
                            {
                                using var evDoc = JsonDocument.Parse(jsonPart);
                                var root = evDoc.RootElement;
                                var objType = root.TryGetProperty("object", out var oProp) ? oProp.GetString() : null;
                                var status = root.TryGetProperty("status", out var sProp) ? sProp.GetString() : null;
                                if (objType == "message" && status == "completed" &&
                                    root.TryGetProperty("content", out var contentArr) &&
                                    contentArr.ValueKind == JsonValueKind.Array)
                                {
                                    foreach (var item in contentArr.EnumerateArray())
                                    {
                                        var itemType = item.TryGetProperty("type", out var tProp) ? tProp.GetString() : null;
                                        if (itemType == "text" && item.TryGetProperty("text", out var txtProp))
                                        {
                                            var chunkTxt = txtProp.GetString()?.Trim();
                                            if (!string.IsNullOrEmpty(chunkTxt))
                                            {
                                                if (accumulated.Length > 0) accumulated.Append(' ');
                                                accumulated.Append(chunkTxt);
                                                await SendWsJsonAsync(webSocket, new { type = "text_delta", delta = chunkTxt }, turnToken);
                                            }
                                        }
                                    }
                                }
                            }
                            catch
                            {
                                // Ignore malformed non-JSON SSE lines
                                }
                            }
                        }
                    }

                    // Fallback to direct LLM360 Grok-4.7 completion on node 5a54d0 if agent runtime returned empty or anonymous voice session
                    if (accumulated.Length == 0 && !turnToken.IsCancellationRequested)
                    {
                        var (baseUrl, apiKey) = ResolveLlm360Config(config);
                        var chatPayload = JsonSerializer.SerializeToUtf8Bytes(new
                        {
                            model = "grok-4.7",
                            messages = new[]
                            {
                                new { role = "system", content = "Bạn là trợ lý giọng nói AI thông minh trên nền tảng Skyer. Hãy trả lời tự nhiên, súc tích, rõ ràng." },
                                new { role = "user", content = userText }
                            }
                        });
                        using var chatReq = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/chat/completions")
                        {
                            Content = new ByteArrayContent(chatPayload)
                        };
                        chatReq.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                        chatReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

                        using var chatResp = await SharedHttpClient.SendAsync(chatReq, turnToken);
                        if (chatResp.IsSuccessStatusCode)
                        {
                            var chatBytes = await chatResp.Content.ReadAsByteArrayAsync(turnToken);
                            using var chatDoc = JsonDocument.Parse(chatBytes);
                            if (chatDoc.RootElement.TryGetProperty("choices", out var choices) &&
                                choices.ValueKind == JsonValueKind.Array &&
                                choices.GetArrayLength() > 0)
                            {
                                var msgContent = choices[0].GetProperty("message").GetProperty("content").GetString()?.Trim();
                                if (!string.IsNullOrEmpty(msgContent))
                                {
                                    accumulated.Append(msgContent);
                                    await SendWsJsonAsync(webSocket, new { type = "text_delta", delta = msgContent }, turnToken);
                                }
                            }
                        }
                    }

                    var finalReply = accumulated.ToString().Trim();
                    var cleanTts = CleanTextForSpeech(finalReply);
                    if (!string.IsNullOrEmpty(cleanTts) && !turnToken.IsCancellationRequested)
                    {
                        var (ttsBytes, ttsMime, ttsStatus) = await CallLlm360SpeechAsync(
                            config,
                            cleanTts,
                            ttsModel,
                            voice,
                            "mp3",
                            null,
                            turnToken);
                        if (ttsStatus == 200 && ttsBytes.Length > 0)
                        {
                            await SendWsJsonAsync(webSocket, new
                            {
                                type = "audio_chunk",
                                data = Convert.ToBase64String(ttsBytes),
                                mime_type = ttsMime.Split(';')[0].Trim(),
                                text = finalReply
                            }, turnToken);
                        }
                    }

                    await SendWsJsonAsync(webSocket, new { type = "turn_complete", text = finalReply }, turnToken);
                }
                finally
                {
                    queueManager.SetSessionTurnActive(sessionId, false, agentId);
                }
            }
            catch (OperationCanceledException)
            {
                await SendWsJsonAsync(webSocket, new { type = "interrupted" }, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Realtime voice turn error for agent {AgentId}", agentId);
                await SendWsJsonAsync(webSocket, new { type = "error", message = ex.Message }, CancellationToken.None);
            }
        }

        try
        {
            while (webSocket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                using var frameMs = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await webSocket.ReceiveAsync(new ArraySegment<byte>(recvBuf), ct);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", ct);
                        return;
                    }
                    if (result.Count > 0)
                    {
                        frameMs.Write(recvBuf, 0, result.Count);
                    }
                } while (!result.EndOfMessage);

                if (result.MessageType == WebSocketMessageType.Binary)
                {
                    var bytes = frameMs.ToArray();
                    audioBuffer.Write(bytes, 0, bytes.Length);
                    continue;
                }

                var msgJson = Encoding.UTF8.GetString(frameMs.ToArray());
                if (string.IsNullOrWhiteSpace(msgJson)) continue;

                using var doc = JsonDocument.Parse(msgJson);
                var root = doc.RootElement;
                var msgType = root.TryGetProperty("type", out var tp) ? tp.GetString() ?? "" : "";

                if (msgType == "ping")
                {
                    await SendWsJsonAsync(webSocket, new { type = "pong" }, ct);
                }
                else if (msgType == "config")
                {
                    if (root.TryGetProperty("agent_id", out var ag) && !string.IsNullOrWhiteSpace(ag.GetString()))
                        agentId = ag.GetString()!;
                    if (root.TryGetProperty("session_id", out var sid) && !string.IsNullOrWhiteSpace(sid.GetString()))
                        sessionId = sid.GetString()!;
                    if (root.TryGetProperty("stt_model", out var sm) && !string.IsNullOrWhiteSpace(sm.GetString()))
                        sttModel = sm.GetString()!;
                    if (root.TryGetProperty("tts_model", out var tm) && !string.IsNullOrWhiteSpace(tm.GetString()))
                        ttsModel = tm.GetString()!;
                    if (root.TryGetProperty("voice", out var vc) && !string.IsNullOrWhiteSpace(vc.GetString()))
                        voice = vc.GetString()!;
                    if (root.TryGetProperty("language", out var lg))
                        language = lg.GetString();

                    await SendWsJsonAsync(webSocket, new
                    {
                        type = "configured",
                        agent_id = agentId,
                        session_id = sessionId,
                        stt_model = sttModel,
                        tts_model = ttsModel,
                        voice
                    }, ct);
                }
                else if (msgType is "interrupt" or "stop")
                {
                    audioBuffer.SetLength(0);
                    if (turnCts is not null)
                    {
                        await turnCts.CancelAsync();
                        turnCts.Dispose();
                        turnCts = null;
                    }
                    _ = qwenPawClient.StopChatAsync(new ConsoleChatStopRequestDto(sessionId), ct);
                    await SendWsJsonAsync(webSocket, new { type = "interrupted", session_id = sessionId }, ct);
                }
                else if (msgType == "prompt")
                {
                    var text = root.TryGetProperty("text", out var tx) ? tx.GetString()?.Trim() : null;
                    if (string.IsNullOrEmpty(text)) continue;

                    if (turnCts is not null)
                    {
                        await turnCts.CancelAsync();
                        turnCts.Dispose();
                    }
                    turnCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    _ = RunVoiceAgentTurnAsync(text, turnCts.Token);
                }
                else if (msgType is "audio" or "audio_chunk")
                {
                    if (root.TryGetProperty("data", out var b64Prop) && !string.IsNullOrWhiteSpace(b64Prop.GetString()))
                    {
                        try
                        {
                            var decoded = Convert.FromBase64String(b64Prop.GetString()!);
                            audioBuffer.Write(decoded, 0, decoded.Length);
                        }
                        catch
                        {
                            // Ignore malformed base64 chunk
                        }
                    }

                    var commit = !root.TryGetProperty("commit", out var cmProp) || cmProp.ValueKind != JsonValueKind.False;
                    if (commit && audioBuffer.Length > 0)
                    {
                        if (turnCts is not null)
                        {
                            await turnCts.CancelAsync();
                            turnCts.Dispose();
                            turnCts = null;
                        }

                        var rawAudio = audioBuffer.ToArray();
                        audioBuffer.SetLength(0);

                        var fmt = (root.TryGetProperty("format", out var fmtProp) || root.TryGetProperty("mime_type", out fmtProp)) && !string.IsNullOrWhiteSpace(fmtProp.GetString())
                            ? fmtProp.GetString()!.Trim().ToLowerInvariant()
                            : "webm";
                        var mime = fmt.Contains('/') ? fmt : $"audio/{fmt}";
                        var ext = fmt.Contains('/') ? fmt.Split('/').Last() : fmt;
                        var reqSttModel = root.TryGetProperty("stt_model", out var rsm) && !string.IsNullOrWhiteSpace(rsm.GetString())
                            ? rsm.GetString()!
                            : sttModel;
                        var reqLang = root.TryGetProperty("language", out var rlg) && !string.IsNullOrWhiteSpace(rlg.GetString())
                            ? rlg.GetString()
                            : language;

                        var (sttBody, _, sttStatus) = await CallLlm360TranscribeAsync(
                            config,
                            rawAudio,
                            $"realtime.{ext}",
                            mime,
                            reqSttModel,
                            reqLang,
                            null,
                            "json",
                            ct);

                        if (sttStatus != 200)
                        {
                            await SendWsJsonAsync(webSocket, new
                            {
                                type = "error",
                                message = $"STT failed ({sttStatus}): {Encoding.UTF8.GetString(sttBody)}"
                            }, ct);
                            continue;
                        }

                        var transcript = "";
                        var detectedLang = "";
                        try
                        {
                            using var sttDoc = JsonDocument.Parse(sttBody);
                            if (sttDoc.RootElement.TryGetProperty("text", out var tEl))
                                transcript = tEl.GetString()?.Trim() ?? "";
                            if (sttDoc.RootElement.TryGetProperty("language_code", out var lcEl))
                                detectedLang = lcEl.GetString() ?? "";
                        }
                        catch
                        {
                            transcript = Encoding.UTF8.GetString(sttBody).Trim();
                        }

                        await SendWsJsonAsync(webSocket, new
                        {
                            type = "transcript",
                            text = transcript,
                            language = detectedLang
                        }, ct);

                        if (!string.IsNullOrWhiteSpace(transcript))
                        {
                            turnCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                            _ = RunVoiceAgentTurnAsync(transcript, turnCts.Token);
                        }
                    }
                }
            }
        }
        finally
        {
            if (turnCts is not null)
            {
                await turnCts.CancelAsync();
                turnCts.Dispose();
            }
        }
    }
}
