using System;
using System.Collections.Generic;
using System.Linq;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Runs;

namespace TokenSpire2.Llm;

/// <summary>
/// 学习系统 - 整合所有学习组件，在关键时间点执行学习
/// </summary>
public class LearningSystem
{
    private readonly StructuredMemoryManager _memoryManager;
    private readonly DecisionTracker _decisionTracker;
    private readonly DeathAnalyzer _deathAnalyzer;
    
    public StructuredMemoryManager MemoryManager => _memoryManager;
    public DecisionTracker DecisionTracker => _decisionTracker;
    
    public LearningSystem(string? baseDir = null)
    {
        _memoryManager = new StructuredMemoryManager(baseDir);
        _decisionTracker = new DecisionTracker(baseDir);
        _deathAnalyzer = new DeathAnalyzer();
    }
    
    /// <summary>
    /// 开始新对局
    /// </summary>
    public void StartNewRun(string runId, string character)
    {
        _decisionTracker.StartNewRun(runId, character);
        MainFile.Logger.Info($"[LearningSystem] 开始对局学习追踪: {runId}");
    }
    
    /// <summary>
    /// 记录卡牌选择
    /// </summary>
    public void RecordCardPick(
        string character,
        string cardId,
        List<string> otherOptions,
        string llmReasoning,
        int floor,
        int hp,
        int maxHp,
        List<string> currentDeck)
    {
        // 记录决策
        var snapshot = new GameSnapshot
        {
            Hp = hp,
            MaxHp = maxHp,
            Deck = currentDeck.ToList()
        };
        
        _decisionTracker.RecordDecision(
            DecisionType.CardPick,
            otherOptions.Concat(new[] { cardId }).ToList(),
            cardId,
            llmReasoning,
            snapshot,
            character);
        
        MainFile.Logger.Info($"[LearningSystem] 记录卡牌选择: {cardId} at floor {floor}");
    }
    
    /// <summary>
    /// 记录地图选择
    /// </summary>
    public void RecordPathChoice(
        string character,
        string chosenPath,
        List<string> options,
        string llmReasoning,
        int floor,
        int act,
        int hp,
        int maxHp)
    {
        var snapshot = new GameSnapshot
        {
            Hp = hp,
            MaxHp = maxHp
        };
        
        _decisionTracker.RecordDecision(
            DecisionType.PathChoice,
            options,
            chosenPath,
            llmReasoning,
            snapshot,
            character);
    }
    
    /// <summary>
    /// 战斗结束后学习
    /// </summary>
    public void LearnFromCombat(
        string character,
        string enemyId,
        string enemyName,
        bool won,
        int hpLost,
        int floor)
    {
        // 更新敌人知识
        _memoryManager.UpdateEnemyKnowledge(enemyId, enemyName, won, hpLost, floor);
        
        // 如果输了且是精英/Boss，记录为重要教训
        if (!won)
        {
            var isEliteOrBoss = IsEliteOrBoss(enemyName);
            if (isEliteOrBoss)
            {
                MainFile.Logger.Info($"[LearningSystem] 从 {enemyName} 战斗中学习 (失败)");
            }
        }
    }
    
    /// <summary>
    /// 游戏结束后的完整学习流程
    /// </summary>
    public RunSummary FinalizeRun(
        string runId,
        string character,
        bool victory,
        int floor,
        string? killedBy,
        List<string> finalDeck,
        List<string> finalRelics,
        int finalHp,
        int finalMaxHp,
        int finalGold,
        string? primaryArchetype)
    {
        // 1. 更新决策结果
        _decisionTracker.UpdateDecisionOutcomes(victory, floor);
        
        // 2. 创建对局摘要
        var summary = new RunSummary
        {
            RunId = runId,
            Character = character,
            Victory = victory,
            Floor = floor,
            KilledBy = killedBy,
            FinalDeck = finalDeck,
            FinalRelics = finalRelics,
            FinalHp = finalHp,
            FinalMaxHp = finalMaxHp,
            FinalGold = finalGold,
            PrimaryArchetype = primaryArchetype,
            Timestamp = DateTime.Now
        };
        
        // 3. 如果是失败，进行死因分析
        DeathAnalysis? deathAnalysis = null;
        if (!victory)
        {
            var decisions = _decisionTracker.GetCurrentRunDecisions();
            deathAnalysis = _deathAnalyzer.Analyze(summary, decisions, finalDeck, finalRelics);
            
            MainFile.Logger.Info($"[LearningSystem] 死因分析完成: {deathAnalysis.Summary}");
            
            // 添加教训到全局记忆
            foreach (var advice in deathAnalysis.ActionableAdvice)
            {
                _memoryManager.AddLesson(
                    advice.Advice,
                    advice.Confidence,
                    floor,
                    advice.Category,
                    isGlobal: true);
            }
        }
        
        // 4. 更新卡牌评价
        UpdateCardEvaluationsFromRun(character, victory, floor);
        
        // 5. 更新流派表现
        if (!string.IsNullOrEmpty(primaryArchetype))
        {
            _memoryManager.UpdateArchetypePerformance(
                character,
                primaryArchetype.ToLower().Replace(" ", "_"),
                primaryArchetype,
                victory,
                floor,
                finalDeck.Take(5).ToList()); // 前5张作为关键卡
        }
        
        // 6. 清理当前对局
        _decisionTracker.ClearCurrentRun();
        
        MainFile.Logger.Info($"[LearningSystem] 对局学习完成: {character}, 胜利: {victory}, 层数: {floor}");
        
        return summary;
    }
    
    /// <summary>
    /// 生成用于LLM反思的结构化数据
    /// </summary>
    public string GenerateStructuredReflectionData(
        string character,
        bool victory,
        int floor,
        string? killedBy)
    {
        var sb = new System.Text.StringBuilder();
        
        // 1. 本局决策分析
        sb.AppendLine("=== 本局决策分析 ===");
        sb.AppendLine(_decisionTracker.GenerateDecisionAnalysisForPrompt());
        sb.AppendLine();
        
        // 2. 如果是失败，添加死因分析
        if (!victory && !string.IsNullOrEmpty(killedBy))
        {
            sb.AppendLine("=== 死因分析 ===");
            sb.AppendLine($"死亡层数: {floor}");
            sb.AppendLine($"被击杀者: {killedBy}");
            sb.AppendLine();
        }
        
        // 3. 结构化记忆摘要
        sb.AppendLine("=== 更新后的结构化知识 ===");
        sb.AppendLine(_memoryManager.GenerateNaturalLanguageMemory(character));
        
        return sb.ToString();
    }
    
    /// <summary>
    /// 获取决策分析（用于游戏结束时的LLM提示）
    /// </summary>
    public string GetDecisionAnalysisForPrompt()
    {
        return _decisionTracker.GenerateDecisionAnalysisForPrompt();
    }
    
    /// <summary>
    /// 获取死因分析（如果是失败）
    /// </summary>
    public DeathAnalysis? GetDeathAnalysis(RunSummary run)
    {
        if (run.Victory) return null;
        
        var decisions = _decisionTracker.GetCurrentRunDecisions();
        return _deathAnalyzer.Analyze(run, decisions, run.FinalDeck, run.FinalRelics);
    }
    
    /// <summary>
    /// 获取死因分析文本（用于LLM提示）
    /// </summary>
    public string GetDeathAnalysisForPrompt(RunSummary run)
    {
        var analysis = GetDeathAnalysis(run);
        if (analysis == null) return "";
        return _deathAnalyzer.GenerateAnalysisForPrompt(analysis);
    }
    
    // ==================== 辅助方法 ====================
    
    private void UpdateCardEvaluationsFromRun(string character, bool victory, int floor)
    {
        // 从决策记录中汇总卡牌表现
        var cardPicks = _decisionTracker.GetDecisionsByType(DecisionType.CardPick);
        
        foreach (var pick in cardPicks)
        {
            if (string.IsNullOrEmpty(pick.Chosen)) continue;
            
            _memoryManager.UpdateCardEvaluation(
                character,
                pick.Chosen,
                picked: true,
                victory,
                floor,
                insight: pick.Outcome?.WasGoodDecision == true ? "Worked well this run" : null);
        }
    }
    
    private bool IsEliteOrBoss(string enemyName)
    {
        var elites = new[] { "GremlinNob", "Lagavulin", "Sentries", "Book of Stabbing", "Gremlin Leader", "Taskmaster", "Orb Walker" };
        var bosses = new[] { "Slime Boss", "The Guardian", "Hexaghost", "Bronze Automaton", "The Champ", "The Collector", "Awakened One", "Time Eater", "Donu", "Deca", "Corrupt Heart" };
        
        return elites.Any(e => enemyName.Contains(e)) || bosses.Any(b => enemyName.Contains(b));
    }
}
