using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace STS2_AIPlay.Llm;

/// <summary>
/// 结构化记忆管理器 - 管理JSON格式的可机器读取的记忆
/// </summary>
public class StructuredMemoryManager
{
    private readonly string _baseDir;
    private StructuredGlobalMemory? _globalMemoryCache;
    private readonly Dictionary<string, StructuredCharacterMemory> _characterMemoryCache = new();
    private readonly object _lock = new();
    
    public string StructuredMemoryDir => Path.Combine(_baseDir, "memories", "structured");
    public string DecisionsDir => Path.Combine(_baseDir, "decisions");
    
    public StructuredMemoryManager(string? baseDir = null)
    {
        _baseDir = baseDir ?? Path.GetDirectoryName(
            System.Reflection.Assembly.GetExecutingAssembly().Location) ?? ".";
        
        Directory.CreateDirectory(StructuredMemoryDir);
        Directory.CreateDirectory(DecisionsDir);
    }
    
    // ==================== 全局记忆 ====================
    
    public StructuredGlobalMemory GetGlobalMemory()
    {
        lock (_lock)
        {
            if (_globalMemoryCache != null)
                return _globalMemoryCache;
            
            var path = Path.Combine(StructuredMemoryDir, "global_memory.json");
            if (!File.Exists(path))
            {
                _globalMemoryCache = new StructuredGlobalMemory();
                SaveGlobalMemory(_globalMemoryCache);
                return _globalMemoryCache;
            }
            
            try
            {
                var json = File.ReadAllText(path);
                _globalMemoryCache = JsonSerializer.Deserialize<StructuredGlobalMemory>(json, GetJsonOptions());
                if (_globalMemoryCache == null)
                    _globalMemoryCache = new StructuredGlobalMemory();
            }
            catch (Exception ex)
            {
                MainFile.Logger.Info($"[StructuredMemory] 加载全局记忆失败: {ex.Message}");
                _globalMemoryCache = new StructuredGlobalMemory();
            }
            
            return _globalMemoryCache;
        }
    }
    
    public void SaveGlobalMemory(StructuredGlobalMemory memory)
    {
        lock (_lock)
        {
            memory.LastUpdated = DateTime.Now;
            var path = Path.Combine(StructuredMemoryDir, "global_memory.json");
            
            try
            {
                var json = JsonSerializer.Serialize(memory, GetJsonOptions());
                File.WriteAllText(path, json);
                _globalMemoryCache = memory;
                MainFile.Logger.Info($"[StructuredMemory] 全局记忆已保存");
            }
            catch (Exception ex)
            {
                MainFile.Logger.Info($"[StructuredMemory] 保存全局记忆失败: {ex.Message}");
            }
        }
    }
    
    // ==================== 角色记忆 ====================
    
    public StructuredCharacterMemory GetCharacterMemory(string character)
    {
        var key = character.ToUpperInvariant();
        lock (_lock)
        {
            if (_characterMemoryCache.TryGetValue(key, out var cached))
                return cached;
            
            var path = Path.Combine(StructuredMemoryDir, $"{key}_memory.json");
            if (!File.Exists(path))
            {
                var newMemory = new StructuredCharacterMemory { Character = key };
                SaveCharacterMemory(key, newMemory);
                _characterMemoryCache[key] = newMemory;
                return newMemory;
            }
            
            try
            {
                var json = File.ReadAllText(path);
                var memory = JsonSerializer.Deserialize<StructuredCharacterMemory>(json, GetJsonOptions());
                if (memory == null)
                    memory = new StructuredCharacterMemory { Character = key };
                
                _characterMemoryCache[key] = memory;
                return memory;
            }
            catch (Exception ex)
            {
                MainFile.Logger.Info($"[StructuredMemory] 加载角色记忆失败: {ex.Message}");
                var fallback = new StructuredCharacterMemory { Character = key };
                _characterMemoryCache[key] = fallback;
                return fallback;
            }
        }
    }
    
    public void SaveCharacterMemory(string character, StructuredCharacterMemory memory)
    {
        var key = character.ToUpperInvariant();
        lock (_lock)
        {
            memory.LastUpdated = DateTime.Now;
            var path = Path.Combine(StructuredMemoryDir, $"{key}_memory.json");
            
            try
            {
                var json = JsonSerializer.Serialize(memory, GetJsonOptions());
                File.WriteAllText(path, json);
                _characterMemoryCache[key] = memory;
                MainFile.Logger.Info($"[StructuredMemory] {key} 记忆已保存");
            }
            catch (Exception ex)
            {
                MainFile.Logger.Info($"[StructuredMemory] 保存角色记忆失败: {ex.Message}");
            }
        }
    }
    
    // ==================== 更新方法 ====================
    
    /// <summary>
    /// 更新敌人知识
    /// </summary>
    public void UpdateEnemyKnowledge(string enemyId, string enemyName, bool won, int hpLost, int floor)
    {
        var memory = GetGlobalMemory();
        
        if (!memory.EnemyKnowledge.TryGetValue(enemyId, out var knowledge))
        {
            knowledge = new EnemyKnowledge 
            { 
                EnemyId = enemyId, 
                EnemyName = enemyName,
                LastEncountered = DateTime.Now
            };
            memory.EnemyKnowledge[enemyId] = knowledge;
        }
        
        knowledge.Stats.Encounters++;
        if (won)
            knowledge.Stats.Wins++;
        
        // 更新平均血量损失
        var totalHpLost = knowledge.Stats.AvgHpLost * (knowledge.Stats.Encounters - 1) + hpLost;
        knowledge.Stats.AvgHpLost = totalHpLost / knowledge.Stats.Encounters;
        
        // 更新最佳表现
        if (won && floor > knowledge.Stats.BestPerformanceFloor)
            knowledge.Stats.BestPerformanceFloor = floor;
        
        // 记录死亡
        if (!won)
        {
            if (!knowledge.Stats.DeathCountByFloor.ContainsKey($"Floor_{floor}"))
                knowledge.Stats.DeathCountByFloor[$"Floor_{floor}"] = 0;
            knowledge.Stats.DeathCountByFloor[$"Floor_{floor}"]++;
        }
        
        knowledge.LastEncountered = DateTime.Now;
        SaveGlobalMemory(memory);
    }
    
    /// <summary>
    /// 更新卡牌评价
    /// </summary>
    public void UpdateCardEvaluation(string character, string cardId, bool picked, bool won, int floor, string? insight = null)
    {
        var memory = GetCharacterMemory(character);
        
        if (!memory.CardEvaluations.TryGetValue(cardId, out var evaluation))
        {
            evaluation = new StructuredCardEvaluation { CardId = cardId, CardName = cardId };
            memory.CardEvaluations[cardId] = evaluation;
        }
        
        if (picked)
        {
            evaluation.PickHistory.TimesPicked++;
            if (won)
                evaluation.PickHistory.WinsWhenPicked++;
            
            // 更新平均层数
            var totalFloor = evaluation.PickHistory.AvgFloorWhenPicked * (evaluation.PickHistory.TimesPicked - 1) + floor;
            evaluation.PickHistory.AvgFloorWhenPicked = totalFloor / evaluation.PickHistory.TimesPicked;
        }
        else
        {
            evaluation.PickHistory.TimesSkipped++;
            var totalFloor = evaluation.PickHistory.AvgFloorWhenSkipped * (evaluation.PickHistory.TimesSkipped - 1) + floor;
            evaluation.PickHistory.AvgFloorWhenSkipped = totalFloor / evaluation.PickHistory.TimesSkipped;
        }
        
        // 更新洞察（如果有新的）
        if (!string.IsNullOrEmpty(insight) && string.IsNullOrEmpty(evaluation.KeyInsight))
        {
            evaluation.KeyInsight = insight;
        }
        
        SaveCharacterMemory(character, memory);
    }
    
    /// <summary>
    /// 更新流派表现
    /// </summary>
    public void UpdateArchetypePerformance(string character, string archetypeId, string archetypeName, bool won, int floor, List<string> keyCards)
    {
        var memory = GetCharacterMemory(character);
        
        if (!memory.ArchetypePerformance.TryGetValue(archetypeId, out var performance))
        {
            performance = new ArchetypePerformance 
            { 
                ArchetypeId = archetypeId, 
                ArchetypeName = archetypeName,
                KeyCards = keyCards
            };
            memory.ArchetypePerformance[archetypeId] = performance;
        }
        
        performance.Attempts++;
        if (won)
            performance.Wins++;
        
        // 更新平均层数
        var totalFloor = performance.AvgFloor * (performance.Attempts - 1) + floor;
        performance.AvgFloor = totalFloor / performance.Attempts;
        
        if (floor > performance.BestFloor)
            performance.BestFloor = floor;
        
        // 更新信心（胜率越稳定信心越高）
        if (performance.Attempts >= 5)
        {
            performance.CurrentConfidence = performance.WinRate;
        }
        
        SaveCharacterMemory(character, memory);
    }
    
    /// <summary>
    /// 添加通用教训
    /// </summary>
    public void AddLesson(string lesson, float confidence, int runId, string category = "general", bool isGlobal = true)
    {
        if (isGlobal)
        {
            var memory = GetGlobalMemory();
            
            // 检查是否已有相似教训
            var similar = memory.UniversalLessons.FirstOrDefault(l => 
                l.Lesson.Contains(lesson) || lesson.Contains(l.Lesson));
            
            if (similar != null)
            {
                similar.Confidence = Math.Min(1.0f, similar.Confidence + 0.1f);
                similar.SourceRuns.Add(runId);
                if (similar.Confidence > 0.7f)
                    similar.Verified = true;
            }
            else
            {
                memory.UniversalLessons.Add(new KeyLesson
                {
                    Lesson = lesson,
                    Confidence = confidence,
                    SourceRuns = new List<int> { runId },
                    Category = category,
                    Verified = confidence > 0.7f
                });
            }
            
            SaveGlobalMemory(memory);
        }
    }
    
    // ==================== 生成自然语言记忆 ====================
    
    /// <summary>
    /// 将结构化记忆转换为自然语言，供LLM使用
    /// </summary>
    public string GenerateNaturalLanguageMemory(string? character = null)
    {
        var sb = new System.Text.StringBuilder();
        
        // 全局记忆
        var global = GetGlobalMemory();
        if (global.EnemyKnowledge.Any() || global.UniversalLessons.Any())
        {
            sb.AppendLine("## GLOBAL KNOWLEDGE (All Characters)");
            sb.AppendLine();
            
            // 敌人知识
            if (global.EnemyKnowledge.Any())
            {
                sb.AppendLine("### Enemy Tactics");
                foreach (var enemy in global.EnemyKnowledge.Values.OrderByDescending(e => e.Stats.WinRate).Take(5))
                {
                    sb.AppendLine($"- {enemy.EnemyName}: Win rate {enemy.Stats.WinRate:P0} ({enemy.Stats.Wins}/{enemy.Stats.Encounters})");
                    if (enemy.KeyInsights.Any())
                        sb.AppendLine($"  Insight: {enemy.KeyInsights.First()}");
                }
                sb.AppendLine();
            }
            
            // 通用教训
            if (global.UniversalLessons.Any())
            {
                sb.AppendLine("### Key Lessons");
                foreach (var lesson in global.UniversalLessons.Where(l => l.Verified || l.Confidence > 0.6f).Take(5))
                {
                    var verified = lesson.Verified ? "[VERIFIED] " : "";
                    sb.AppendLine($"- {verified}{lesson.Lesson} (confidence: {lesson.Confidence:P0})");
                }
                sb.AppendLine();
            }
        }
        
        // 角色记忆
        if (!string.IsNullOrEmpty(character))
        {
            var charMemory = GetCharacterMemory(character);
            sb.AppendLine($"## {character} SPECIFIC KNOWLEDGE");
            sb.AppendLine();
            
            // 流派表现
            if (charMemory.ArchetypePerformance.Any())
            {
                sb.AppendLine("### Archetype Performance");
                foreach (var arch in charMemory.ArchetypePerformance.Values.OrderByDescending(a => a.WinRate).Take(3))
                {
                    sb.AppendLine($"- {arch.ArchetypeName}: {arch.WinRate:P0} win rate ({arch.Wins}/{arch.Attempts}), avg floor {arch.AvgFloor:F0}");
                }
                sb.AppendLine();
            }
            
            // 卡牌评价
            if (charMemory.CardEvaluations.Any())
            {
                sb.AppendLine("### Card Evaluations");
                var topCards = charMemory.CardEvaluations.Values
                    .Where(c => c.PickHistory.TimesPicked >= 3)
                    .OrderByDescending(c => c.PickHistory.WinRateWhenPicked)
                    .Take(5);
                
                foreach (var card in topCards)
                {
                    sb.AppendLine($"- {card.CardName}: {card.PickHistory.WinRateWhenPicked:P0} win rate when picked");
                    if (!string.IsNullOrEmpty(card.KeyInsight))
                        sb.AppendLine($"  Note: {card.KeyInsight}");
                }
                sb.AppendLine();
            }
            
            // 发现的组合
            if (charMemory.DiscoveredCombos.Any())
            {
                sb.AppendLine("### Discovered Combos");
                foreach (var combo in charMemory.DiscoveredCombos.Where(c => c.Verified || c.SuccessRate > 0.5f).Take(3))
                {
                    sb.AppendLine($"- {combo.Name}: {combo.Cards} - {combo.Description}");
                }
                sb.AppendLine();
            }
        }
        
        return sb.ToString();
    }
    
    // ==================== 辅助方法 ====================
    
    private static JsonSerializerOptions GetJsonOptions()
    {
        return new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };
    }
    
    public void ClearCache()
    {
        lock (_lock)
        {
            _globalMemoryCache = null;
            _characterMemoryCache.Clear();
        }
    }
}
