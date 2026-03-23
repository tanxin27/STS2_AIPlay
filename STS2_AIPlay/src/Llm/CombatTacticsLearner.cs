using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace STS2_AIPlay.Llm;

/// <summary>
/// 战斗战术学习器 - 记录、分析、学习战斗策略
/// </summary>
public class CombatTacticsLearner
{
    private readonly string _dataDir;
    private CombatTacticsLibrary? _libraryCache;
    private CombatTurnRecord? _currentTurn;
    private List<CombatTurnRecord> _currentCombatTurns = new();
    private string? _currentEnemyId;
    private CombatTurnAnalyzer _analyzer = new();
    private TurnAnalysis? _currentTurnAnalysis; // Phase 2: 当前回合的分析
    
    public CombatTacticsLearner(string? baseDir = null)
    {
        _dataDir = baseDir ?? Path.Combine(
            Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) ?? ".",
            "memories", "tactics");
        Directory.CreateDirectory(_dataDir);
    }
    
    // ==================== 战斗记录 ====================
    
    /// <summary>
    /// 开始一场新战斗
    /// </summary>
    public void StartCombat(string enemyId, string enemyName, bool isElite, bool isBoss)
    {
        _currentEnemyId = enemyId;
        _currentCombatTurns.Clear();
        MainFile.Logger.Info($"[CombatTactics] 开始记录战斗: {enemyName} ({enemyId})");
    }
    
    /// <summary>
    /// 记录回合开始状态
    /// </summary>
    public void RecordTurnStart(
        int turnNumber,
        int playerHp, int playerMaxHp, int playerBlock, int playerEnergy,
        List<string> hand,
        string enemyId, string enemyName, int enemyHp, int enemyMaxHp, int enemyBlock,
        string intent, int intentDamage,
        List<string> deckSnapshot)
    {
        _currentTurn = new CombatTurnRecord
        {
            EnemyId = enemyId,
            EnemyName = enemyName,
            TurnNumber = turnNumber,
            StartState = new TurnStartState
            {
                PlayerHp = playerHp,
                PlayerMaxHp = playerMaxHp,
                PlayerBlock = playerBlock,
                PlayerEnergy = playerEnergy,
                Hand = hand.ToList(),
                DeckSnapshot = deckSnapshot.Take(10).ToList(), // 只记录前10张简化
                Enemy = new EnemyState
                {
                    EnemyId = enemyId,
                    EnemyName = enemyName,
                    Hp = enemyHp,
                    MaxHp = enemyMaxHp,
                    Block = enemyBlock,
                    Intent = intent,
                    IntentDamage = intentDamage
                }
            }
        };
    }
    
    /// <summary>
    /// 记录玩家决策
    /// </summary>
    public void RecordPlayerDecision(
        List<string> cardsPlayed,
        List<string> potionsUsed,
        bool endedTurn,
        int expectedDamage,
        int expectedBlock,
        string llmReasoning)
    {
        if (_currentTurn == null) return;
        
        _currentTurn.Decision = new PlayerDecision
        {
            CardsPlayed = cardsPlayed,
            PotionsUsed = potionsUsed,
            EndedTurn = endedTurn,
            DamageDealt = expectedDamage,
            BlockGained = expectedBlock,
            LlmReasoning = llmReasoning.Length > 200 ? llmReasoning.Substring(0, 200) + "..." : llmReasoning
        };
    }
    
    /// <summary>
    /// 记录回合结果
    /// </summary>
    public void RecordTurnOutcome(
        int playerHpAfter,
        int playerBlockAfter,
        int enemyHpAfter,
        int actualDamageDealt,
        int damageTaken,
        bool enemyDefeated)
    {
        if (_currentTurn == null) return;
        
        _currentTurn.Outcome = new TurnOutcome
        {
            PlayerHpAfter = playerHpAfter,
            PlayerBlockAfter = playerBlockAfter,
            EnemyHpAfter = enemyHpAfter,
            ActualDamageDealt = actualDamageDealt,
            DamageTaken = damageTaken,
            EnemyDefeated = enemyDefeated
        };
        
        // 分析决策质量
        _currentTurn.Quality = AnalyzeDecisionQuality(_currentTurn);
        
        // Phase 2: 进行深度回合分析
        _currentTurnAnalysis = _analyzer.AnalyzeOptimalStrategy(_currentTurn);
        
        // 保存到当前战斗记录
        _currentCombatTurns.Add(_currentTurn);
        _currentTurn = null;
    }
    
    /// <summary>
    /// 战斗结束，保存并学习
    /// </summary>
    public void EndCombat(bool playerWon, int totalTurns)
    {
        if (string.IsNullOrEmpty(_currentEnemyId) || !_currentCombatTurns.Any()) return;
        
        try
        {
            // 保存战斗记录
            SaveCombatRecord();
            
            // 更新敌人档案
            UpdateEnemyProfile(playerWon, totalTurns);
            
            // 提取教训
            ExtractLessons();
            
            // Phase 2: 保存详细的回合分析
            SaveTurnAnalyses();
            
            MainFile.Logger.Info($"[CombatTactics] 战斗学习完成: {_currentEnemyId}, 胜利: {playerWon}, 回合数: {totalTurns}");
        }
        catch (Exception ex)
        {
            MainFile.Logger.Info($"[CombatTactics] 战斗学习失败: {ex.Message}");
        }
        
        _currentCombatTurns.Clear();
        _currentEnemyId = null;
    }
    
    // ==================== 决策质量分析 ====================
    
    private DecisionQuality AnalyzeDecisionQuality(CombatTurnRecord turn)
    {
        var quality = new DecisionQuality();
        var mistakes = new List<string>();
        var goodPlays = new List<string>();
        
        var start = turn.StartState;
        var decision = turn.Decision;
        var outcome = turn.Outcome;
        
        // 1. 检查是否应该防御
        if (start.Enemy.IntentDamage > 0)
        {
            var expectedDamage = Math.Max(0, start.Enemy.IntentDamage - start.PlayerBlock);
            var blockNeeded = Math.Max(0, start.Enemy.IntentDamage - start.PlayerBlock);
            
            // 如果受到的伤害 > 5 且没有尽力防御
            if (outcome.DamageTaken > 5 && decision.BlockGained < blockNeeded * 0.7f)
            {
                // 检查手牌是否有防御牌但未使用
                var defenseCards = new[] { "Defend", "Backflip", "Dodge", "Blur", "Footwork" };
                var hadDefense = start.Hand.Any(c => defenseCards.Any(d => c.Contains(d)));
                
                if (hadDefense)
                {
                    mistakes.Add($"Enemy telegraphed {start.Enemy.IntentDamage} damage but didn't defend enough");
                    quality.ShouldHaveBlocked = true;
                }
            }
            
            // 如果防御了但没有受到伤害（格挡溢出）
            if (decision.BlockGained > start.Enemy.IntentDamage && outcome.DamageTaken == 0)
            {
                var overflow = decision.BlockGained - start.Enemy.IntentDamage;
                if (overflow > 5)
                {
                    mistakes.Add($"Wasted {overflow} block - could have attacked instead");
                    quality.WastedBlock = true;
                }
            }
        }
        
        // 2. 检查是否 missed 斩杀
        if (!outcome.EnemyDefeated && outcome.EnemyHpAfter < 10)
        {
            var couldHaveDealt = decision.DamageDealt + start.PlayerEnergy * 5; // 估算剩余能量能造成的伤害
            if (couldHaveDealt >= outcome.EnemyHpAfter + decision.DamageDealt)
            {
                mistakes.Add("Missed lethal - could have killed enemy this turn");
                quality.MissedLethal = true;
            }
        }
        
        // 3. 检查药水使用
        if (start.PlayerHp < 20 && !decision.PotionsUsed.Any() && outcome.DamageTaken > 0)
        {
            mistakes.Add("Low HP but didn't use potion when taking damage");
            quality.BadPotionTiming = true;
        }
        
        // 4. 好决策
        if (outcome.DamageTaken == 0 && start.Enemy.IntentDamage > 10)
        {
            goodPlays.Add("Perfect defense against heavy attack");
        }
        
        if (outcome.EnemyDefeated && turn.TurnNumber <= 3)
        {
            goodPlays.Add("Fast kill - good damage output");
        }
        
        // 计算总分
        int score = 100;
        score -= mistakes.Count * 15;
        score += goodPlays.Count * 10;
        quality.Score = Math.Clamp(score, 0, 100);
        quality.WasOptimal = score >= 85 && !mistakes.Any();
        quality.Mistakes = mistakes;
        quality.GoodPlays = goodPlays;
        
        return quality;
    }
    
    // ==================== 学习更新 ====================
    
    private void UpdateEnemyProfile(bool playerWon, int totalTurns)
    {
        var library = GetLibrary();
        var enemyId = _currentEnemyId!;
        
        if (!library.EnemyProfiles.TryGetValue(enemyId, out var profile))
        {
            profile = new EnemyTacticsProfile
            {
                EnemyId = enemyId,
                EnemyName = _currentCombatTurns.First().EnemyName
            };
            library.EnemyProfiles[enemyId] = profile;
        }
        
        profile.LastUpdated = DateTime.Now;
        profile.Stats.Encounters++;
        if (playerWon) profile.Stats.Wins++;
        
        // 更新意图策略统计
        foreach (var turn in _currentCombatTurns.Where(t => t.Quality != null))
        {
            var intent = turn.StartState.Enemy.Intent;
            if (string.IsNullOrEmpty(intent)) continue;
            
            // 简化意图分类
            var intentType = ClassifyIntent(intent);
            
            if (!profile.IntentStrategies.TryGetValue(intentType, out var strategy))
            {
                strategy = new IntentStrategy { IntentType = intentType };
                profile.IntentStrategies[intentType] = strategy;
            }
            
            // 判断这次采用的策略类型
            var approach = ClassifyPlayerApproach(turn);
            var approachStats = approach switch
            {
                "aggressive" => strategy.AggressiveApproach,
                "defensive" => strategy.DefensiveApproach,
                _ => strategy.BalancedApproach
            };
            
            approachStats.Attempts++;
            if (turn.Quality!.Score >= 80)
            {
                approachStats.SuccessCount++;
            }
            approachStats.AvgHpLost = 
                (approachStats.AvgHpLost * (approachStats.Attempts - 1) + turn.Outcome.DamageTaken) 
                / approachStats.Attempts;
        }
        
        // 更新推荐策略
        foreach (var strategy in profile.IntentStrategies.Values)
        {
            var best = new[]
            {
                ("aggressive", strategy.AggressiveApproach.SuccessRate),
                ("defensive", strategy.DefensiveApproach.SuccessRate),
                ("balanced", strategy.BalancedApproach.SuccessRate)
            }.OrderByDescending(x => x.Item2).First();
            
            strategy.RecommendedApproach = best.Item1;
        }
        
        SaveLibrary(library);
    }
    
    private void ExtractLessons()
    {
        var library = GetLibrary();
        
        foreach (var turn in _currentCombatTurns.Where(t => t.Quality?.Mistakes.Any() == true))
        {
            foreach (var mistake in turn.Quality!.Mistakes)
            {
                // 检查是否已记录类似错误
                var existing = library.CommonMistakes.FirstOrDefault(m => 
                    m.Pattern.Contains(mistake) || mistake.Contains(m.Pattern));
                
                if (existing != null)
                {
                    existing.OccurrenceCount++;
                }
                else
                {
                    library.CommonMistakes.Add(new CommonMistake
                    {
                        Pattern = mistake,
                        Consequence = $"Took {turn.Outcome.DamageTaken} damage",
                        BetterAlternative = "Block more or use potion",
                        OccurrenceCount = 1
                    });
                }
            }
        }
        
        // 保留最常见的20个错误
        library.CommonMistakes = library.CommonMistakes
            .OrderByDescending(m => m.OccurrenceCount)
            .Take(20)
            .ToList();
        
        SaveLibrary(library);
    }
    
    // ==================== 战术提示生成 ====================
    
    /// <summary>
    /// 为当前战斗生成战术提示 (Phase 1 + Phase 2)
    /// </summary>
    public string GenerateTacticsHint(string enemyId, string enemyName, string currentIntent, int turnNumber)
    {
        var sb = new System.Text.StringBuilder();
        
        // Phase 1: 历史经验提示
        sb.Append(GenerateHistoricalHint(enemyId, enemyName, currentIntent, turnNumber));
        
        sb.AppendLine();
        
        // Phase 2: 当前回合详细分析
        if (_currentTurnAnalysis != null)
        {
            sb.AppendLine(GenerateTurnSpecificHint());
        }
        
        return sb.ToString();
    }
    
    /// <summary>
    /// Phase 1: 基于历史经验的提示
    /// </summary>
    private string GenerateHistoricalHint(string enemyId, string enemyName, string currentIntent, int turnNumber)
    {
        var library = GetLibrary();
        var sb = new System.Text.StringBuilder();
        
        sb.AppendLine("=== 战术提示 ===");
        sb.AppendLine();
        
        // 敌人特定提示
        if (library.EnemyProfiles.TryGetValue(enemyId, out var profile))
        {
            sb.AppendLine($"你对 {enemyName} 的战斗记录: {profile.Stats.Wins}/{profile.Stats.Encounters} 胜利");
            
            // 当前意图的建议
            var intentType = ClassifyIntent(currentIntent);
            if (profile.IntentStrategies.TryGetValue(intentType, out var strategy) && strategy.SampleSize >= 3)
            {
                sb.AppendLine($"面对 {intentType} 意图时，推荐策略: {strategy.RecommendedApproach}");
                if (strategy.RecommendedApproach == "defensive")
                    sb.AppendLine("→ 优先防御，下回合再输出");
                else if (strategy.RecommendedApproach == "aggressive")
                    sb.AppendLine("→ 全力输出， rush down");
            }
            
            // 验证过的洞察
            var verifiedInsights = profile.Insights.Where(i => i.Verified).Take(2);
            foreach (var insight in verifiedInsights)
            {
                sb.AppendLine($"💡 {insight.Insight}");
            }
            
            sb.AppendLine();
        }
        
        // 通用教训
        var relevantLessons = library.UniversalLessons
            .Where(l => l.Verified && currentIntent.Contains(l.Category))
            .Take(2);
        
        if (relevantLessons.Any())
        {
            sb.AppendLine("通用战术:");
            foreach (var lesson in relevantLessons)
            {
                sb.AppendLine($"- {lesson.Lesson}");
            }
            sb.AppendLine();
        }
        
        // 常见错误提醒
        var commonMistake = library.CommonMistakes
            .Where(m => m.OccurrenceCount >= 3)
            .OrderByDescending(m => m.OccurrenceCount)
            .FirstOrDefault();
        
        if (commonMistake != null && turnNumber <= 2)
        {
            sb.AppendLine($"⚠️ 常见错误提醒: {commonMistake.Pattern}");
        }
        
        return sb.ToString();
    }
    
    /// <summary>
    /// Phase 2: 生成当前回合的具体操作建议
    /// </summary>
    private string GenerateTurnSpecificHint()
    {
        if (_currentTurnAnalysis == null) return "";
        
        var sb = new System.Text.StringBuilder();
        var analysis = _currentTurnAnalysis;
        
        sb.AppendLine("=== 当前回合策略分析 ===");
        sb.AppendLine();
        
        // 局势评估
        sb.AppendLine($"【局势】紧急度: {analysis.Situation.Urgency}, 威胁: {analysis.Situation.ThreatLevel}");
        if (analysis.Situation.CanLethal)
        {
            sb.AppendLine("🎯 【可以斩杀！】优先最大化伤害");
        }
        sb.AppendLine($"【目标】{analysis.RecommendedStrategy.PrimaryGoal}");
        sb.AppendLine();
        
        // 关键行动
        sb.AppendLine("【建议行动】");
        foreach (var action in analysis.RecommendedStrategy.KeyActions.Take(3))
        {
            sb.AppendLine($"• {action}");
        }
        sb.AppendLine();
        
        // 能量分配
        sb.AppendLine($"【能量分配】{analysis.RecommendedStrategy.EnergyAllocation}");
        sb.AppendLine();
        
        // 关键卡牌
        if (analysis.KeyCards.Any())
        {
            sb.AppendLine("【关键卡牌】(按优先级排序)");
            foreach (var card in analysis.KeyCards.Where(c => c.Importance == "critical" || c.Importance == "high").Take(4))
            {
                var icon = card.Importance == "critical" ? "🔴" : "🟡";
                var note = !string.IsNullOrEmpty(card.Note) ? $" - {card.Note}" : "";
                sb.AppendLine($"{icon} [{card.Type.ToUpper()}] {card.CardName}{note}");
            }
            sb.AppendLine();
        }
        
        // 警告
        if (analysis.CommonMistakes.Any())
        {
            sb.AppendLine("⚠️ 【注意】");
            foreach (var warning in analysis.CommonMistakes.Take(2))
            {
                sb.AppendLine($"• {warning.Pattern}");
                sb.AppendLine($"  → 风险: {warning.Risk}");
                sb.AppendLine($"  → 建议: {warning.BetterPlay}");
            }
            sb.AppendLine();
        }
        
        // 结果预测
        sb.AppendLine("【预期结果】");
        var outcome = analysis.ExpectedOutcome;
        if (outcome.WillKillEnemy)
            sb.AppendLine($"✓ 可以击杀敌人 (剩余HP: 0)");
        else
            sb.AppendLine($"• 敌人剩余HP: {outcome.EnemyHpRemaining}");
        
        if (outcome.WillDie)
            sb.AppendLine($"❌ 警告：会受到致命伤害！");
        else if (outcome.DamageTaken > 0)
            sb.AppendLine($"• 会受到 {outcome.DamageTaken} 伤害");
        else
            sb.AppendLine($"• 完美防御，不受伤害");
        
        sb.AppendLine($"【评估】{GetEvaluationText(outcome.Evaluation)}");
        
        return sb.ToString();
    }
    
    private string GetEvaluationText(string evaluation)
    {
        return evaluation switch
        {
            "optimal" => "最优 ✓",
            "good" => "良好",
            "acceptable" => "可接受",
            "risky" => "有风险 ⚠️",
            _ => "未知"
        };
    }
    
    // ==================== 辅助方法 ====================
    
    private string ClassifyIntent(string intent)
    {
        if (intent.Contains("Attack") || intent.Contains("伤害")) return "Attack";
        if (intent.Contains("Defend") || intent.Contains("Block") || intent.Contains("格挡")) return "Defend";
        if (intent.Contains("Buff") || intent.Contains("强化")) return "Buff";
        if (intent.Contains("Debuff") || intent.Contains("虚弱")) return "Debuff";
        return "Unknown";
    }
    
    private string ClassifyPlayerApproach(CombatTurnRecord turn)
    {
        var attackCount = turn.Decision.CardsPlayed.Count(c => 
            !c.Contains("Defend") && !c.Contains("Block") && !c.Contains("Blur"));
        var defenseCount = turn.Decision.CardsPlayed.Count(c => 
            c.Contains("Defend") || c.Contains("Block") || c.Contains("Blur"));
        
        if (attackCount >= 2 && defenseCount == 0) return "aggressive";
        if (defenseCount >= 2 && attackCount <= 1) return "defensive";
        return "balanced";
    }
    
    private CombatTacticsLibrary GetLibrary()
    {
        if (_libraryCache != null) return _libraryCache;
        
        var path = Path.Combine(_dataDir, "combat_tactics_library.json");
        if (!File.Exists(path))
        {
            _libraryCache = new CombatTacticsLibrary();
            return _libraryCache;
        }
        
        try
        {
            var json = File.ReadAllText(path);
            _libraryCache = JsonSerializer.Deserialize<CombatTacticsLibrary>(json, GetJsonOptions());
        }
        catch
        {
            _libraryCache = new CombatTacticsLibrary();
        }
        
        return _libraryCache ?? new CombatTacticsLibrary();
    }
    
    private void SaveLibrary(CombatTacticsLibrary library)
    {
        try
        {
            var path = Path.Combine(_dataDir, "combat_tactics_library.json");
            var json = JsonSerializer.Serialize(library, GetJsonOptions());
            File.WriteAllText(path, json);
            _libraryCache = library;
        }
        catch (Exception ex)
        {
            MainFile.Logger.Info($"[CombatTactics] 保存战术库失败: {ex.Message}");
        }
    }
    
    private void SaveCombatRecord()
    {
        try
        {
            var fileName = $"combat_{_currentEnemyId}_{DateTime.Now:yyyyMMdd_HHmmss}.json";
            var path = Path.Combine(_dataDir, "records", fileName);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            
            var json = JsonSerializer.Serialize(_currentCombatTurns, GetJsonOptions());
            File.WriteAllText(path, json);
        }
        catch { }
    }
    
    /// <summary>
    /// Phase 2: 保存详细的回合分析
    /// </summary>
    private void SaveTurnAnalyses()
    {
        try
        {
            var analyses = new List<object>();
            foreach (var turn in _currentCombatTurns)
            {
                var analysis = _analyzer.AnalyzeOptimalStrategy(turn);
                var postHoc = _analyzer.PostHocAnalysis(turn, analysis);
                
                analyses.Add(new
                {
                    TurnId = turn.Id,
                    TurnNumber = turn.TurnNumber,
                    EnemyId = turn.EnemyId,
                    Situation = analysis.Situation,
                    RecommendedStrategy = analysis.RecommendedStrategy,
                    ActualStrategy = postHoc.ActualStrategy,
                    Deviation = postHoc.Deviation,
                    Lesson = postHoc.Lesson,
                    WasOptimal = postHoc.WasOptimal
                });
            }
            
            var fileName = $"analyses_{_currentEnemyId}_{DateTime.Now:yyyyMMdd_HHmmss}.json";
            var path = Path.Combine(_dataDir, "analyses", fileName);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            
            var json = JsonSerializer.Serialize(analyses, GetJsonOptions());
            File.WriteAllText(path, json);
        }
        catch (Exception ex)
        {
            MainFile.Logger.Info($"[CombatTactics] 保存回合分析失败: {ex.Message}");
        }
    }
    
    private static JsonSerializerOptions GetJsonOptions()
    {
        return new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
    }
}
