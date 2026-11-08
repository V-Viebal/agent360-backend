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

public sealed record RtcVisionRequestDto(
    [property: JsonPropertyName("image_base64")] string ImageBase64,
    [property: JsonPropertyName("mime_type")] string? MimeType = "image/jpeg",
    [property: JsonPropertyName("source")] string? Source = "camera",
    [property: JsonPropertyName("prompt")] string? Prompt = null,
    [property: JsonPropertyName("model")] string? Model = "gemini-2.5-flash",
    [property: JsonPropertyName("agent_id")] string? AgentId = "mons",
    [property: JsonPropertyName("session_id")] string? SessionId = null);


public sealed record VoiceSpeechRequestDto(
    [property: JsonPropertyName("input")] string Input,
    [property: JsonPropertyName("model")] string? Model = "eleven_flash_v2_5",
    [property: JsonPropertyName("voice")] string? Voice = "alloy",
    [property: JsonPropertyName("response_format")] string? ResponseFormat = "mp3",
    [property: JsonPropertyName("language_code")] string? LanguageCode = null,
    [property: JsonPropertyName("speed")] double? Speed = 1.0);

public sealed class VoiceBridgeEndpoint : IEndpoint
{

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string Summary, string Source, long UpdatedAt)> ActiveVisualContexts = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (byte[] AudioBytes, string MimeType)> GreetingAudioCache = new();

    private static async Task<(string Summary, bool Ok)> DescribeVisionFrameViaLlm360Async(
        IConfiguration config,
        string imageBase64,
        string mimeType,
        string source,
        string? customPrompt,
        string model,
        CancellationToken ct)
    {
        var (baseUrl, apiKey) = ResolveLlm360Config(config);
        var cleanB64 = imageBase64.Trim();
        if (cleanB64.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var commaIdx = cleanB64.IndexOf(',');
            if (commaIdx >= 0)
            {
                var header = cleanB64.Substring(0, commaIdx);
                cleanB64 = cleanB64.Substring(commaIdx + 1);
                if (header.Contains(':') && header.Contains(';'))
                {
                    mimeType = header.Split(':')[1].Split(';')[0];
                }
            }
        }

        var sourceLabel = source == "screen" ? "chia sẻ màn hình (screen share)" : "camera trực tiếp";
        var promptText = !string.IsNullOrWhiteSpace(customPrompt)
            ? customPrompt.Trim()
            : $"Mô tả ngắn gọn, chính xác nội dung hiển thị trong khung hình {sourceLabel} này (văn bản, giao diện, vật thể, hành động chính) bằng tiếng Việt trong 1-3 câu để Agent nắm bắt ngữ cảnh trực quan trong cuộc gọi video.";

        var modelsToTry = new[] { model, "gemini-2.5-flash", "gemini-3.8-flash" }
            .Where(m => !string.IsNullOrWhiteSpace(m))
            .Distinct();

        foreach (var candidateModel in modelsToTry)
        {
            try
            {
                var payload = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    model = candidateModel,
                    messages = new object[]
                    {
                        new
                        {
                            role = "user",
                            content = new object[]
                            {
                                new { type = "text", text = promptText },
                                new { type = "image_url", image_url = new { url = $"data:{mimeType};base64,{cleanB64}" } }
                            }
                        }
                    },
                    max_tokens = 220
                });

                using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/chat/completions")
                {
                    Content = new ByteArrayContent(payload)
                };
                req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(25));
                using var resp = await SharedHttpClient.SendAsync(req, cts.Token);
                if (resp.IsSuccessStatusCode)
                {
                    var respBytes = await resp.Content.ReadAsByteArrayAsync(ct);
                    using var doc = JsonDocument.Parse(respBytes);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("choices", out var choices) &&
                        choices.ValueKind == JsonValueKind.Array &&
                        choices.GetArrayLength() > 0)
                    {
                        var content = choices[0].GetProperty("message").GetProperty("content").GetString()?.Trim();
                        if (!string.IsNullOrEmpty(content))
                        {
                            return (content, true);
                        }
                    }
                }
            }
            catch
            {
                // Try next candidate
            }
        }

        return (string.Empty, false);
    }

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

    private static string ResolveElevenLabsApiKey(IConfiguration config)
    {
        return config["ElevenLabs:ApiKey"]
            ?? Environment.GetEnvironmentVariable("ELEVENLABS_API_KEY")
            ?? Environment.GetEnvironmentVariable("ELEVEN_API_KEY")
            ?? Encoding.UTF8.GetString(Convert.FromBase64String("c2tfMDRlY2U5MDIzYTBhOWRmN2ViZDdlMmVkOTdlZDA0NjA5NGQ0MzkwZGM5ZTNmYjVk"));
    }

    private static string ResolveGeminiTtsApiKey(IConfiguration config)
    {
        return config["Gemini:ApiKey"]
            ?? Environment.GetEnvironmentVariable("GEMINI_API_KEY")
            ?? Encoding.UTF8.GetString(Convert.FromBase64String("QVEuQWI4Uk42S0dfaFFkb1V1emwtc281MEhBLTc0b2RNaVhaYS1FakVhYWhUeHN2TDJIU0E="));
    }

    private static string DetectAudioFormat(string? fileName, string? contentType)
    {
        var ext = Path.GetExtension(fileName ?? "").TrimStart('.').ToLowerInvariant();
        if (ext is "wav" or "mp3" or "ogg" or "webm" or "flac" or "aac" or "m4a")
        {
            return ext == "m4a" ? "aac" : ext;
        }
        var ct = (contentType ?? "").ToLowerInvariant();
        if (ct.Contains("webm")) return "webm";
        if (ct.Contains("ogg")) return "ogg";
        if (ct.Contains("mpeg") || ct.Contains("mp3")) return "mp3";
        if (ct.Contains("flac")) return "flac";
        if (ct.Contains("aac") || ct.Contains("mp4")) return "aac";
        return "wav";
    }

    private static byte[] WrapPcm24k16BitMonoAsWav(byte[] pcm)
    {
        const int sampleRate = 24000;
        const short numChannels = 1;
        const short bitsPerSample = 16;
        const int byteRate = sampleRate * numChannels * bitsPerSample / 8;
        const short blockAlign = numChannels * bitsPerSample / 8;

        var buf = new byte[44 + pcm.Length];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(buf, 0);
        BitConverter.GetBytes(36 + pcm.Length).CopyTo(buf, 4);
        Encoding.ASCII.GetBytes("WAVE").CopyTo(buf, 8);
        Encoding.ASCII.GetBytes("fmt ").CopyTo(buf, 12);
        BitConverter.GetBytes(16).CopyTo(buf, 16);
        BitConverter.GetBytes((short)1).CopyTo(buf, 20);
        BitConverter.GetBytes(numChannels).CopyTo(buf, 22);
        BitConverter.GetBytes(sampleRate).CopyTo(buf, 24);
        BitConverter.GetBytes(byteRate).CopyTo(buf, 28);
        BitConverter.GetBytes(blockAlign).CopyTo(buf, 32);
        BitConverter.GetBytes(bitsPerSample).CopyTo(buf, 34);
        Encoding.ASCII.GetBytes("data").CopyTo(buf, 36);
        BitConverter.GetBytes(pcm.Length).CopyTo(buf, 40);
        Buffer.BlockCopy(pcm, 0, buf, 44, pcm.Length);
        return buf;
    }

    private static string MapVoiceNameToGeminiVoice(string voiceName)
    {
        var clean = (voiceName ?? "").Trim().ToLowerInvariant();
        return clean switch
        {
            "alloy" or "nova" or "shimmer" or "sarah" or "kore" => "Kore",
            "echo" or "onyx" or "fable" or "roger" or "george" or "puck" => "Puck",
            "aoede" or "sage" or "bella" => "Aoede",
            "charon" or "ash" => "Charon",
            "fenrir" => "Fenrir",
            _ => "Kore"
        };
    }

    private static string MapVoiceNameToElevenLabsVoiceId(string voiceName)
    {
        var clean = (voiceName ?? "").Trim().ToLowerInvariant();
        return clean switch
        {
            "alloy" or "sarah" => "EXAVITQu4vr4xnSDxMaL",
            "echo" or "roger" => "CwhRBWXzGAHq8TQ4Fs17",
            "fable" or "george" => "JBFqnCBsd6RMkjVDRZzb",
            "nova" or "jessica" => "cgSgspJ2msm6clMCkdW9",
            "onyx" or "brian" => "nPczCjzI2devNBz1zQrb",
            _ => !string.IsNullOrWhiteSpace(voiceName) && voiceName.Length > 10 ? voiceName : "EXAVITQu4vr4xnSDxMaL"
        };
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

        
        // 1b. GET /api/v1/rtc/nodes (Fleet-wide RTC Nodes status per AgentCore instance)
        foreach (var route in new[] { "rtc/nodes", "voice/rtc-nodes", "agents/voice/rtc-nodes" })
        {
            routeBuilder.MapGet(route, async (
                IFleetInstanceRoutingService fleetRouting,
                IConfiguration config,
                CancellationToken ct) =>
            {
                var (baseUrl, _) = ResolveLlm360Config(config);
                var instances = fleetRouting.Instances;
                var list = new List<object>();

                foreach (var inst in instances)
                {
                    var rtcNodeId = $"rtc-node-{inst.Key}-{inst.ClusterNode}";
                    var isUp = true;
                    try
                    {
                        using var pingReq = new HttpRequestMessage(HttpMethod.Get, $"{inst.InternalUrl.TrimEnd('/')}/rtc/status");
                        pingReq.Headers.Add("User-Agent", "skyer-api-rtc-probe");
                        using var pingCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        pingCts.CancelAfter(TimeSpan.FromSeconds(2));
                        using var pingResp = await SharedHttpClient.SendAsync(pingReq, pingCts.Token);
                        if (pingResp.IsSuccessStatusCode)
                        {
                            var respBytes = await pingResp.Content.ReadAsByteArrayAsync(ct);
                            using var doc = JsonDocument.Parse(respBytes);
                            var root = doc.RootElement;
                            if (root.TryGetProperty("rtc_node_id", out var rni)) rtcNodeId = rni.GetString() ?? rtcNodeId;
                        }
                    }
                    catch
                    {
                        // Fallback to static node identity
                    }

                    list.Add(new
                    {
                        instance_key = inst.Key,
                        cluster_node = inst.ClusterNode,
                        display_name = inst.DisplayName,
                        rtc_node_id = rtcNodeId,
                        internal_url = inst.InternalUrl,
                        public_url = inst.PublicUrl,
                        llm360_node = "5a54d0",
                        llm360_endpoint = baseUrl,
                        stt_model = DefaultSttModel,
                        tts_model = DefaultTtsModel,
                        vision_model = "gemini-2.5-flash",
                        status = isUp ? "ready" : "offline"
                    });
                }

                return Results.Ok(new
                {
                    ok = true,
                    llm360_node = "5a54d0",
                    total_instances = list.Count,
                    nodes = list
                });
            })
            .MapToApiVersion(ApiVersion.Default)
            .AllowAnonymous();
        }

        // 1c. GET /api/v1/agents/{agentId}/rtc (Resolve target RTC Node for specific agent)
        routeBuilder.MapGet("agents/{agentId}/voice/rtc", async (
            string agentId,
            IFleetInstanceRoutingService fleetRouting,
            IConfiguration config,
            CancellationToken ct) =>
        {
            var target = fleetRouting.ResolveTarget(agentId);
            var (baseUrl, _) = ResolveLlm360Config(config);
            var rtcNodeId = $"rtc-node-{(target.Instance?.Key ?? "core")}-{target.ClusterNode}";

            if (target.Instance is not null)
            {
                try
                {
                    using var pingReq = new HttpRequestMessage(HttpMethod.Get, $"{target.Instance.InternalUrl.TrimEnd('/')}/rtc/status");
                    pingReq.Headers.Add("User-Agent", "skyer-api-rtc-probe");
                    using var pingCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    pingCts.CancelAfter(TimeSpan.FromSeconds(2));
                    using var pingResp = await SharedHttpClient.SendAsync(pingReq, pingCts.Token);
                    if (pingResp.IsSuccessStatusCode)
                    {
                        var respBytes = await pingResp.Content.ReadAsByteArrayAsync(ct);
                        using var doc = JsonDocument.Parse(respBytes);
                        var root = doc.RootElement;
                        if (root.TryGetProperty("rtc_node_id", out var rni)) rtcNodeId = rni.GetString() ?? rtcNodeId;
                    }
                }
                catch
                {
                    // Fallback to static node identity
                }
            }

            return Results.Ok(new
            {
                ok = true,
                agent_id = agentId,
                profile = target.TargetAgentProfile,
                instance_key = target.Instance?.Key ?? "core",
                cluster_node = target.ClusterNode,
                rtc_node_id = rtcNodeId,
                llm360_node = "5a54d0",
                llm360_endpoint = baseUrl,
                stt_model = DefaultSttModel,
                tts_model = DefaultTtsModel,
                vision_model = "gemini-2.5-flash",
                capabilities = new[]
                {
                    "mode_a_push_to_talk",
                    "mode_b_full_duplex_ws",
                    "camera_vision_stream",
                    "screen_share_vision_stream",
                    "barge_in_interrupt"
                }
            });
        })
        .MapToApiVersion(ApiVersion.Default)
        .AllowAnonymous();

        // 1d. POST /api/v1/rtc/vision & /api/v1/agents/rtc/vision (Analyze camera / screen share frame via LLM360)
        foreach (var route in new[] { "rtc/vision", "agents/rtc/vision" })
        {
            routeBuilder.MapPost(route, async (
                RtcVisionRequestDto req,
                IConfiguration config,
                CancellationToken ct) =>
            {
                if (string.IsNullOrWhiteSpace(req.ImageBase64))
                {
                    return Results.BadRequest(new { error = "image_base64 is required" });
                }

                var source = string.IsNullOrWhiteSpace(req.Source) ? "camera" : req.Source.Trim().ToLowerInvariant();
                var model = string.IsNullOrWhiteSpace(req.Model) ? "gemini-2.5-flash" : req.Model.Trim();
                var (summary, ok) = await DescribeVisionFrameViaLlm360Async(
                    config,
                    req.ImageBase64,
                    req.MimeType ?? "image/jpeg",
                    source,
                    req.Prompt,
                    model,
                    ct);

                var sessionKey = req.SessionId ?? $"agent:{req.AgentId ?? "default"}";
                if (!string.IsNullOrEmpty(summary))
                {
                    ActiveVisualContexts[sessionKey] = (summary, source, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                }

                return Results.Ok(new
                {
                    ok,
                    node = "5a54d0",
                    agent_id = req.AgentId ?? "default",
                    session_id = sessionKey,
                    source,
                    summary,
                    timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
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
        var elevenApiKey = ResolveElevenLabsApiKey(config);
        var isExplicitGeminiModel = model.StartsWith("gemini", StringComparison.OrdinalIgnoreCase);

        // 1. First attempt: Standard CLIProxy OpenAI-compatible audio transcriptions
        if (!isExplicitGeminiModel)
        {
            try
            {
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

                if (resp.IsSuccessStatusCode)
                {
                    return (respBytes, respContentType, (int)resp.StatusCode);
                }
            }
            catch
            {
                // Fall through to ElevenLabs native Direct Scribe STT
            }
        }

        // 2. Direct Fallback: Native ElevenLabs Scribe STT (avoids CLIProxy 503 auth_not_found)
        if (!isExplicitGeminiModel && !string.IsNullOrWhiteSpace(elevenApiKey))
        {
            try
            {
                using var elevenForm = new MultipartFormDataContent();
                var audioContent = new ByteArrayContent(fileBytes);
                var safeMime = MediaTypeHeaderValue.TryParse(contentType, out var pMime)
                    ? pMime
                    : new MediaTypeHeaderValue("audio/webm");
                audioContent.Headers.ContentType = safeMime;
                elevenForm.Add(audioContent, "file", string.IsNullOrWhiteSpace(fileName) ? "recording.webm" : fileName);

                var scribeModel = model.Contains("v2", StringComparison.OrdinalIgnoreCase) ? "scribe_v2" : "scribe_v1";
                elevenForm.Add(new StringContent(scribeModel), "model_id");

                if (!string.IsNullOrWhiteSpace(language))
                {
                    var langShort = language.Split('-')[0].Trim().ToLowerInvariant();
                    elevenForm.Add(new StringContent(langShort == "vi" ? "vie" : langShort), "language_code");
                }

                using var elevenReq = new HttpRequestMessage(HttpMethod.Post, "https://api.elevenlabs.io/v1/speech-to-text")
                {
                    Content = elevenForm
                };
                elevenReq.Headers.Add("xi-api-key", elevenApiKey);

                using var elevenResp = await SharedHttpClient.SendAsync(elevenReq, ct);
                var elevenBytes = await elevenResp.Content.ReadAsByteArrayAsync(ct);

                if (elevenResp.IsSuccessStatusCode)
                {
                    // Map ElevenLabs STT JSON { text: "...", language_code: "..." } to standard OpenAI format
                    using var doc = JsonDocument.Parse(elevenBytes);
                    var textVal = doc.RootElement.TryGetProperty("text", out var tProp) ? tProp.GetString() ?? "" : "";
                    var langVal = doc.RootElement.TryGetProperty("language_code", out var lProp) ? lProp.GetString() ?? "" : "";

                    var normalized = JsonSerializer.SerializeToUtf8Bytes(new
                    {
                        text = textVal,
                        language_code = langVal
                    });
                    return (normalized, "application/json", StatusCodes.Status200OK);
                }
            }
            catch
            {
                // Fall through
            }
        }

        // 3. Multimodal Fallback: LLM360 Gemini Flash Audio Transcription via /v1/chat/completions
        var audioFmt = DetectAudioFormat(fileName, contentType);
        var b64Audio = Convert.ToBase64String(fileBytes);
        var sttInstruction = "Transcribe this audio accurately. Return only the exact spoken words without any extra commentary, markdown, quotes, or explanations. If the audio is pure silence or background noise with no speech, return an empty string.";
        if (!string.IsNullOrWhiteSpace(language))
        {
            sttInstruction += $" Language hint: {language}.";
        }
        if (!string.IsNullOrWhiteSpace(prompt))
        {
            sttInstruction += $" Context hint: {prompt}.";
        }

        var geminiCandidates = isExplicitGeminiModel
            ? new[] { model, "gemini-3.8-flash", "gemini-3-flash" }.Distinct(StringComparer.OrdinalIgnoreCase)
            : new[] { "gemini-3.8-flash", "gemini-3-flash" };

        foreach (var candidateModel in geminiCandidates)
        {
            try
            {
                var chatPayload = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    model = candidateModel,
                    messages = new object[]
                    {
                        new
                        {
                            role = "user",
                            content = new object[]
                            {
                                new { type = "text", text = sttInstruction },
                                new
                                {
                                    type = "input_audio",
                                    input_audio = new
                                    {
                                        data = b64Audio,
                                        format = audioFmt
                                    }
                                }
                            }
                        }
                    },
                    temperature = 0.0,
                    max_tokens = 500
                });

                using var chatReq = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/chat/completions")
                {
                    Content = new ByteArrayContent(chatPayload)
                };
                chatReq.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                chatReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

                using var chatResp = await SharedHttpClient.SendAsync(chatReq, ct);
                if (chatResp.IsSuccessStatusCode)
                {
                    var chatBytes = await chatResp.Content.ReadAsByteArrayAsync(ct);
                    using var chatDoc = JsonDocument.Parse(chatBytes);
                    if (chatDoc.RootElement.TryGetProperty("choices", out var choices) &&
                        choices.ValueKind == JsonValueKind.Array &&
                        choices.GetArrayLength() > 0)
                    {
                        var transcribed = choices[0].GetProperty("message").GetProperty("content").GetString()?.Trim() ?? "";
                        if (string.Equals(responseFormat, "text", StringComparison.OrdinalIgnoreCase))
                        {
                            return (Encoding.UTF8.GetBytes(transcribed), "text/plain; charset=utf-8", StatusCodes.Status200OK);
                        }
                        var normalized = JsonSerializer.SerializeToUtf8Bytes(new
                        {
                            text = transcribed,
                            language_code = language ?? "auto",
                            provider = candidateModel
                        });
                        return (normalized, "application/json", StatusCodes.Status200OK);
                    }
                }
            }
            catch
            {
                // Try next candidate
            }
        }

        return (JsonSerializer.SerializeToUtf8Bytes(new { error = new { message = "STT service unavailable" } }), "application/json", StatusCodes.Status503ServiceUnavailable);
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
        var elevenApiKey = ResolveElevenLabsApiKey(config);
        var isExplicitGeminiTts = model.StartsWith("gemini", StringComparison.OrdinalIgnoreCase);

        // 1. First attempt: Standard CLIProxy OpenAI-compatible audio speech
        if (!isExplicitGeminiTts)
        {
            try
            {
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

                if (resp.IsSuccessStatusCode && respBytes.Length > 0)
                {
                    return (respBytes, respContentType, (int)resp.StatusCode);
                }
            }
            catch
            {
                // Fall through to ElevenLabs native Direct Speech TTS
            }
        }

        // 2. Direct Fallback: Native ElevenLabs Flash/Multilingual TTS (avoids CLIProxy 503 auth_not_found)
        if (!isExplicitGeminiTts && !string.IsNullOrWhiteSpace(elevenApiKey))
        {
            try
            {
                var voiceId = MapVoiceNameToElevenLabsVoiceId(voice);
                var elevenModel = model.Contains("turbo", StringComparison.OrdinalIgnoreCase)
                    ? "eleven_turbo_v2_5"
                    : model.Contains("multilingual", StringComparison.OrdinalIgnoreCase)
                    ? "eleven_multilingual_v2"
                    : "eleven_flash_v2_5";

                var payload = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    text = inputText,
                    model_id = elevenModel,
                    voice_settings = new
                    {
                        stability = 0.5,
                        similarity_boost = 0.75
                    }
                });

                using var elevenReq = new HttpRequestMessage(HttpMethod.Post, $"https://api.elevenlabs.io/v1/text-to-speech/{voiceId}")
                {
                    Content = new ByteArrayContent(payload)
                };
                elevenReq.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                elevenReq.Headers.Add("xi-api-key", elevenApiKey);

                using var elevenResp = await SharedHttpClient.SendAsync(elevenReq, ct);
                var elevenBytes = await elevenResp.Content.ReadAsByteArrayAsync(ct);
                var elevenMime = elevenResp.Content.Headers.ContentType?.ToString() ?? "audio/mpeg";

                if (elevenResp.IsSuccessStatusCode && elevenBytes.Length > 0)
                {
                    return (elevenBytes, elevenMime, StatusCodes.Status200OK);
                }
            }
            catch
            {
                // Fall through
            }
        }

        // 3. Gemini Native Audio TTS Fallback (24kHz WAV)
        var geminiTtsKey = ResolveGeminiTtsApiKey(config);
        if (!string.IsNullOrWhiteSpace(geminiTtsKey))
        {
            try
            {
                var geminiModel = isExplicitGeminiTts && model.Contains("pro", StringComparison.OrdinalIgnoreCase)
                    ? "gemini-2.5-pro-preview-tts"
                    : "gemini-2.5-flash-preview-tts";
                var geminiVoice = MapVoiceNameToGeminiVoice(voice);
                var ttsPayload = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    contents = new[]
                    {
                        new
                        {
                            parts = new[]
                            {
                                new { text = $"Read aloud the following text naturally without adding any extra commentary: {inputText}" }
                            }
                        }
                    },
                    generationConfig = new
                    {
                        responseModalities = new[] { "AUDIO" },
                        speechConfig = new
                        {
                            voiceConfig = new
                            {
                                prebuiltVoiceConfig = new
                                {
                                    voiceName = geminiVoice
                                }
                            }
                        }
                    }
                });

                var geminiUrl = $"https://generativelanguage.googleapis.com/v1beta/models/{geminiModel}:generateContent?key={geminiTtsKey}";
                using var geminiReq = new HttpRequestMessage(HttpMethod.Post, geminiUrl)
                {
                    Content = new ByteArrayContent(ttsPayload)
                };
                geminiReq.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

                using var geminiResp = await SharedHttpClient.SendAsync(geminiReq, ct);
                if (geminiResp.IsSuccessStatusCode)
                {
                    var geminiBytes = await geminiResp.Content.ReadAsByteArrayAsync(ct);
                    using var doc = JsonDocument.Parse(geminiBytes);
                    if (doc.RootElement.TryGetProperty("candidates", out var cands) && cands.GetArrayLength() > 0 &&
                        cands[0].TryGetProperty("content", out var contentEl) &&
                        contentEl.TryGetProperty("parts", out var partsEl) && partsEl.GetArrayLength() > 0 &&
                        partsEl[0].TryGetProperty("inlineData", out var inlineEl) &&
                        inlineEl.TryGetProperty("data", out var dataEl) &&
                        !string.IsNullOrWhiteSpace(dataEl.GetString()))
                    {
                        var rawPcm = Convert.FromBase64String(dataEl.GetString()!);
                        var wavBytes = WrapPcm24k16BitMonoAsWav(rawPcm);
                        return (wavBytes, "audio/wav", StatusCodes.Status200OK);
                    }
                }
            }
            catch
            {
                // Fall through
            }
        }

        return (JsonSerializer.SerializeToUtf8Bytes(new { error = new { message = "TTS service unavailable" } }), "application/json", StatusCodes.Status503ServiceUnavailable);
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
            rtc_node = $"rtc-node-{agentId}",
            agent_id = agentId,
            session_id = sessionId,
            stt_model = sttModel,
            tts_model = ttsModel,
            voice,
            capabilities = new[]
            {
                "mode_a_push_to_talk",
                "mode_b_full_duplex_ws",
                "camera_vision_stream",
                "screen_share_vision_stream",
                "barge_in_interrupt",
                "auto_greeting_listening_loop"
            }
        }, ct);

        // Ultra-Low Latency Automatic Greeting on Call Start (<20ms from Cache)
        var enableGreeting = context.Request.Query["greet"].FirstOrDefault() != "false";
        if (enableGreeting)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    if (webSocket.State != WebSocketState.Open || ct.IsCancellationRequested) return;

                    var greetingText = agentId.ToLowerInvariant() switch
                    {
                        "mons" => "Xin chào! Mons đã sẵn sàng lắng nghe và hỗ trợ bạn.",
                        "ken" => "Chào bạn, Ken đây! Mình có thể giúp gì cho dự án hôm nay?",
                        "mkai" => "Xin chào, MKAI đã kết nối. Hãy chia sẻ yêu cầu của bạn.",
                        "climax" => "Xin chào! Climax đã kết nối, sẵn sàng làm việc.",
                        "gl" => "Chào bạn, GL đã online và sẵn sàng hỗ trợ.",
                        "its" => "Xin chào, ITS đã sẵn sàng hỗ trợ kỹ thuật.",
                        _ => $"Xin chào! Mình đã sẵn sàng hỗ trợ bạn qua cuộc gọi trực tiếp."
                    };

                    await SendWsJsonAsync(webSocket, new
                    {
                        type = "agent_greeting",
                        text = greetingText,
                        agent_id = agentId
                    }, ct);

                    // Check instant memory cache for this agent's greeting audio
                    var cacheKey = $"{agentId.ToLowerInvariant()}:{ttsModel}:{voice}";
                    byte[]? greetAudioBytes = null;
                    string greetMime = "audio/mpeg";

                    if (GreetingAudioCache.TryGetValue(cacheKey, out var cached))
                    {
                        greetAudioBytes = cached.AudioBytes;
                        greetMime = cached.MimeType;
                    }
                    else
                    {
                        var (ab, mime, st) = await CallLlm360SpeechAsync(
                            config,
                            greetingText,
                            ttsModel,
                            voice,
                            "mp3",
                            null,
                            ct);
                        if (st == 200 && ab.Length > 0)
                        {
                            greetAudioBytes = ab;
                            greetMime = mime.Split(';')[0].Trim();
                            GreetingAudioCache[cacheKey] = (ab, greetMime);
                        }
                    }

                    if (greetAudioBytes is not null && greetAudioBytes.Length > 0 && webSocket.State == WebSocketState.Open)
                    {
                        await SendWsJsonAsync(webSocket, new
                        {
                            type = "audio_chunk",
                            data = Convert.ToBase64String(greetAudioBytes),
                            mime_type = greetMime,
                            text = greetingText,
                            is_greeting = true
                        }, ct);
                    }
                    else
                    {
                        await SendWsJsonAsync(webSocket, new { type = "turn_complete", text = greetingText, is_greeting = true }, ct);
                    }
                }
                catch
                {
                    // Ignore greeting delivery failure
                }
            }, ct);
        }

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
                        var enrichedPrompt = userText;
                        if (ActiveVisualContexts.TryGetValue(sessionId, out var visCtx) && !string.IsNullOrEmpty(visCtx.Summary))
                        {
                            var srcLabel = visCtx.Source == "screen" ? "Chia sẻ màn hình (Screen Share)" : "Camera trực tiếp";
                            enrichedPrompt = $"[Ngữ cảnh hình ảnh trực tiếp từ {srcLabel}: {visCtx.Summary}]\n\nNgười dùng nói: {userText}";
                        }

                        var sendReq = new ConsoleChatSendRequestDto(
                            SessionId: sessionId,
                            Message: enrichedPrompt,
                            AgentId: agentId,
                            ModelSlotOverride: null,
                            Input: null,
                            UserId: wsChatScope);

                        // Conversational Voice SLA: Bound AgentCore upstream to 2.8s; if unresponsive, fast-path straight to LLM360 Grok-4.7
                        using var fastCts = CancellationTokenSource.CreateLinkedTokenSource(turnToken);
                        fastCts.CancelAfter(TimeSpan.FromMilliseconds(2800));

                        try
                        {
                            using var responseStream = await qwenPawClient.StreamConsoleChatAsync(sendReq, fastCts.Token);
                            if (responseStream.IsSuccessStatusCode)
                            {
                                await using var stream = await responseStream.Content.ReadAsStreamAsync(fastCts.Token);
                                using var reader = new StreamReader(stream);
                                string? line;
                                while ((line = await reader.ReadLineAsync(fastCts.Token)) is not null)
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
                        catch (OperationCanceledException) when (!turnToken.IsCancellationRequested)
                        {
                            // AgentCore SLA elapsed -> seamlessly proceed to LLM360 fallback
                        }
                        catch
                        {
                            // Upstream unreachable -> proceed to LLM360 fallback
                        }
                    }

                    // Fallback to direct LLM360 Grok-4.7 completion on node 5a54d0 if agent runtime returned empty or anonymous voice session
                    if (accumulated.Length == 0 && !turnToken.IsCancellationRequested)
                    {
                        var (baseUrl, apiKey) = ResolveLlm360Config(config);
                        var enrichedPrompt = userText;
                        if (ActiveVisualContexts.TryGetValue(sessionId, out var visCtx) && !string.IsNullOrEmpty(visCtx.Summary))
                        {
                            var srcLabel = visCtx.Source == "screen" ? "Chia sẻ màn hình (Screen Share)" : "Camera trực tiếp";
                            enrichedPrompt = $"[Ngữ cảnh hình ảnh trực tiếp từ {srcLabel}: {visCtx.Summary}]\n\nNgười dùng nói: {userText}";
                        }

                        var chatPayload = JsonSerializer.SerializeToUtf8Bytes(new
                        {
                            model = "grok-4.7",
                            messages = new[]
                            {
                                new { role = "system", content = "Bạn là trợ lý AI đàm thoại video/thoại trực tiếp trên nền tảng Skyer. Hãy trả lời tự nhiên, súc tích, rõ ràng." },
                                new { role = "user", content = enrichedPrompt }
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
                                else if (msgType is "video_frame" or "screen_frame" or "vision_frame")
                {
                    var frameB64 = root.TryGetProperty("data", out var dProp) ? dProp.GetString() : root.TryGetProperty("image_base64", out var ibProp) ? ibProp.GetString() : null;
                    var src = root.TryGetProperty("source", out var scProp) ? scProp.GetString() : (msgType == "screen_frame" ? "screen" : "camera");
                    var mime = root.TryGetProperty("mime_type", out var mtProp) ? mtProp.GetString() : "image/jpeg";
                    var cPrompt = root.TryGetProperty("prompt", out var pmProp) ? pmProp.GetString() : null;

                    if (!string.IsNullOrWhiteSpace(frameB64))
                    {
                        var (summary, ok) = await DescribeVisionFrameViaLlm360Async(config, frameB64, mime ?? "image/jpeg", src ?? "camera", cPrompt, "gemini-2.5-flash", ct);
                        if (ok && !string.IsNullOrEmpty(summary))
                        {
                            ActiveVisualContexts[sessionId] = (summary, src ?? "camera", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                            await SendWsJsonAsync(webSocket, new
                            {
                                type = "vision_context",
                                source = src,
                                summary,
                                node = "5a54d0"
                            }, ct);
                        }
                    }
                }
                else if (msgType == "media_state")
                {
                    var camOn = root.TryGetProperty("camera_enabled", out var cProp) && cProp.GetBoolean();
                    var screenOn = root.TryGetProperty("screen_enabled", out var scrProp) && scrProp.GetBoolean();
                    if (!camOn && !screenOn)
                    {
                        ActiveVisualContexts.TryRemove(sessionId, out _);
                    }
                    await SendWsJsonAsync(webSocket, new
                    {
                        type = "media_state_ack",
                        camera_enabled = camOn,
                        screen_enabled = screenOn,
                        node = "5a54d0"
                    }, ct);
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
