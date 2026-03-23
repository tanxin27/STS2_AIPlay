using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace TokenSpire2.Llm;

/// <summary>
/// 高级战术学习器 - Phase 3: 药水、连招、能量管理
/// </summary>
public class AdvancedTacticsLearner
{
    private readonly string _dataDir;
    private PotionStrategyLibrary? _potionLibraryCache;
    private ComboLibrary? _comboLibraryCache;
    private EnergyManagementProfile? _energyProfileCache;
    private Dictionary<string, CompleteEnemyProfile> _completeProfilesCache = new();
    
    // 当前战斗追踪
    private List<PotionUsageRecord> _currentPotionsUsed = new();
    private List<CardComboRecord> _currentCombosExecuted = new();
    private List<EnergyUsageRecord> _currentEnergyDecisions = new();
    private List<string> _cardsPlayedThisTurn = new();
    private int _currentTurnEnergyUsed = 0;
    private int _currentTurnEnergyAvailable = 0;
    
    public AdvancedTacticsLearner(string? baseDir = null)
    {
        _dataDir = baseDir ?? Path.Combine(
            Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) ?? ".",
            "memories", "advanced_tactics");
        Directory.CreateDirectory(_dataDir);
    }
    
    // ==================== 药水使用学习 ====================
    
    /// <summary>
    /// 记录药水使用
    /// </summary>
    public void RecordPotionUsage(
        string potionId,
        string potionName,
        PotionUsageContext context)
    {
        var record = new PotionUsageRecord
        {
            PotionId = potionId,
            PotionName = potionName,
            Context = context
        };
        _currentPotionsUsed.Add(record);
        MainFile.Logger.Info($"[AdvancedTactics] 记录药水使用: {potionName} at {context.HpPercent:P0} HP");
    }
    
    /// <summary>
    /// 更新药水使用结果
    /// </summary>
    public void UpdatePotionOutcome(
        string potionId,
        bool survived,
        int hpAfter,
        bool wonCombat,
        bool wouldHaveDiedWithout)
    {
        var record = _currentPotionsUsed.LastOrDefault(p => p.PotionId == potionId);
        if (record == null) return;
        
        record.Outcome = new PotionUsageOutcome
        {
            SurvivedTurn = survived,
            HpAfter = hpAfter,
            WonCombat = wonCombat,
            WouldHaveDiedWithoutPotion = wouldHaveDiedWithout
        };
        
        // 评估使用质量
        record.Rating = EvaluatePotionUsage(record);
        
        // 更新策略库
        UpdatePotionStrategy(record);
    }
    
    private PotionUsageRating EvaluatePotionUsage(PotionUsageRecord record)
    {
        var ctx = record.Context;
        var outcome = record.Outcome;
        
        // 救了一命
        if (outcome.WouldHaveDiedWithoutPotion && outcome.SurvivedTurn)
            return PotionUsageRating.Excellent;
        
        // 满血用治疗药水 - 浪费
        if (ctx.HpPercent > 0.9f && record.PotionName.Contains("Heal"))
            return PotionUsageRating.Wasted;
        
        // 赢了这场战斗
        if (outcome.WonCombat && ctx.HpPercent < 0.5f)
            return PotionUsageRating.Good;
        
        // 用了但还是死了 - 浪费（除非是必死局面下的尝试）
        if (!outcome.SurvivedTurn && !outcome.WouldHaveDiedWithoutPotion)
            return PotionUsageRating.Wasted;
        
        // 低血时使用合理
        if (ctx.HpPercent < 0.4f)
            return PotionUsageRating.Good;
        
        return PotionUsageRating.Neutral;
    }
    
    private void UpdatePotionStrategy(PotionUsageRecord record)
    {
        var lib = GetPotionLibrary();
        
        if (!lib.Strategies.TryGetValue(record.PotionId, out var strategy))
        {
            strategy = new Phase3PotionStrategy
            {
                PotionId = record.PotionId,
                PotionName = record.PotionName
            };
            lib.Strategies[record.PotionId] = strategy;
        }
        
        strategy.TimesUsed++;
        
        if (record.Outcome?.WonCombat == true)
            strategy.TimesHelpedWin++;
        if (record.Outcome?.WouldHaveDiedWithoutPotion == true)
            strategy.TimesPreventedDeath++;
        if (record.Rating == PotionUsageRating.Wasted || record.Rating == PotionUsageRating.Poor)
            strategy.TimesWasted++;
        
        // 推断使用规则
        InferPotionRules(strategy, record);
        
        SavePotionLibrary(lib);
    }
    
    private void InferPotionRules(Phase3PotionStrategy strategy, PotionUsageRecord record)
    {
        // 如果多次在HP<30%时使用且效果好，记录规则
        if (record.Context.HpPercent < 0.3f && record.Rating <= PotionUsageRating.Good)
        {
            if (string.IsNullOrEmpty(strategy.HpThresholdRule))
            {
                strategy.HpThresholdRule = "Use when HP < 30%";
                strategy.BestUseCases.Add("Critical HP situations (< 30%)");
            }
        }
        
        // 记录意图规则
        if (record.Context.IncomingDamage > record.Context.PlayerHp * 0.5f && record.Rating == PotionUsageRating.Excellent)
        {
            strategy.IntentRule = "Use when facing heavy attack";
            strategy.BestUseCases.Add("Before heavy enemy attacks");
        }
    }
    
    /// <summary>
    /// 获取药水使用建议
    /// </summary>
    public string? GetPotionAdvice(string potionId, PotionUsageContext context)
    {
        var lib = GetPotionLibrary();
        if (!lib.Strategies.TryGetValue(potionId, out var strategy)) return null;
        
        // 检查是否应该使用
        if (context.HpPercent > 0.8f && strategy.TimesWasted > strategy.TimesPreventedDeath)
        {
            return $"⚠️ 历史数据显示满血使用{strategy.PotionName}通常是浪费（{strategy.TimesWasted}次浪费 vs {strategy.TimesPreventedDeath}次救命）";
        }
        
        if (context.IncomingDamage > context.PlayerHp * 0.5f && strategy.TimesPreventedDeath > 0)
        {
            return $"💡 推荐现在使用！历史显示在重击前使用可救命（{strategy.TimesPreventedDeath}次成功）";
        }
        
        return null;
    }
    
    // ==================== 连招学习 ====================
    
    /// <summary>
    /// 记录卡牌打出，检测连招
    /// </summary>
    public void RecordCardPlayed(string cardId, int turnNumber, string enemyId)
    {
        _cardsPlayedThisTurn.Add(cardId);
        
        // 检测2-3卡连招
        if (_cardsPlayedThisTurn.Count >= 2)
        {
            DetectCombo(turnNumber, enemyId);
        }
    }
    
    /// <summary>
    /// 回合结束，结算连招效果
    /// </summary>
    public void EndComboTracking(
        int turnNumber,
        int totalDamage,
        int totalBlock,
        bool killedEnemy,
        int hpLost)
    {
        foreach (var combo in _currentCombosExecuted.Where(c => c.TurnNumber == turnNumber))
        {
            combo.TotalDamage = totalDamage;
            combo.TotalBlock = totalBlock;
            combo.KilledEnemy = killedEnemy;
            combo.HpLost = hpLost;
            
            // 评估连招
            combo.Rating = EvaluateCombo(combo);
        }
        
        _cardsPlayedThisTurn.Clear();
    }
    
    private void DetectCombo(int turnNumber, string enemyId)
    {
        // 检测最后2张牌的组合
        if (_cardsPlayedThisTurn.Count >= 2)
        {
            var last2 = _cardsPlayedThisTurn.TakeLast(2).ToList();
            var combo = new CardComboRecord
            {
                CardSequence = last2,
                TurnNumber = turnNumber,
                EnemyId = enemyId
            };
            _currentCombosExecuted.Add(combo);
        }
        
        // 检测最后3张牌的组合
        if (_cardsPlayedThisTurn.Count >= 3)
        {
            var last3 = _cardsPlayedThisTurn.TakeLast(3).ToList();
            var combo = new CardComboRecord
            {
                CardSequence = last3,
                TurnNumber = turnNumber,
                EnemyId = enemyId
            };
            _currentCombosExecuted.Add(combo);
        }
    }
    
    private ComboRating EvaluateCombo(CardComboRecord combo)
    {
        if (combo.KilledEnemy) return ComboRating.Lethal;
        if (combo.HpLost == 0 && combo.TotalDamage > 20) return ComboRating.Excellent;
        if (combo.HpLost < 10 && combo.TotalDamage > 15) return ComboRating.Good;
        if (combo.HpLost > 20) return ComboRating.Poor;
        return ComboRating.Average;
    }
    
    /// <summary>
    /// 学习连招模式
    /// </summary>
    public void LearnCombos(string enemyId, string archetype)
    {
        var lib = GetComboLibrary();
        
        // 找出好评连招
        var goodCombos = _currentCombosExecuted.Where(c => 
            c.Rating == ComboRating.Excellent || c.Rating == ComboRating.Lethal).ToList();
        
        foreach (var combo in goodCombos)
        {
            // 检查是否已记录
            var existing = lib.Combos.FirstOrDefault(c => 
                c.CardSequence.SequenceEqual(combo.CardSequence) &&
                (c.EnemyId == enemyId || c.EnemyId == null));
            
            if (existing != null)
            {
                existing.TimesExecuted++;
                existing.TimesSucceeded++;
                existing.LastUsedAt = DateTime.Now;
            }
            else
            {
                // 创建新连招记录
                lib.Combos.Add(new Phase3DiscoveredCombo
                {
                    CardSequence = combo.CardSequence,
                    EnemyId = enemyId,
                    Archetype = archetype,
                    ExpectedOutcome = combo.KilledEnemy ? "Kill enemy" : "High damage, low risk",
                    AvgDamage = combo.TotalDamage,
                    AvgBlock = combo.TotalBlock,
                    TimesExecuted = 1,
                    TimesSucceeded = 1,
                    DiscoveredAt = DateTime.Now,
                    LastUsedAt = DateTime.Now
                });
            }
            
            // 更新卡牌协同度
            UpdateCardSynergy(lib, combo.CardSequence, combo.Rating);
        }
        
        SaveComboLibrary(lib);
    }
    
    private void UpdateCardSynergy(ComboLibrary lib, List<string> cards, ComboRating rating)
    {
        for (int i = 0; i < cards.Count - 1; i++)
        {
            var cardA = cards[i];
            var cardB = cards[i + 1];
            var key = $"{cardA}#{cardB}";
            
            if (!lib.CardSynergies.TryGetValue(key, out var synergy))
            {
                synergy = new CardSynergy { CardA = cardA, CardB = cardB };
                lib.CardSynergies[key] = synergy;
            }
            
            synergy.TimesPlayedTogether++;
            if (rating == ComboRating.Excellent || rating == ComboRating.Lethal)
            {
                synergy.TimesWonTogether++;
            }
            
            // 计算协同分数
            synergy.SynergyScore = (float)synergy.TimesWonTogether / synergy.TimesPlayedTogether * 2 - 1;
        }
    }
    
    /// <summary>
    /// 获取推荐的连招
    /// </summary>
    public List<Phase3DiscoveredCombo> GetRecommendedCombos(List<string> hand, string? enemyId = null)
    {
        var lib = GetComboLibrary();
        
        var possibleCombos = lib.Combos.Where(c =>
        {
            // 手牌中是否有连招所需的所有卡
            return c.CardSequence.All(card => hand.Any(h => h.Contains(card) || card.Contains(h)));
        }).ToList();
        
        // 优先返回对特定敌人有效的连招，然后是通用连招
        var enemySpecific = possibleCombos.Where(c => c.EnemyId == enemyId).OrderByDescending(c => c.SuccessRate);
        var general = possibleCombos.Where(c => string.IsNullOrEmpty(c.EnemyId)).OrderByDescending(c => c.SuccessRate);
        
        return enemySpecific.Concat(general).Take(3).ToList();
    }
    
    // ==================== 能量管理学习 ====================
    
    /// <summary>
    /// 记录能量决策
    /// </summary>
    public void RecordEnergyDecision(
        int turnNumber,
        int energyAvailable,
        int energyUsed,
        List<string> cardsPlayed,
        List<string> cardsInHand,
        string enemyId)
    {
        var wasted = energyAvailable - energyUsed;
        
        // 检查是否有可打出的牌但没打
        var skippedCards = cardsInHand.Where(c => 
            !cardsPlayed.Contains(c) && 
            !IsHighCostCard(c) && // 假设不是高费牌
            !IsSituationalCard(c) // 假设不是 situational 牌
        ).ToList();
        
        var record = new EnergyUsageRecord
        {
            TurnNumber = turnNumber,
            EnemyId = enemyId,
            EnergyAvailable = energyAvailable,
            EnergyUsed = energyUsed,
            EnergyWasted = wasted > 0 && skippedCards.Any() ? wasted : 0,
            CardsPlayed = cardsPlayed,
            CardsSkipped = skippedCards
        };
        
        _currentEnergyDecisions.Add(record);
    }
    
    /// <summary>
    /// 评估能量决策是否正确
    /// </summary>
    public void EvaluateEnergyDecision(int turnNumber, bool nextTurnWasBetter, string reasoning)
    {
        var record = _currentEnergyDecisions.LastOrDefault(e => e.TurnNumber == turnNumber);
        if (record == null) return;
        
        record.GoodDecision = nextTurnWasBetter;
        record.Reasoning = reasoning;
        
        // 学习规则
        if (record.EnergyWasted > 0 && !nextTurnWasBetter)
        {
            LearnEnergyRule(record.TurnNumber, "spend", "Wasted energy without benefit");
        }
        else if (record.EnergyWasted > 0 && nextTurnWasBetter)
        {
            LearnEnergyRule(record.TurnNumber, "save", "Saving energy allowed better play next turn");
        }
    }
    
    private void LearnEnergyRule(int turnNumber, string strategy, string reasoning)
    {
        var profile = GetEnergyProfile();
        
        if (!profile.TurnRules.TryGetValue(turnNumber, out var rule))
        {
            rule = new TurnEnergyRule { TurnNumber = turnNumber };
            profile.TurnRules[turnNumber] = rule;
        }
        
        rule.Strategy = strategy;
        rule.Reasoning = reasoning;
        rule.AttemptCount++;
        if (strategy == "save" && reasoning.Contains("benefit"))
            rule.SuccessCount++;
        
        SaveEnergyProfile(profile);
    }
    
    /// <summary>
    /// 获取能量管理建议
    /// </summary>
    public string? GetEnergyAdvice(int turnNumber, int currentEnergy, int enemyHp, int enemyMaxHp)
    {
        var profile = GetEnergyProfile();
        
        if (profile.TurnRules.TryGetValue(turnNumber, out var rule) && rule.AttemptCount >= 3)
        {
            if (rule.Strategy == "save" && rule.SuccessRate > 0.6f)
            {
                return $"💡 历史数据显示第{turnNumber}回合留能量效果更好（{rule.SuccessRate:P0}胜率）";
            }
        }
        
        // 敌人血量低时建议 spend
        if (enemyHp < enemyMaxHp * 0.3f && currentEnergy >= 2)
        {
            return "⚡ 敌人血量低，建议用光能量争取斩杀";
        }
        
        return null;
    }
    
    // ==================== 危险回合识别 ====================
    
    /// <summary>
    /// 记录危险模式
    /// </summary>
    public void RecordDangerPattern(
        string enemyId,
        string patternName,
        List<DangerTrigger> triggers,
        string consequence,
        List<string> countermeasures)
    {
        var profile = GetCompleteEnemyProfile(enemyId);
        
        var existing = profile.DangerPatterns.FirstOrDefault(p => p.PatternName == patternName);
        if (existing != null)
        {
            foreach (var trigger in triggers)
            {
                var existingTrigger = existing.Triggers.FirstOrDefault(t => t.Type == trigger.Type);
                if (existingTrigger != null)
                    existingTrigger.OccurrenceCount++;
                else
                    existing.Triggers.Add(trigger);
            }
        }
        else
        {
            profile.DangerPatterns.Add(new DangerPattern
            {
                EnemyId = enemyId,
                PatternName = patternName,
                Triggers = triggers,
                Consequence = consequence,
                Countermeasures = countermeasures
            });
        }
        
        SaveCompleteProfile(profile);
    }
    
    /// <summary>
    /// 检查当前是否危险回合
    /// </summary>
    public List<string> CheckDangerPatterns(string enemyId, int turnNumber, int playerHp, int playerMaxHp)
    {
        var profile = GetCompleteEnemyProfile(enemyId);
        var alerts = new List<string>();
        
        foreach (var pattern in profile.DangerPatterns)
        {
            bool triggered = false;
            
            foreach (var trigger in pattern.Triggers)
            {
                switch (trigger.Type)
                {
                    case "turn_number":
                        if (int.TryParse(trigger.Condition, out var dangerTurn) && turnNumber == dangerTurn)
                            triggered = true;
                        break;
                    case "hp_percent":
                        var hpPercent = (float)playerHp / playerMaxHp;
                        if (trigger.Condition.StartsWith("<") && 
                            float.TryParse(trigger.Condition.Substring(1), out var threshold) &&
                            hpPercent < threshold)
                            triggered = true;
                        break;
                }
            }
            
            if (triggered)
            {
                var counters = string.Join(", ", pattern.Countermeasures.Take(2));
                alerts.Add($"🚨 危险！{pattern.PatternName}: {pattern.Consequence} | 应对: {counters}");
            }
        }
        
        return alerts;
    }
    
    // ==================== 数据持久化 ====================
    
    private PotionStrategyLibrary GetPotionLibrary()
    {
        if (_potionLibraryCache != null) return _potionLibraryCache;
        return LoadOrCreate<PotionStrategyLibrary>("potion_strategies.json");
    }
    
    private void SavePotionLibrary(PotionStrategyLibrary lib)
    {
        Save("potion_strategies.json", lib);
        _potionLibraryCache = lib;
    }
    
    private ComboLibrary GetComboLibrary()
    {
        if (_comboLibraryCache != null) return _comboLibraryCache;
        return LoadOrCreate<ComboLibrary>("combo_library.json");
    }
    
    private void SaveComboLibrary(ComboLibrary lib)
    {
        Save("combo_library.json", lib);
        _comboLibraryCache = lib;
    }
    
    private EnergyManagementProfile GetEnergyProfile()
    {
        if (_energyProfileCache != null) return _energyProfileCache;
        return LoadOrCreate<EnergyManagementProfile>("energy_profile.json");
    }
    
    private void SaveEnergyProfile(EnergyManagementProfile profile)
    {
        Save("energy_profile.json", profile);
        _energyProfileCache = profile;
    }
    
    private CompleteEnemyProfile GetCompleteEnemyProfile(string enemyId)
    {
        if (_completeProfilesCache.TryGetValue(enemyId, out var cached)) return cached;
        
        var profile = LoadOrCreate<CompleteEnemyProfile>($"enemy_{enemyId}.json");
        profile.EnemyId = enemyId;
        _completeProfilesCache[enemyId] = profile;
        return profile;
    }
    
    private void SaveCompleteProfile(CompleteEnemyProfile profile)
    {
        Save($"enemy_{profile.EnemyId}.json", profile);
        _completeProfilesCache[profile.EnemyId] = profile;
    }
    
    private T LoadOrCreate<T>(string fileName) where T : new()
    {
        var path = Path.Combine(_dataDir, fileName);
        if (File.Exists(path))
        {
            try
            {
                var json = File.ReadAllText(path);
                return JsonSerializer.Deserialize<T>(json) ?? new T();
            }
            catch { }
        }
        return new T();
    }
    
    private void Save<T>(string fileName, T obj)
    {
        try
        {
            var path = Path.Combine(_dataDir, fileName);
            var json = JsonSerializer.Serialize(obj, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
        }
        catch (Exception ex)
        {
            MainFile.Logger.Info($"[AdvancedTactics] 保存失败: {ex.Message}");
        }
    }
    
    private bool IsHighCostCard(string card) => card.Contains("Catalyst") || card.Contains("Bludgeon");
    private bool IsSituationalCard(string card) => card.Contains("Pommel") || card.Contains("Shrug");
    
    /// <summary>
    /// 生成Phase 3综合提示
    /// </summary>
    public AdvancedTacticsHint GenerateAdvancedHint(
        string enemyId,
        List<string> hand,
        int turnNumber,
        int playerHp,
        int playerMaxHp,
        int currentEnergy,
        int enemyHp,
        int enemyMaxHp)
    {
        var hint = new AdvancedTacticsHint();
        
        // 危险预警
        hint.DangerAlerts = CheckDangerPatterns(enemyId, turnNumber, playerHp, playerMaxHp);
        if (hint.DangerAlerts.Any())
            hint.UrgentWarning = hint.DangerAlerts.First();
        
        // 连招建议
        var combos = GetRecommendedCombos(hand, enemyId);
        if (combos.Any())
        {
            var best = combos.First();
            hint.ComboSuggestion = $"💡 推荐连招: {string.Join(" → ", best.CardSequence)} ({best.ExpectedOutcome}, {best.SuccessRate:P0}成功率)";
        }
        
        // 能量建议
        hint.EnergyTip = GetEnergyAdvice(turnNumber, currentEnergy, enemyHp, enemyMaxHp);
        
        // 优化建议
        hint.OptimizationTips = GenerateOptimizationTips(hand, currentEnergy);
        
        return hint;
    }
    
    private List<string> GenerateOptimizationTips(List<string> hand, int currentEnergy)
    {
        var tips = new List<string>();
        
        // 检查是否有抽牌卡
        var drawCards = hand.Where(c => c.Contains("Pommel") || c.Contains("Shrug") || c.Contains("Acrobatics"));
        if (drawCards.Any())
        {
            tips.Add("优先打出抽牌卡，看到更多选项");
        }
        
        // 检查能量利用
        if (currentEnergy >= 2 && hand.Count >= 4)
        {
            tips.Add("手牌较多，考虑用光能量避免弃牌");
        }
        
        return tips;
    }
    
    /// <summary>
    /// 战斗结束，清理和保存
    /// </summary>
    public void EndCombat(string enemyId, string archetype, bool won)
    {
        // 学习连招
        LearnCombos(enemyId, archetype);
        
        // 清空当前战斗记录
        _currentPotionsUsed.Clear();
        _currentCombosExecuted.Clear();
        _currentEnergyDecisions.Clear();
        _cardsPlayedThisTurn.Clear();
    }
}
