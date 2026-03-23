using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace STS2_AIPlay.Llm;

/// <summary>
/// Tracks route choices and outcomes to help LLM learn optimal pathing strategies.
/// Records: chosen path, HP/gold changes, encounter results, and final outcome.
/// </summary>
public static class RouteHistoryLogger
{
    private static readonly List<RouteChoice> _choices = new();
    private static int _currentAct = 1;
    private static int _startHp = 0;
    private static int _startGold = 0;
    
    public static void Reset(int act)
    {
        _choices.Clear();
        _currentAct = act;
        _startHp = 0;
        _startGold = 0;
    }
    
    /// <summary>
    /// Saves current route history and resets for a new run
    /// </summary>
    public static void SaveAndReset()
    {
        if (_choices.Count > 0)
        {
            var asmDir = System.Reflection.Assembly.GetExecutingAssembly().Location;
            var baseDir = Path.GetDirectoryName(asmDir) ?? ".";
            SaveToFile(baseDir);
        }
        Reset(1);
    }
    
    public static void RecordStartState(int hp, int gold)
    {
        _startHp = hp;
        _startGold = gold;
    }
    
    /// <summary>
    /// Records a route choice made by the LLM
    /// </summary>
    public static void RecordChoice(
        int fromRow, int fromCol,
        int toRow, int toCol,
        string roomType,
        int currentHp, int maxHp, int gold,
        string? reasoning = null)
    {
        var choice = new RouteChoice
        {
            Timestamp = DateTime.Now.ToString("HH:mm:ss"),
            Act = _currentAct,
            FromRow = fromRow,
            FromCol = fromCol,
            ToRow = toRow,
            ToCol = toCol,
            RoomType = roomType,
            CurrentHp = currentHp,
            MaxHp = maxHp,
            Gold = gold,
            Reasoning = reasoning
        };
        
        _choices.Add(choice);
        MainFile.Logger.Info($"[RouteHistory] Recorded: {roomType} at ({toRow},{toCol}) with HP {currentHp}/{maxHp}");
    }
    
    /// <summary>
    /// Records the outcome of entering a room
    /// </summary>
    public static void RecordOutcome(
        int row, int col,
        string outcome,  // "victory", "defeat", "event_result", etc.
        int hpDelta,
        int goldDelta,
        string? notes = null)
    {
        var choice = _choices.LastOrDefault(c => c.ToRow == row && c.ToCol == col);
        if (choice != null)
        {
            choice.Outcome = outcome;
            choice.HpDelta = hpDelta;
            choice.GoldDelta = goldDelta;
            choice.OutcomeNotes = notes;
        }
    }
    
    /// <summary>
    /// Gets a summary of route history for the current act
    /// </summary>
    public static string GetRouteSummary()
    {
        if (_choices.Count == 0)
            return "No route choices recorded yet.";
        
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"=== Route History (Act {_currentAct}) ===");
        sb.AppendLine($"Total choices: {_choices.Count}");
        sb.AppendLine();
        
        foreach (var choice in _choices)
        {
            sb.AppendLine($"Row {choice.ToRow}: {choice.RoomType} " +
                         $"| HP: {choice.CurrentHp}/{choice.MaxHp} " +
                         $"| Gold: {choice.Gold}g");
            
            if (!string.IsNullOrEmpty(choice.Outcome))
            {
                var outcomeSymbol = choice.Outcome == "victory" ? "✓" : 
                                   choice.Outcome == "defeat" ? "✗" : "?";
                sb.AppendLine($"   → Outcome: {outcomeSymbol} HP change: {choice.HpDelta:+#;-#;0}, Gold: {choice.GoldDelta:+#;-#;0}g");
            }
            
            if (!string.IsNullOrEmpty(choice.Reasoning))
            {
                sb.AppendLine($"   → Reasoning: {choice.Reasoning}");
            }
        }
        
        // Calculate statistics
        var eliteCount = _choices.Count(c => c.RoomType == "Elite");
        var restCount = _choices.Count(c => c.RoomType == "Rest");
        var shopCount = _choices.Count(c => c.RoomType == "Shop");
        var totalHpChange = _choices.Sum(c => c.HpDelta);
        
        sb.AppendLine();
        sb.AppendLine($"Stats: {eliteCount} Elites, {restCount} Rests, {shopCount} Shops");
        sb.AppendLine($"Total HP change from route: {totalHpChange:+#;-#;0}");
        
        return sb.ToString();
    }
    
    /// <summary>
    /// Gets route analysis for reflection prompt
    /// </summary>
    public static string GetRouteAnalysisForReflection()
    {
        if (_choices.Count == 0)
            return "";
        
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("=== Route Decisions Analysis ===");
        
        // Analyze good vs bad choices
        var goodChoices = _choices.Where(c => c.HpDelta > 0 || (c.RoomType == "Elite" && c.Outcome == "victory")).ToList();
        var badChoices = _choices.Where(c => c.HpDelta < -20 || c.Outcome == "defeat").ToList();
        
        if (goodChoices.Any())
        {
            sb.AppendLine("\nGood route decisions:");
            foreach (var c in goodChoices.Take(3))
            {
                sb.AppendLine($"- Chose {c.RoomType} at row {c.ToRow} (HP: {c.CurrentHp})");
            }
        }
        
        if (badChoices.Any())
        {
            sb.AppendLine("\nPoor route decisions:");
            foreach (var c in badChoices.Take(3))
            {
                sb.AppendLine($"- Chose {c.RoomType} at row {c.ToRow} with HP {c.CurrentHp}, lost {c.HpDelta} HP");
            }
        }
        
        // Pattern analysis
        var eliteDeaths = _choices.Count(c => c.RoomType == "Elite" && c.Outcome == "defeat");
        if (eliteDeaths > 0)
        {
            sb.AppendLine($"\nPattern: Died to {eliteDeaths} Elite(s). Consider avoiding Elites when HP < 60%");
        }
        
        return sb.ToString();
    }
    
    /// <summary>
    /// 获取最近 N 个路线选择（用于实时决策参考）
    /// </summary>
    public static List<RouteChoice> GetRecentChoices(int count = 3)
    {
        return _choices.TakeLast(count).ToList();
    }
    
    /// <summary>
    /// 获取路线统计信息（用于多样化路线建议）
    /// </summary>
    public static RouteStats GetRouteStats()
    {
        var stats = new RouteStats
        {
            TotalChoices = _choices.Count,
            EliteCount = _choices.Count(c => c.RoomType == "Elite"),
            MonsterCount = _choices.Count(c => c.RoomType == "Monster"),
            RestCount = _choices.Count(c => c.RoomType == "Rest"),
            ShopCount = _choices.Count(c => c.RoomType == "Shop"),
            EventCount = _choices.Count(c => c.RoomType == "Event")
        };
        
        // 计算连续战斗（非精英）
        int maxConsecutiveFights = 0;
        int currentConsecutiveFights = 0;
        int maxConsecutiveElites = 0;
        int currentConsecutiveElites = 0;
        
        foreach (var choice in _choices)
        {
            if (choice.RoomType == "Monster")
            {
                currentConsecutiveFights++;
                maxConsecutiveFights = Math.Max(maxConsecutiveFights, currentConsecutiveFights);
                currentConsecutiveElites = 0;
            }
            else if (choice.RoomType == "Elite")
            {
                currentConsecutiveElites++;
                maxConsecutiveElites = Math.Max(maxConsecutiveElites, currentConsecutiveElites);
                currentConsecutiveFights = 0;
            }
            else
            {
                // Shop, Rest, Event break the chain
                currentConsecutiveFights = 0;
                currentConsecutiveElites = 0;
            }
        }
        
        stats.MaxConsecutiveFights = maxConsecutiveFights;
        stats.MaxConsecutiveElites = maxConsecutiveElites;
        stats.CurrentConsecutiveFights = currentConsecutiveFights;
        stats.CurrentConsecutiveElites = currentConsecutiveElites;
        
        return stats;
    }
    
    /// <summary>
    /// 路线统计信息
    /// </summary>
    public class RouteStats
    {
        public int TotalChoices { get; set; }
        public int EliteCount { get; set; }
        public int MonsterCount { get; set; }
        public int RestCount { get; set; }
        public int ShopCount { get; set; }
        public int EventCount { get; set; }
        public int MaxConsecutiveFights { get; set; }
        public int MaxConsecutiveElites { get; set; }
        public int CurrentConsecutiveFights { get; set; }
        public int CurrentConsecutiveElites { get; set; }
        
        public float FightRatio => TotalChoices > 0 ? (float)(EliteCount + MonsterCount) / TotalChoices : 0;
        public bool NeedsShop => TotalChoices >= 5 && ShopCount == 0;
        public bool NeedsRest => TotalChoices >= 4 && RestCount == 0;
    }
    
    /// <summary>
    /// 获取当前运行的连续路线风险评估
    /// </summary>
    public static string GetContinuousRiskAssessment()
    {
        if (_choices.Count == 0) return "";
        
        var recent = _choices.TakeLast(3).ToList();
        var sb = new System.Text.StringBuilder();
        
        // 检查连续精英
        var eliteCount = recent.Count(c => c.RoomType == "Elite");
        if (eliteCount >= 2)
        {
            var lastElite = recent.LastOrDefault(c => c.RoomType == "Elite");
            if (lastElite?.HpDelta < -20)
            {
                sb.AppendLine("⚠️ PATTERN ALERT: Just fought 2+ Elites and lost significant HP!");
                sb.AppendLine("   RECOMMENDATION: REST immediately, avoid next Elite!");
            }
        }
        
        // 检查最近选择的结果
        var lastChoice = recent.LastOrDefault();
        if (lastChoice?.RoomType == "Elite" && lastChoice?.HpDelta < -25)
        {
            sb.AppendLine("⚠️ WARNING: Last Elite cost heavy HP loss. Play conservatively!");
        }
        
        // 检查是否需要恢复
        var lowHpChoices = recent.Where(c => c.CurrentHp * 100 / c.MaxHp < 40).Count();
        if (lowHpChoices >= 2)
        {
            sb.AppendLine("🚨 URGENT: HP has been low for multiple rooms. Prioritize REST!");
        }
        
        return sb.ToString();
    }
    
    public static void SaveToFile(string basePath)
    {
        try
        {
            var path = Path.Combine(basePath, $"route_history_act{_currentAct}_{DateTime.Now:yyyyMMdd_HHmmss}.json");
            var json = JsonSerializer.Serialize(_choices, new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            });
            File.WriteAllText(path, json);
            MainFile.Logger.Info($"[RouteHistory] Saved to {path}");
        }
        catch (Exception ex)
        {
            MainFile.Logger.Info($"[RouteHistory] Failed to save: {ex.Message}");
        }
    }
    
    public class RouteChoice
    {
        [JsonPropertyName("timestamp")]
        public string Timestamp { get; set; } = "";
        
        [JsonPropertyName("act")]
        public int Act { get; set; }
        
        [JsonPropertyName("from_row")]
        public int FromRow { get; set; }
        
        [JsonPropertyName("from_col")]
        public int FromCol { get; set; }
        
        [JsonPropertyName("to_row")]
        public int ToRow { get; set; }
        
        [JsonPropertyName("to_col")]
        public int ToCol { get; set; }
        
        [JsonPropertyName("room_type")]
        public string RoomType { get; set; } = "";
        
        [JsonPropertyName("current_hp")]
        public int CurrentHp { get; set; }
        
        [JsonPropertyName("max_hp")]
        public int MaxHp { get; set; }
        
        [JsonPropertyName("gold")]
        public int Gold { get; set; }
        
        [JsonPropertyName("reasoning")]
        public string? Reasoning { get; set; }
        
        [JsonPropertyName("outcome")]
        public string? Outcome { get; set; }
        
        [JsonPropertyName("hp_delta")]
        public int HpDelta { get; set; }
        
        [JsonPropertyName("gold_delta")]
        public int GoldDelta { get; set; }
        
        [JsonPropertyName("outcome_notes")]
        public string? OutcomeNotes { get; set; }
    }
}
