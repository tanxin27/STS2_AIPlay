using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading.Tasks;

namespace STS2_AIPlay.Llm;

public class LlmClient
{
    private readonly HttpClient _http;
    private readonly string _endpoint;
    private readonly string _model;
    private readonly bool _thinking;
    private readonly int _thinkingBudget;
    private readonly List<Message> _history = new();
    private readonly List<List<Message>> _allRuns = new();
    private string _logPath = "";
    private string _memoryPath = "";
    private string _historyPath = "";
    private string _character = "UNKNOWN";
    private readonly string _sessionTimestamp;
    private readonly string _asmDir;
    
    // Two-tier memory system:
    // 1. Global memory: shared across all characters (enemy tactics, map strategies, events)
    // 2. Character memory: specific to current character (deck archetypes, card evaluations)
    private string _globalMemory = "";
    private string _characterMemory = "";
    private string _globalMemoryPath = "";

    public string LogPath => _logPath;
    public string Model => _model;
    public string Memory => _globalMemory + "\n\n" + _characterMemory;
    public string Character => _character;

    public LlmClient(LlmConfig config)
    {
        _http = new HttpClient();
        _http.DefaultRequestHeaders.Add("Authorization", $"Bearer {config.Key}");
        _http.Timeout = TimeSpan.FromSeconds(120);

        var url = config.Url.TrimEnd('/');
        if (!url.EndsWith("/chat/completions"))
            url += "/chat/completions";
        _endpoint = url;
        _model = config.Model;
        _thinking = config.Thinking;
        _thinkingBudget = config.ThinkingBudget;

        // Set up base paths
        _asmDir = Path.GetDirectoryName(
            System.Reflection.Assembly.GetExecutingAssembly().Location) ?? ".";
        _sessionTimestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        
        // Set up global memory path (shared across all characters)
        var globalMemoriesDir = Path.Combine(_asmDir, "memories", "global");
        Directory.CreateDirectory(globalMemoriesDir);
        _globalMemoryPath = Path.Combine(globalMemoriesDir, $"llm_memory_global_{_sessionTimestamp}.md");
        
        // Load existing global memory
        LoadGlobalMemory();
        
        // Initialize with default paths (will be updated when character is set)
        UpdatePathsForCharacter("UNKNOWN");

        LogToFile($"\n=== LLM Session Started {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
        LogToFile($"Endpoint: {_endpoint}");
        LogToFile($"Model: {_model}");
        LogToFile($"Character: {_character}");
        LogToFile($"Global Memory: {_globalMemoryPath}");
        LogToFile($"Character Memory: {_memoryPath}");
        LogToFile($"\n[SYSTEM PROMPT]\n{BuildSystemPrompt()}\n");
    }

    /// <summary>
    /// Update file paths when character is determined. This enables per-character memory isolation.
    /// Global memory (enemy tactics, map strategies) is shared across all characters.
    /// Character memory (deck archetypes, card evaluations) is specific to the current character.
    /// </summary>
    public void SetCharacter(string characterId)
    {
        if (string.IsNullOrEmpty(characterId) || characterId == _character)
            return;
        
        // Save current character's memory if needed
        if (!string.IsNullOrEmpty(_characterMemory) && _character != "UNKNOWN")
        {
            SaveCharacterMemory(_characterMemory);
        }
        
        _character = characterId.ToUpperInvariant();
        UpdatePathsForCharacter(_character);
        
        // Try to load existing character-specific memory
        LoadExistingMemoryForCharacter();
        
        LogToFile($"\n=== Character Changed: {_character} ===");
        LogToFile($"Character Memory Path: {_memoryPath}");
        LogToFile($"Global Memory Path: {_globalMemoryPath}");
        MainFile.Logger.Info($"[AutoSlay/LLM] Character set to {_character}, global memory shared, character memory isolated");
    }
    
    /// <summary>
    /// Load global memory (shared across all characters) from the most recent global memory file.
    /// </summary>
    private void LoadGlobalMemory()
    {
        try
        {
            var globalDir = Path.Combine(_asmDir, "memories", "global");
            if (!Directory.Exists(globalDir))
                return;
                
            var files = Directory.GetFiles(globalDir, "llm_memory_global_*.md");
            if (files.Length > 0)
            {
                Array.Sort(files, (a, b) => File.GetLastWriteTime(b).CompareTo(File.GetLastWriteTime(a)));
                _globalMemory = File.ReadAllText(files[0]);
                MainFile.Logger.Info($"[AutoSlay/LLM] Loaded global memory from {files[0]}");
            }
        }
        catch (Exception ex)
        {
            MainFile.Logger.Info($"[AutoSlay/LLM] No existing global memory: {ex.Message}");
        }
    }

    private void UpdatePathsForCharacter(string character)
    {
        // Create organized directory structure:
        // ModFolder/
        //   logs/
        //     IRONCLAD/
        //       llm_log_IRONCLAD_20240322_123456.txt
        //       llm_history_IRONCLAD_20240322_123456.json
        //     SILENT/
        //       ...
        //   memories/
        //     IRONCLAD/
        //       llm_memory_IRONCLAD_20240322_123456.md
        //     SILENT/
        //       ...
        
        // Logs directory: logs/{character}/
        var logsDir = Path.Combine(_asmDir, "logs", character);
        Directory.CreateDirectory(logsDir);
        _logPath = Path.Combine(logsDir, $"llm_log_{character}_{_sessionTimestamp}.txt");
        _historyPath = Path.Combine(logsDir, $"llm_history_{character}_{_sessionTimestamp}.json");
        
        // Memories directory: memories/{character}/
        var memoriesDir = Path.Combine(_asmDir, "memories", character);
        Directory.CreateDirectory(memoriesDir);
        _memoryPath = Path.Combine(memoriesDir, $"llm_memory_{character}_{_sessionTimestamp}.md");
    }

    /// <summary>
    /// Load existing memory file for the current character if available.
    /// Looks for the most recent memory file in memories/{character}/ directory.
    /// </summary>
    private void LoadExistingMemoryForCharacter()
    {
        try
        {
            // Look in memories/{character}/ directory
            var memoriesDir = Path.Combine(_asmDir, "memories", _character);
            if (!Directory.Exists(memoriesDir))
                return;
            
            var pattern = $"llm_memory_{_character}_*.md";
            var files = Directory.GetFiles(memoriesDir, pattern);
            
            if (files.Length > 0)
            {
                // Sort by timestamp (newest first) and load the most recent
                Array.Sort(files, (a, b) => File.GetLastWriteTime(b).CompareTo(File.GetLastWriteTime(a)));
                var mostRecent = files[0];
                
                _characterMemory = File.ReadAllText(mostRecent);
                LogToFile($"[MEMORY LOADED] Loaded existing memory for {_character} from {mostRecent}");
                MainFile.Logger.Info($"[AutoSlay/LLM] Loaded existing memory for {_character}");
            }
        }
        catch (Exception ex)
        {
            MainFile.Logger.Info($"[AutoSlay/LLM] No existing memory found for {_character}: {ex.Message}");
        }
    }

    public int MessageCount => _history.Count;

    /// <summary>Archive current run's conversation and clear history for next run.</summary>
    public void ResetForNewRun()
    {
        if (_history.Count > 0)
        {
            _allRuns.Add(new List<Message>(_history));
            SaveHistory();
        }
        _history.Clear();
        LogToFile($"\n=== New Run — History Cleared ({DateTime.Now:HH:mm:ss}) ===");
        var fullMemory = Memory;
        if (!string.IsNullOrEmpty(fullMemory))
            LogToFile($"[MEMORY CARRIED OVER]\n{fullMemory}");
        MainFile.Logger.Info($"[AutoSlay/LLM] Conversation reset for new run ({(fullMemory.Length > 0 ? "with" : "no")} memory)");
    }

    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private void SaveHistory()
    {
        try
        {
            var data = SerializeAllRuns();
            File.WriteAllText(_historyPath, JsonSerializer.Serialize(data, _jsonOpts));
            MainFile.Logger.Info($"[AutoSlay/LLM] History saved ({_allRuns.Count} runs) to {_historyPath}");
        }
        catch (Exception ex)
        {
            MainFile.Logger.Info($"[AutoSlay/LLM] Failed to save history: {ex.Message}");
        }
    }

    private void SaveLive()
    {
        try
        {
            var data = SerializeAllRuns();
            // Append current (in-progress) run
            data.Add(SerializeRun(_history));
            File.WriteAllText(_historyPath, JsonSerializer.Serialize(data, _jsonOpts));
        }
        catch { /* ignore — live updates are best-effort */ }
    }

    private List<object> SerializeAllRuns()
    {
        var data = new List<object>();
        foreach (var run in _allRuns)
            data.Add(SerializeRun(run));
        return data;
    }

    private static object SerializeRun(List<Message> messages)
    {
        var msgs = new List<object>();
        foreach (var msg in messages)
        {
            msgs.Add(new
            {
                role = msg.role,
                content = msg.content,
                thinking = msg.thinking,
                context = msg.context,
                timestamp = msg.timestamp
            });
        }
        return new { messages = msgs };
    }

    /// <summary>
    /// Save reflection/lessons as memory for future runs.
    /// The LLM should organize its response into two sections:
    /// ## GLOBAL (shared across all characters): enemy tactics, map strategies, event knowledge
    /// ## CHARACTER {_character} (specific to this character): deck archetypes, card/relic evaluations
    /// </summary>
    public void SaveMemory(string text)
    {
        // Parse the response to separate global and character-specific memories
        ParseAndSaveLayeredMemory(text);
    }
    
    /// <summary>
    /// Parse LLM response and save to appropriate memory tier.
    /// Expects format with ## GLOBAL and ## CHARACTER sections.
    /// </summary>
    private void ParseAndSaveLayeredMemory(string text)
    {
        var globalSection = ExtractSection(text, "GLOBAL", "CHARACTER");
        var characterSection = ExtractSection(text, $"CHARACTER {_character}", null) 
                             ?? ExtractSection(text, "CHARACTER", null);
        
        // If no sections found, treat entire text as character-specific
        if (string.IsNullOrEmpty(globalSection) && string.IsNullOrEmpty(characterSection))
        {
            characterSection = text;
        }
        
        // Save global memory
        if (!string.IsNullOrEmpty(globalSection))
        {
            SaveGlobalMemory(globalSection);
        }
        
        // Save character memory
        if (!string.IsNullOrEmpty(characterSection))
        {
            SaveCharacterMemory(characterSection);
        }
    }
    
    private string? ExtractSection(string text, string sectionName, string? nextSectionName)
    {
        var pattern = $"##\\s*{sectionName}";
        var match = System.Text.RegularExpressions.Regex.Match(text, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!match.Success)
            return null;
            
        int start = match.Index + match.Length;
        int end = text.Length;
        
        if (!string.IsNullOrEmpty(nextSectionName))
        {
            var nextPattern = $"##\\s*{nextSectionName}";
            var nextMatch = System.Text.RegularExpressions.Regex.Match(text, nextPattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (nextMatch.Success && nextMatch.Index > start)
            {
                end = nextMatch.Index;
            }
        }
        
        return text.Substring(start, end - start).Trim();
    }
    
    private void SaveGlobalMemory(string text)
    {
        _globalMemory = text;
        try
        {
            var header = $"# LLM Global Memory (All Characters)\nUpdated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\nSession: {_sessionTimestamp}\nCharacter: {_character}\n\n";
            File.WriteAllText(_globalMemoryPath, header + text + "\n");
            LogToFile($"[GLOBAL MEMORY SAVED]\n{text}");
            MainFile.Logger.Info($"[AutoSlay/LLM] Global memory saved");
        }
        catch (Exception ex)
        {
            MainFile.Logger.Info($"[AutoSlay/LLM] Failed to save global memory: {ex.Message}");
        }
    }
    
    private void SaveCharacterMemory(string text)
    {
        _characterMemory = text;
        try
        {
            var header = $"# LLM Character Memory: {_character}\nUpdated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\nSession: {_sessionTimestamp}\n\n";
            File.WriteAllText(_memoryPath, header + text + "\n");
            LogToFile($"[CHARACTER MEMORY SAVED FOR {_character}]\n{text}");
            MainFile.Logger.Info($"[AutoSlay/LLM] Character memory saved for {_character}");
        }
        catch (Exception ex)
        {
            MainFile.Logger.Info($"[AutoSlay/LLM] Failed to save character memory: {ex.Message}");
        }
    }

    private string? _currentContext;

    // 退避重试配置
    private const int MaxRetries = 3;
    private const int InitialRetryDelayMs = 2000;
    
    public async Task<string> SendAsync(string userMessage, string? context = null)
    {
        _currentContext = context;
        
        // 检查消息长度，如果太长则截断
        var truncatedMessage = TruncateIfTooLong(userMessage, 8000);
        if (truncatedMessage.Length != userMessage.Length)
        {
            MainFile.Logger.Info($"[AutoSlay/LLM] Message truncated from {userMessage.Length} to {truncatedMessage.Length} chars");
        }
        
        _history.Add(new Message("user", truncatedMessage, context: context, timestamp: DateTime.Now.ToString("o")));
        SaveLive();

        // Build messages array: system (with memory) + history
        var messages = new List<object>
        {
            new { role = "system", content = BuildSystemPrompt() }
        };
        foreach (var msg in _history)
            messages.Add(new { role = msg.role, content = msg.content });

        var requestBody = new Dictionary<string, object>
        {
            ["model"] = _model,
            ["messages"] = messages,
            ["stream"] = true,
        };
        ApplyReasoningParams(requestBody);

        var json = JsonSerializer.Serialize(requestBody);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        LogToFile($"\n--- Turn {_history.Count / 2} ({DateTime.Now:HH:mm:ss}) ---");
        LogToFile($"[USER]\n{truncatedMessage}");
        MainFile.Logger.Info($"[AutoSlay/LLM] Sending request ({_history.Count} messages, ~{truncatedMessage.Length} chars)...");

        // 退避重试逻辑
        int retryCount = 0;
        int retryDelay = InitialRetryDelayMs;
        
        while (true)
        {
            try
            {
                var response = await _http.SendAsync(
                    new HttpRequestMessage(HttpMethod.Post, _endpoint) { Content = content },
                    HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);

                if (response.IsSuccessStatusCode)
                {
                    return await ProcessResponseAsync(response).ConfigureAwait(false);
                }

                // 处理错误
                var errorBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                var isRateLimit = response.StatusCode == System.Net.HttpStatusCode.TooManyRequests ||
                                  errorBody.Contains("engine_overloaded") ||
                                  errorBody.Contains("too_many_requests");
                
                if (isRateLimit && retryCount < MaxRetries)
                {
                    retryCount++;
                    MainFile.Logger.Info($"[AutoSlay/LLM] Rate limited, retrying in {retryDelay}ms... (attempt {retryCount}/{MaxRetries})");
                    LogToFile($"[RETRY] Rate limited, waiting {retryDelay}ms...");
                    await Task.Delay(retryDelay).ConfigureAwait(false);
                    retryDelay *= 2; // 指数退避
                    continue;
                }
                
                MainFile.Logger.Info($"[AutoSlay/LLM] API error {response.StatusCode}: {errorBody}");
                LogToFile($"[ERROR] {response.StatusCode}: {errorBody}");
                _history.RemoveAt(_history.Count - 1);
                throw new Exception($"LLM API error: {response.StatusCode}");
            }
            catch (TaskCanceledException) when (retryCount < MaxRetries)
            {
                retryCount++;
                MainFile.Logger.Info($"[AutoSlay/LLM] Request timeout, retrying in {retryDelay}ms... (attempt {retryCount}/{MaxRetries})");
                await Task.Delay(retryDelay).ConfigureAwait(false);
                retryDelay *= 2;
            }
        }
    }
    
    /// <summary>
    /// 如果消息太长则截断，保留关键信息
    /// </summary>
    private string TruncateIfTooLong(string message, int maxLength)
    {
        if (message.Length <= maxLength) return message;
        
        // 尝试找到最后一个完整的段落来截断
        var truncateIndex = message.LastIndexOf("\n\n", maxLength - 200);
        if (truncateIndex < maxLength / 2) truncateIndex = maxLength - 200;
        
        var truncated = message.Substring(0, truncateIndex);
        return truncated + $"\n\n...[MESSAGE TRUNCATED: {message.Length - truncateIndex} chars omitted]";
    }
    
    private async Task<string> ProcessResponseAsync(HttpResponseMessage response)
    {

        // Read SSE stream on background thread to avoid blocking the game
        var assistantMessage = new StringBuilder();
        var thinkingContent = new StringBuilder();
        using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        using var reader = new System.IO.StreamReader(stream);
        while (!reader.EndOfStream)
        {
            var line = await reader.ReadLineAsync().ConfigureAwait(false);
            if (line == null) break;
            if (!line.StartsWith("data: ")) continue;
            var data = line.Substring(6).Trim();
            if (data == "[DONE]") break;

            try
            {
                var chunk = JsonSerializer.Deserialize<JsonElement>(data);
                if (!chunk.TryGetProperty("choices", out var choices)) continue;
                foreach (var choice in choices.EnumerateArray())
                {
                    if (!choice.TryGetProperty("delta", out var delta)) continue;

                    // Reasoning content (thinking)
                    if (delta.TryGetProperty("reasoning", out var rp) && rp.ValueKind == JsonValueKind.String)
                        thinkingContent.Append(rp.GetString());
                    else if (delta.TryGetProperty("reasoning_content", out var rc) && rc.ValueKind == JsonValueKind.String)
                        thinkingContent.Append(rc.GetString());

                    // Main content
                    if (delta.TryGetProperty("content", out var cp) && cp.ValueKind == JsonValueKind.String)
                        assistantMessage.Append(cp.GetString());
                }
            }
            catch { /* skip malformed chunks */ }
        }

        var result = assistantMessage.ToString();
        var thinking = thinkingContent.ToString();
        _history.Add(new Message("assistant", result,
            thinking: string.IsNullOrEmpty(thinking) ? null : thinking,
            context: _currentContext,
            timestamp: DateTime.Now.ToString("o")));
        SaveLive();

        if (!string.IsNullOrEmpty(thinking))
            LogToFile($"[THINKING]\n{thinking}");
        LogToFile($"[ASSISTANT]\n{result}");
        MainFile.Logger.Info($"[AutoSlay/LLM] Response: {result.Replace("\n", " | ")}");
        return result;
    }

    private void ApplyReasoningParams(Dictionary<string, object> body)
    {
        if (_thinking)
        {
            body["reasoning"] = new { max_tokens = _thinkingBudget };
            // Claude needs Anthropic provider routing (Bedrock doesn't support thinking)
            var m = _model.ToLowerInvariant();
            if (m.Contains("claude") || m.Contains("anthropic"))
                body["provider"] = new { order = new[] { "Anthropic" } };
        }
        else
        {
            body["reasoning"] = new { enabled = false };
        }
    }

    private string BuildSystemPrompt()
    {
        var prompt = PromptStrings.Get("SystemPrompt");
        
        // Add character context to help LLM understand which archetypes to pursue
        if (_character != "UNKNOWN")
        {
            prompt += $"\n\n=== CURRENT CHARACTER ===\nYou are playing as {_character}. Adapt your strategy to this character's strengths and typical archetypes.";
        }
        
        // Add map/route planning strategy guide
        prompt += "\n\n" + PromptStrings.Get("MapStrategyGuide");
        
        // Add two-tier memory: global (shared) + character-specific
        if (!string.IsNullOrEmpty(_globalMemory) || !string.IsNullOrEmpty(_characterMemory))
        {
            prompt += "\n\n=== YOUR MEMORY FILE ===\n";
            
            if (!string.IsNullOrEmpty(_globalMemory))
            {
                prompt += "\n[GLOBAL KNOWLEDGE - Applies to all characters]\n" + _globalMemory;
            }
            
            if (!string.IsNullOrEmpty(_characterMemory))
            {
                prompt += $"\n[{_character} SPECIFIC]\n" + _characterMemory;
            }
        }
        
        return prompt;
    }

    private void LogToFile(string text)
    {
        try { File.AppendAllText(_logPath, text + "\n"); }
        catch { /* ignore file write errors */ }
    }

    public record Message(string role, string content, string? thinking = null, string? context = null, string? timestamp = null);
}
